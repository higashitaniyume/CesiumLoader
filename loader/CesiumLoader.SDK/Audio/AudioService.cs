using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// CesiumLoader SDK 音频服务门面类。
    /// 
    /// 提供统一的自定义音频播放（BGM、音效、角色语音）、WAV 解码与缓存、
    /// 原版音频事件拦截替换、背景音乐覆盖以及与游戏设置音量联动等功能。
    /// </summary>
    public static class AudioService
    {
        private static readonly object _ruleLock = new object();
        private static readonly List<AudioReplacementRule> _rules = new List<AudioReplacementRule>();
        private static readonly Dictionary<string, object> _clipCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private static IAudioBackend _backend;
        private static bool _syncWithGameSettings = true;
        private static bool _bgmOverrideActive = false;
        private static float _previousWwiseBgmVolume = -1f;

        /// <summary>
        /// 底层音频后端。默认使用 Unity 原生后端（测试时可注入模拟后端）。
        /// </summary>
        public static IAudioBackend Backend
        {
            get
            {
                if (_backend == null)
                {
                    var unityBackend = new UnityAudioBackend();
                    if (unityBackend.IsAvailable)
                    {
                        _backend = unityBackend;
                    }
                    else
                    {
                        var nativeBackend = new NativeAudioBackend();
                        _backend = nativeBackend.IsAvailable ? (IAudioBackend)nativeBackend : unityBackend;
                    }
                }
                return _backend;
            }
            set
            {
                _backend = value;
            }
        }

        /// <summary>音频服务是否可用（原生引擎是否就绪）。</summary>
        public static bool IsAvailable => Backend != null && Backend.IsAvailable;

        /// <summary>是否自动与《Astral Party》游戏内设置面板的音量联动。</summary>
        public static bool SyncWithGameSettings
        {
            get => _syncWithGameSettings;
            set => _syncWithGameSettings = value;
        }

        /// <summary>当前是否有自定义 BGM 覆盖正在运行。</summary>
        public static bool IsBgmOverrideActive => _bgmOverrideActive;

        /// <summary>当前 BGM 是否正在播放。</summary>
        public static bool IsBgmPlaying => Backend != null && Backend.IsBgmPlaying;

        /// <summary>当前语音是否正在播放。</summary>
        public static bool IsVoicePlaying => Backend != null && Backend.IsVoicePlaying;

        static AudioService()
        {
            // 自动开启事件钩子维护
            AudioEvents.StartAutoHook();
        }

        // =====================================================================
        // 音频播放 API
        // =====================================================================

        /// <summary>
        /// 播放一次性音效。
        /// </summary>
        /// <param name="clip">AudioClip 对象句柄。</param>
        /// <param name="volumeScale">音量缩放系数 (0.0 ~ 1.0)。</param>
        /// <param name="channel">音频通道（默认 Sfx）。</param>
        public static void PlayOneShot(object clip, float volumeScale = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            if (clip == null || Backend == null) return;
            float vol = ComputeEffectiveVolume(channel, volumeScale);
            Backend.PlayOneShot(clip, vol, channel);
        }

        /// <summary>
        /// 从文件路径加载并播放一次性音效（自动缓存）。
        /// </summary>
        public static void PlayOneShot(string filePath, float volumeScale = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            object clip = GetOrLoadClip(filePath);
            if (clip != null)
            {
                PlayOneShot(clip, volumeScale, channel);
            }
        }

        /// <summary>
        /// 播放背景音乐（BGM）。
        /// </summary>
        public static void PlayBgm(object clip, bool loop = true, float volume = 1.0f, float fadeTime = 0.5f)
        {
            if (clip == null || Backend == null) return;
            float vol = ComputeEffectiveVolume(AudioChannel.Bgm, volume);
            Backend.PlayBgm(clip, loop, vol, fadeTime);
        }

        /// <summary>
        /// 从文件路径加载并播放背景音乐（BGM）。
        /// </summary>
        public static void PlayBgm(string filePath, bool loop = true, float volume = 1.0f, float fadeTime = 0.5f)
        {
            object clip = GetOrLoadClip(filePath);
            if (clip != null)
            {
                PlayBgm(clip, loop, volume, fadeTime);
            }
        }

        /// <summary>
        /// 停止背景音乐。
        /// </summary>
        public static void StopBgm(float fadeTime = 0.5f)
        {
            Backend?.StopBgm(fadeTime);
        }

        /// <summary>
        /// 暂停背景音乐。
        /// </summary>
        public static void PauseBgm()
        {
            Backend?.PauseBgm();
        }

        /// <summary>
        /// 恢复背景音乐。
        /// </summary>
        public static void ResumeBgm()
        {
            Backend?.ResumeBgm();
        }

        /// <summary>
        /// 播放角色语音（独占 Voice 通道，可打断上一句自定义语音）。
        /// </summary>
        public static void PlayVoice(object clip, float volume = 1.0f)
        {
            if (clip == null || Backend == null) return;
            float vol = ComputeEffectiveVolume(AudioChannel.Voice, volume);
            Backend.PlayVoice(clip, vol);
        }

        /// <summary>
        /// 停止角色语音播放。
        /// </summary>
        public static void StopVoice()
        {
            Backend?.StopVoice();
        }

        /// <summary>
        /// 在 3D 空间坐标处播放声音（空间定位音效）。
        /// </summary>
        public static void PlayClipAtPoint(object clip, Vector3 position, float volume = 1.0f)
        {
            if (clip == null || Backend == null) return;
            float vol = ComputeEffectiveVolume(AudioChannel.Sfx, volume);
            Backend.PlayClipAtPoint(clip, position, vol);
        }

        // =====================================================================
        // 音量管理与联动
        // =====================================================================

        /// <summary>设置指定通道的基础音量 (0.0 ~ 1.0)。</summary>
        public static void SetVolume(AudioChannel channel, float volume)
        {
            Backend?.SetVolume(channel, volume);
        }

        /// <summary>设置指定通道的基础音量（SetVolume 别名）。</summary>
        public static void SetChannelVolume(AudioChannel channel, float volume)
        {
            SetVolume(channel, volume);
        }

        /// <summary>从游戏设置中同步音量。</summary>
        public static void SyncVolumeFromGameSettings()
        {
            // 自动触碰一次以触发缓存或初始化
            try
            {
                _ = GameAudioBridge.GetGameMasterVolume();
            }
            catch { }
        }

        /// <summary>获取指定通道的基础音量。</summary>
        public static float GetVolume(AudioChannel channel)
        {
            return Backend != null ? Backend.GetVolume(channel) : 1.0f;
        }

        /// <summary>
        /// 计算结合通道音量、主音量以及游戏内设置后的最终有效音量。
        /// </summary>
        public static float ComputeEffectiveVolume(AudioChannel channel, float requestedVolume = 1.0f)
        {
            float baseVol = Mathf.Clamp01(requestedVolume);
            if (!_syncWithGameSettings) return baseVol;

            try
            {
                float gameMaster = GameAudioBridge.GetGameMasterVolume() / 100f;
                float gameChannel = 1.0f;

                switch (channel)
                {
                    case AudioChannel.Bgm:
                        gameChannel = GameAudioBridge.GetGameBgmVolume() / 100f;
                        break;
                    case AudioChannel.Sfx:
                        gameChannel = GameAudioBridge.GetGameSfxVolume() / 100f;
                        break;
                    case AudioChannel.Voice:
                        gameChannel = GameAudioBridge.GetGameVoiceVolume() / 100f;
                        break;
                }

                return Mathf.Clamp01(baseVol * gameMaster * gameChannel);
            }
            catch
            {
                return baseVol;
            }
        }

        // =====================================================================
        // 音频加载与缓存
        // =====================================================================

        /// <summary>
        /// 从文件路径同步解码并创建 AudioClip 对象句柄（支持标准 WAV 文件）。
        /// </summary>
        public static object LoadWav(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                SdkLog.Warn("AUDIO", "WAV 文件不存在: " + filePath);
                return null;
            }

            if (!WavUtility.TryDecode(filePath, out float[] samples, out int channels, out int sampleRate))
            {
                SdkLog.Error("AUDIO", "WAV 文件解码失败: " + filePath);
                return null;
            }

            string clipName = Path.GetFileNameWithoutExtension(filePath);
            object rawClip = Backend?.CreateAudioClip(clipName, samples, channels, sampleRate);
            float duration = (channels > 0 && sampleRate > 0) ? (float)samples.Length / (channels * sampleRate) : 0f;
            return new AudioClipHandle(rawClip, clipName, duration, channels, sampleRate, samples.Length, filePath);
        }

        /// <summary>
        /// 从内存字节数组同步解码并创建 AudioClip。
        /// </summary>
        public static object LoadWav(byte[] wavBytes, string clipName = "CustomWav")
        {
            if (wavBytes == null || wavBytes.Length == 0) return null;

            if (!WavUtility.TryDecode(wavBytes, out float[] samples, out int channels, out int sampleRate))
            {
                SdkLog.Error("AUDIO", "内存 WAV 数据解码失败: " + clipName);
                return null;
            }

            object rawClip = Backend?.CreateAudioClip(clipName, samples, channels, sampleRate);
            float duration = (channels > 0 && sampleRate > 0) ? (float)samples.Length / (channels * sampleRate) : 0f;
            return new AudioClipHandle(rawClip, clipName, duration, channels, sampleRate, samples.Length, null);
        }

        /// <summary>
        /// 获取已缓存的音频片段，或首次自动从磁盘加载。
        /// </summary>
        public static object GetOrLoadClip(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;

            lock (_clipCache)
            {
                if (_clipCache.TryGetValue(filePath, out object cached) && cached != null)
                {
                    return cached;
                }
            }

            object clip = LoadWav(filePath);
            if (clip != null)
            {
                lock (_clipCache)
                {
                    _clipCache[filePath] = clip;
                }
            }
            return clip;
        }

        /// <summary>
        /// 释放已缓存的音频片段。
        /// </summary>
        public static void UnloadClip(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            lock (_clipCache)
            {
                _clipCache.Remove(filePath);
            }
        }

        /// <summary>
        /// 清空所有音频缓存。
        /// </summary>
        public static void ClearClipCache()
        {
            lock (_clipCache)
            {
                _clipCache.Clear();
            }
        }

        // =====================================================================
        // 音频替换规则管理 (声明式 API)
        // =====================================================================

        /// <summary>
        /// 注册音频替换规则对象。
        /// </summary>
        public static void RegisterReplacement(AudioReplacementRule rule)
        {
            AddRule(rule);
        }

        /// <summary>
        /// 注册按游戏事件配置 ID 替换声音的规则。
        /// </summary>
        public static void RegisterReplacement(int eventConfigId, object clip, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            AddRule(new AudioReplacementRule(eventConfigId, clip, volume, channel));
        }

        /// <summary>
        /// 注册按游戏事件配置 ID 替换声音文件的规则。
        /// </summary>
        public static void RegisterReplacement(int eventConfigId, string audioFilePath, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            AddRule(new AudioReplacementRule(eventConfigId, audioFilePath, volume, channel));
        }

        /// <summary>
        /// 注册按 Wwise 事件名称替换声音的规则。
        /// </summary>
        public static void RegisterReplacement(string eventName, object clip, bool prefix = false, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            AddRule(new AudioReplacementRule(eventName, clip, prefix, volume, channel));
        }

        /// <summary>
        /// 注册按 Wwise 事件名称替换声音文件的规则。
        /// </summary>
        public static void RegisterReplacement(string eventName, string audioFilePath, bool prefix = false, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            AddRule(new AudioReplacementRule(eventName, audioFilePath, prefix, volume, channel));
        }

        /// <summary>
        /// 注册基于自定义谓词条件与解析器的替换规则。
        /// </summary>
        public static void RegisterReplacement(Predicate<SoundPlayEventArgs> filter, Func<SoundPlayEventArgs, object> resolver, float volume = 1.0f, AudioChannel channel = AudioChannel.Sfx)
        {
            AddRule(new AudioReplacementRule(filter, resolver, volume, channel));
        }

        private static void AddRule(AudioReplacementRule rule)
        {
            if (rule == null) return;
            lock (_ruleLock)
            {
                _rules.Add(rule);
            }
            AudioEvents.EnsureHooked();
        }

        /// <summary>
        /// 移除所有与指定配置 ID 关联的替换规则。
        /// </summary>
        public static void UnregisterReplacement(int eventConfigId)
        {
            lock (_ruleLock)
            {
                _rules.RemoveAll(r => r.MatchMode == AudioMatchMode.ConfigId && r.TargetConfigId == eventConfigId);
            }
        }

        /// <summary>
        /// 移除所有与指定事件名称关联的替换规则。
        /// </summary>
        public static void UnregisterReplacement(string eventName)
        {
            lock (_ruleLock)
            {
                _rules.RemoveAll(r => (r.MatchMode == AudioMatchMode.EventNameExact || r.MatchMode == AudioMatchMode.EventNamePrefix) &&
                                      string.Equals(r.TargetEventName, eventName, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// 清空所有注册的替换规则。
        /// </summary>
        public static void ClearReplacements()
        {
            lock (_ruleLock)
            {
                _rules.Clear();
            }
        }

        /// <summary>
        /// 查找匹配当前音频事件的最高优先级替换规则（后注册者优先）。
        /// </summary>
        internal static AudioReplacementRule FindMatchingRule(SoundPlayEventArgs args)
        {
            if (args == null) return null;
            lock (_ruleLock)
            {
                for (int i = _rules.Count - 1; i >= 0; i--)
                {
                    if (_rules[i].Matches(args))
                        return _rules[i];
                }
            }
            return null;
        }

        // =====================================================================
        // 背景音乐覆盖 (BGM Override)
        // =====================================================================

        /// <summary>
        /// 启用全局自定义 BGM 覆盖。
        /// 会自动将原版 Wwise BGM 音量静音，并播放自定义背景音乐。
        /// </summary>
        public static void SetBgmOverride(object clip, bool loop = true, float volume = 1.0f, float fadeTime = 0.5f)
        {
            if (clip == null) return;

            MuteGameWwiseBgm();
            _bgmOverrideActive = true;
            PlayBgm(clip, loop, volume, fadeTime);
            SdkLog.Info("AUDIO", "已启用自定义 BGM 覆盖。");
        }

        /// <summary>
        /// 启用全局自定义 BGM 覆盖（从文件路径加载）。
        /// </summary>
        public static void SetBgmOverride(string audioFilePath, bool loop = true, float volume = 1.0f, float fadeTime = 0.5f)
        {
            object clip = GetOrLoadClip(audioFilePath);
            if (clip != null)
            {
                SetBgmOverride(clip, loop, volume, fadeTime);
            }
        }

        /// <summary>
        /// 取消自定义 BGM 覆盖，停止自定义音乐并恢复原版游戏 BGM 音量。
        /// </summary>
        public static void ClearBgmOverride(float fadeTime = 0.5f)
        {
            if (!_bgmOverrideActive) return;

            StopBgm(fadeTime);
            RestoreGameWwiseBgm();
            _bgmOverrideActive = false;
            SdkLog.Info("AUDIO", "已取消自定义 BGM 覆盖并恢复游戏音乐。");
        }

        private static void MuteGameWwiseBgm()
        {
            try
            {
                _previousWwiseBgmVolume = GameAudioBridge.GetGameBgmVolume();
                // 通过 Wwise RTPC 将原版 BGM 音量归零，不破坏原版状态机
                GameAudioBridge.SetWwiseRtpc("BGMVolume", 0f);
            }
            catch (Exception ex)
            {
                SdkLog.Warn("AUDIO", "静音原版 Wwise BGM 失败: " + ex.Message);
            }
        }

        private static void RestoreGameWwiseBgm()
        {
            try
            {
                float targetVol = _previousWwiseBgmVolume >= 0 ? _previousWwiseBgmVolume : GameAudioBridge.GetGameBgmVolume();
                GameAudioBridge.SetWwiseRtpc("BGMVolume", targetVol);
            }
            catch (Exception ex)
            {
                SdkLog.Warn("AUDIO", "恢复原版 Wwise BGM 失败: " + ex.Message);
            }
        }
    }
}
