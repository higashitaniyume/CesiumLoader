using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 基于 Unity 原生 UnityEngine.AudioSource 与 UnityEngine.AudioClip 的音频后端实现。
    /// 
    /// 采用全反射实现，不强制 SDK 编译期硬依赖 UnityEngine.AudioModule.dll，
    /// 从而完全避免在缺少该程序集的独立测试环境抛出 TypeLoadException。
    /// </summary>
    public sealed class UnityAudioBackend : IAudioBackend
    {
        private readonly object _lock = new object();
        private bool _initialized;
        private bool _available;

        private Type _audioSourceType;
        private Type _audioClipType;
        private Type _audioListenerType;

        private MethodInfo _clipCreateMethod;
        private MethodInfo _clipSetDataMethod;
        private MethodInfo _sourcePlayOneShotMethod;
        private MethodInfo _sourcePlayMethod;
        private MethodInfo _sourceStopMethod;
        private MethodInfo _sourcePauseMethod;
        private MethodInfo _sourceUnPauseMethod;
        private MethodInfo _playClipAtPointMethod;

        private PropertyInfo _sourceClipProp;
        private PropertyInfo _sourceVolumeProp;
        private PropertyInfo _sourceLoopProp;
        private PropertyInfo _sourceSpatialBlendProp;
        private PropertyInfo _sourceIsPlayingProp;

        private GameObject _hostObject;
        private Component _bgmSource;
        private Component _sfxSource;
        private Component _voiceSource;
        private readonly List<Component> _sfxPool = new List<Component>();

        private float _masterVolume = 1.0f;
        private float _bgmVolume = 1.0f;
        private float _sfxVolume = 1.0f;
        private float _voiceVolume = 1.0f;
        private float _customVolume = 1.0f;

        public bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _available;
            }
        }

        public bool IsBgmPlaying
        {
            get
            {
                EnsureInitialized();
                if (!_available || _bgmSource == null || _sourceIsPlayingProp == null) return false;
                try { return (bool)_sourceIsPlayingProp.GetValue(_bgmSource, null); }
                catch { return false; }
            }
        }

        public bool IsVoicePlaying
        {
            get
            {
                EnsureInitialized();
                if (!_available || _voiceSource == null || _sourceIsPlayingProp == null) return false;
                try { return (bool)_sourceIsPlayingProp.GetValue(_voiceSource, null); }
                catch { return false; }
            }
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;

                try
                {
                    _audioSourceType = RuntimeAssemblyService.FindType("UnityEngine.AudioSource");
                    _audioClipType = RuntimeAssemblyService.FindType("UnityEngine.AudioClip");
                    _audioListenerType = RuntimeAssemblyService.FindType("UnityEngine.AudioListener");

                    if (_audioSourceType == null || _audioClipType == null)
                    {
                        SdkLog.Warn("AUDIO", "未在当前运行时找到 UnityEngine.AudioSource 或 UnityEngine.AudioClip，原生音频功能将停用。");
                        _available = false;
                        return;
                    }

                    // AudioClip 反射
                    _clipCreateMethod = _audioClipType.GetMethod("Create", new Type[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) });
                    if (_clipCreateMethod == null)
                    {
                        foreach (var m in _audioClipType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (m.Name == "Create")
                            {
                                int len = m.GetParameters().Length;
                                if (len == 5 || len == 7)
                                {
                                    _clipCreateMethod = m;
                                    break;
                                }
                            }
                        }
                    }

                    _clipSetDataMethod = _audioClipType.GetMethod("SetData", new Type[] { typeof(float[]), typeof(int) });
                    if (_clipSetDataMethod == null)
                    {
                        foreach (var m in _audioClipType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (m.Name == "SetData" && m.GetParameters().Length == 2)
                            {
                                _clipSetDataMethod = m;
                                break;
                            }
                        }
                    }

                    // AudioSource 反射
                    _sourcePlayOneShotMethod = _audioClipType != null
                        ? _audioSourceType.GetMethod("PlayOneShot", new Type[] { _audioClipType, typeof(float) })
                        : null;
                    _sourcePlayMethod = _audioSourceType.GetMethod("Play", Type.EmptyTypes);
                    _sourceStopMethod = _audioSourceType.GetMethod("Stop", Type.EmptyTypes);
                    _sourcePauseMethod = _audioSourceType.GetMethod("Pause", Type.EmptyTypes);
                    _sourceUnPauseMethod = _audioSourceType.GetMethod("UnPause", Type.EmptyTypes);
                    _playClipAtPointMethod = _audioSourceType.GetMethod("PlayClipAtPoint", new Type[] { _audioClipType, typeof(Vector3), typeof(float) });

                    _sourceClipProp = _audioSourceType.GetProperty("clip");
                    _sourceVolumeProp = _audioSourceType.GetProperty("volume");
                    _sourceLoopProp = _audioSourceType.GetProperty("loop");
                    _sourceSpatialBlendProp = _audioSourceType.GetProperty("spatialBlend");
                    _sourceIsPlayingProp = _audioSourceType.GetProperty("isPlaying");

                    _available = (_clipCreateMethod != null && _clipSetDataMethod != null && _sourcePlayMethod != null);
                    SdkLog.Info("AUDIO", $"Unity 音频接口绑定: available={_available}, create={_clipCreateMethod != null}({_clipCreateMethod?.GetParameters().Length}p), setData={_clipSetDataMethod != null}, playOneShot={_sourcePlayOneShotMethod != null}, play={_sourcePlayMethod != null}");
                }
                catch (Exception e)
                {
                    SdkLog.Error("AUDIO", "解析 Unity 音频反射类型失败: " + e.Message);
                    _available = false;
                }
            }
        }

        private void EnsureHost()
        {
            if (_hostObject != null) return;
            EnsureInitialized();
            if (!_available) return;

            void Setup()
            {
                try
                {
                    _hostObject = new GameObject("[CesiumAudioHost]");
                    UnityEngine.Object.DontDestroyOnLoad(_hostObject);

                    _bgmSource = _hostObject.AddComponent(_audioSourceType);
                    _sfxSource = _hostObject.AddComponent(_audioSourceType);
                    _voiceSource = _hostObject.AddComponent(_audioSourceType);

                    if (_sourceSpatialBlendProp != null)
                    {
                        _sourceSpatialBlendProp.SetValue(_bgmSource, 0f, null);
                        _sourceSpatialBlendProp.SetValue(_sfxSource, 0f, null);
                        _sourceSpatialBlendProp.SetValue(_voiceSource, 0f, null);
                    }

                    EnsureAudioListener();
                }
                catch (Exception e)
                {
                    SdkLog.Error("AUDIO", "创建 CesiumAudioHost 失败: " + e.Message);
                }
            }

            if (MainThread.IsMainThread || !MainThread.HasMainThread)
            {
                Setup();
            }
            else
            {
                MainThread.Send(Setup);
            }
        }

        public object CreateAudioClip(string name, float[] samples, int channels, int sampleRate)
        {
            EnsureInitialized();
            if (!_available || samples == null || samples.Length == 0 || channels <= 0 || sampleRate <= 0)
                return null;

            int sampleCount = samples.Length / channels;
            object clip = null;

            void DoCreate()
            {
                try
                {
                    int pCount = _clipCreateMethod.GetParameters().Length;
                    object[] args = pCount switch
                    {
                        5 => new object[] { name ?? "CesiumClip", sampleCount, channels, sampleRate, false },
                        7 => new object[] { name ?? "CesiumClip", sampleCount, channels, sampleRate, false, null, null },
                        _ => new object[] { name ?? "CesiumClip", sampleCount, channels, sampleRate, false }
                    };
                    clip = _clipCreateMethod.Invoke(null, args);
                    if (clip != null)
                    {
                        _clipSetDataMethod.Invoke(clip, new object[] { samples, 0 });
                    }
                }
                catch (Exception e)
                {
                    SdkLog.Error("AUDIO", "创建 AudioClip 失败: " + e.Message);
                }
            }

            if (MainThread.IsMainThread || !MainThread.HasMainThread)
            {
                DoCreate();
            }
            else
            {
                MainThread.Send(DoCreate);
            }

            return clip;
        }

        private Component GetOrSpawnSfxSource()
        {
            if (_hostObject == null || _audioSourceType == null) return null;

            for (int i = 0; i < _sfxPool.Count; i++)
            {
                var s = _sfxPool[i];
                if (s == null) continue;
                bool isPlaying = false;
                try { isPlaying = (bool)(_sourceIsPlayingProp?.GetValue(s, null) ?? false); } catch { }
                if (!isPlaying) return s;
            }

            if (_sfxPool.Count < 8)
            {
                try
                {
                    var comp = _hostObject.AddComponent(_audioSourceType);
                    if (comp != null)
                    {
                        _sourceSpatialBlendProp?.SetValue(comp, 0f, null);
                        _sfxPool.Add(comp);
                        return comp;
                    }
                }
                catch { }
            }

            return _sfxSource ?? (_sfxPool.Count > 0 ? _sfxPool[0] : null);
        }

        public void PlayOneShot(object clip, float volume, AudioChannel channel)
        {
            if (clip == null) return;
            EnsureHost();
            if (_sourcePlayMethod == null && _sourcePlayOneShotMethod == null) return;

            float channelVol = GetVolume(channel);
            float effectiveVolume = Mathf.Clamp01(volume * channelVol * _masterVolume);

            void Execute()
            {
                try
                {
                    if (_sourcePlayOneShotMethod != null && _sfxSource != null)
                    {
                        _sourcePlayOneShotMethod.Invoke(_sfxSource, new object[] { clip, effectiveVolume });
                    }
                    else
                    {
                        var source = GetOrSpawnSfxSource();
                        if (source != null)
                        {
                            _sourceClipProp?.SetValue(source, clip, null);
                            _sourceVolumeProp?.SetValue(source, effectiveVolume, null);
                            _sourcePlayMethod?.Invoke(source, null);
                        }
                    }
                }
                catch (Exception e)
                {
                    SdkLog.Error("AUDIO", "PlayOneShot 播放异常: " + e.Message);
                }
            }

            if (MainThread.IsMainThread || !MainThread.HasMainThread) Execute();
            else MainThread.Post(Execute);
        }

        public void PlayBgm(object clip, bool loop, float volume, float fadeTime)
        {
            if (clip == null) return;
            EnsureHost();
            if (_bgmSource == null || _sourcePlayMethod == null) return;

            float effectiveVolume = Mathf.Clamp01(volume * _bgmVolume * _masterVolume);

            void Execute()
            {
                try
                {
                    _sourceClipProp?.SetValue(_bgmSource, clip, null);
                    _sourceLoopProp?.SetValue(_bgmSource, loop, null);
                    _sourceVolumeProp?.SetValue(_bgmSource, effectiveVolume, null);
                    _sourcePlayMethod.Invoke(_bgmSource, null);
                }
                catch (Exception e)
                {
                    SdkLog.Error("AUDIO", "PlayBgm 播放异常: " + e.Message);
                }
            }

            if (MainThread.IsMainThread) Execute();
            else MainThread.Post(Execute);
        }

        public void StopBgm(float fadeTime)
        {
            EnsureHost();
            if (_bgmSource == null || _sourceStopMethod == null) return;

            void Execute()
            {
                try
                {
                    _sourceStopMethod.Invoke(_bgmSource, null);
                }
                catch (Exception e)
                {
                    SdkLog.Error("AUDIO", "StopBgm 停止异常: " + e.Message);
                }
            }

            if (MainThread.IsMainThread) Execute();
            else MainThread.Post(Execute);
        }

        public void PauseBgm()
        {
            EnsureHost();
            if (_bgmSource == null || _sourcePauseMethod == null) return;

            void Execute()
            {
                try { _sourcePauseMethod.Invoke(_bgmSource, null); }
                catch { }
            }

            if (MainThread.IsMainThread) Execute();
            else MainThread.Post(Execute);
        }

        public void ResumeBgm()
        {
            EnsureHost();
            if (_bgmSource == null || _sourceUnPauseMethod == null) return;

            void Execute()
            {
                try { _sourceUnPauseMethod.Invoke(_bgmSource, null); }
                catch { }
            }

            if (MainThread.IsMainThread) Execute();
            else MainThread.Post(Execute);
        }

        public void PlayVoice(object clip, float volume)
        {
            if (clip == null) return;
            EnsureHost();
            if (_voiceSource == null || _sourcePlayMethod == null) return;

            float effectiveVolume = Mathf.Clamp01(volume * _voiceVolume * _masterVolume);

            void Execute()
            {
                try
                {
                    _sourceClipProp?.SetValue(_voiceSource, clip, null);
                    _sourceLoopProp?.SetValue(_voiceSource, false, null);
                    _sourceVolumeProp?.SetValue(_voiceSource, effectiveVolume, null);
                    _sourcePlayMethod.Invoke(_voiceSource, null);
                }
                catch (Exception e)
                {
                    SdkLog.Error("AUDIO", "PlayVoice 播放异常: " + e.Message);
                }
            }

            if (MainThread.IsMainThread) Execute();
            else MainThread.Post(Execute);
        }

        public void StopVoice()
        {
            EnsureHost();
            if (_voiceSource == null || _sourceStopMethod == null) return;

            void Execute()
            {
                try { _sourceStopMethod.Invoke(_voiceSource, null); }
                catch { }
            }

            if (MainThread.IsMainThread) Execute();
            else MainThread.Post(Execute);
        }

        public void PlayClipAtPoint(object clip, Vector3 position, float volume)
        {
            if (clip == null) return;
            EnsureInitialized();
            if (!_available || _playClipAtPointMethod == null) return;

            float effectiveVolume = Mathf.Clamp01(volume * _sfxVolume * _masterVolume);

            void Execute()
            {
                try
                {
                    _playClipAtPointMethod.Invoke(null, new object[] { clip, position, effectiveVolume });
                }
                catch (Exception e)
                {
                    SdkLog.Error("AUDIO", "PlayClipAtPoint 播放异常: " + e.Message);
                }
            }

            if (MainThread.IsMainThread) Execute();
            else MainThread.Post(Execute);
        }

        public void SetVolume(AudioChannel channel, float volume)
        {
            volume = Mathf.Clamp01(volume);
            switch (channel)
            {
                case AudioChannel.Master:
                    _masterVolume = volume;
                    UpdateAllSourceVolumes();
                    break;
                case AudioChannel.Bgm:
                    _bgmVolume = volume;
                    UpdateBgmVolume();
                    break;
                case AudioChannel.Sfx:
                    _sfxVolume = volume;
                    break;
                case AudioChannel.Voice:
                    _voiceVolume = volume;
                    UpdateVoiceVolume();
                    break;
                case AudioChannel.Custom:
                    _customVolume = volume;
                    break;
            }
        }

        public float GetVolume(AudioChannel channel)
        {
            switch (channel)
            {
                case AudioChannel.Master: return _masterVolume;
                case AudioChannel.Bgm: return _bgmVolume;
                case AudioChannel.Sfx: return _sfxVolume;
                case AudioChannel.Voice: return _voiceVolume;
                case AudioChannel.Custom: return _customVolume;
                default: return 1.0f;
            }
        }

        public void EnsureAudioListener()
        {
            if (_audioListenerType == null) return;
            void Check()
            {
                try
                {
                    var existing = UnityEngine.Object.FindObjectOfType(_audioListenerType);
                    if (existing == null && _hostObject != null)
                    {
                        _hostObject.AddComponent(_audioListenerType);
                        SdkLog.Info("AUDIO", "未检测到活跃的 AudioListener，已在 CesiumAudioHost 上自动添加。");
                    }
                }
                catch { }
            }

            if (MainThread.IsMainThread) Check();
            else MainThread.Post(Check);
        }

        private void UpdateAllSourceVolumes()
        {
            UpdateBgmVolume();
            UpdateVoiceVolume();
        }

        private void UpdateBgmVolume()
        {
            if (_bgmSource == null || _sourceVolumeProp == null) return;
            float vol = Mathf.Clamp01(_bgmVolume * _masterVolume);
            void Execute() { try { _sourceVolumeProp.SetValue(_bgmSource, vol, null); } catch { } }
            if (MainThread.IsMainThread) Execute();
            else MainThread.Post(Execute);
        }

        private void UpdateVoiceVolume()
        {
            if (_voiceSource == null || _sourceVolumeProp == null) return;
            float vol = Mathf.Clamp01(_voiceVolume * _masterVolume);
            void Execute() { try { _sourceVolumeProp.SetValue(_voiceSource, vol, null); } catch { } }
            if (MainThread.IsMainThread) Execute();
            else MainThread.Post(Execute);
        }
    }
}
