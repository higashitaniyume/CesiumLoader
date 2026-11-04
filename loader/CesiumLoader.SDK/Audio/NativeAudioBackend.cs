using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using CesiumLoader.SDK.Logging;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 基于 CesiumLoader 原生通道 (version.dll) 的音频后端实现。
    /// 
    /// 解决在 Unity IL2CPP 构建下 UnityEngine.AudioSource 方法被代码裁剪剥离，
    /// 且热更程序集缺乏 Managed2Native thunk 无法任意 P/Invoke 的根本问题。
    /// 
    /// 采用 UDP 本地环回套接字（127.0.0.1）作为亚毫秒级低延迟主通道，
    /// 并辅以 request.txt 文件通道作为灾备，实现零依赖、高并发的音效与背景音乐播放。
    /// </summary>
    public sealed class NativeAudioBackend : IAudioBackend
    {
        private readonly object _lock = new object();
        private bool _initialized;
        private bool _isAvailable;
        private Socket _socket;
        private EndPoint _endPoint;
        private string _channelDir;

        private float _masterVolume = 1.0f;
        private float _bgmVolume = 1.0f;
        private float _sfxVolume = 1.0f;
        private float _voiceVolume = 1.0f;
        private float _customVolume = 1.0f;

        private bool _isBgmPlaying;
        private bool _isVoicePlaying;

        public bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _isAvailable;
            }
        }

        public bool IsBgmPlaying => _isBgmPlaying;
        public bool IsVoicePlaying => _isVoicePlaying;

        private void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;

                try
                {
                    // 加载器显式告知"本次没有启动原生音频服务"(CESIUM_AUDIO_ENABLED=0):
                    // 直接判定不可用。否则目录还在、port.txt/state.txt 可能是上一轮的残留,
                    // 会被误判成"仅文件通道就绪", 让自定义音效请求被静默丢弃。
                    string enabledFlag = Environment.GetEnvironmentVariable("CESIUM_AUDIO_ENABLED");
                    if (enabledFlag != null && enabledFlag.Trim() == "0")
                    {
                        SdkLog.Info("AUDIO", "原生音频服务未启动(加载器已关闭音频通道), 自定义音效不可用。");
                        _isAvailable = false;
                        return;
                    }

                    int port = ResolveAudioPort();
                    if (port > 0)
                    {
                        try
                        {
                            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                            _endPoint = new IPEndPoint(IPAddress.Loopback, port);
                            _isAvailable = true;
                            SdkLog.Info("AUDIO", $"NativeAudioBackend 就绪，绑定原生 UDP 端口: 127.0.0.1:{port}");
                            return;
                        }
                        catch (Exception ex)
                        {
                            SdkLog.Warn("AUDIO", "创建原生音频 UDP 套接字失败，尝试回退文件通道: " + ex.Message);
                        }
                    }

                    // 检查文件通道: 目录存在还不够 —— 必须是服务本轮写下的 port.txt/state.txt 才算就绪,
                    // 否则只是上一轮遗留的空壳目录, 会被误判成可用。
                    string dir = ResolveAudioDir();
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                        (File.Exists(Path.Combine(dir, "port.txt")) || File.Exists(Path.Combine(dir, "state.txt"))))
                    {
                        _channelDir = dir;
                        _isAvailable = true;
                        SdkLog.Info("AUDIO", "NativeAudioBackend 仅文件通道就绪: " + dir);
                        return;
                    }

                    SdkLog.Warn("AUDIO", "未能探测到原生音频服务（CESIUM_AUDIO_PORT 与 CESIUM_AUDIO_DIR 均不可用）。");
                    _isAvailable = false;
                }
                catch (Exception e)
                {
                    SdkLog.Error("AUDIO", "初始化 NativeAudioBackend 失败: " + e.Message);
                    _isAvailable = false;
                }
            }
        }

        private static int ResolveAudioPort()
        {
            // 1. 环境变量
            string envPort = Environment.GetEnvironmentVariable("CESIUM_AUDIO_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort.Trim(), out int p) && p > 0)
            {
                return p;
            }

            // 2. 从 audio 目录 port.txt 读取
            string dir = ResolveAudioDir();
            if (!string.IsNullOrEmpty(dir))
            {
                string portFile = Path.Combine(dir, "port.txt");
                if (File.Exists(portFile))
                {
                    try
                    {
                        string txt = File.ReadAllText(portFile);
                        if (int.TryParse(txt.Trim(), out int portVal) && portVal > 0)
                            return portVal;
                    }
                    catch { }
                }
            }

            return 0;
        }

        private static string ResolveAudioDir()
        {
            string envDir = Environment.GetEnvironmentVariable("CESIUM_AUDIO_DIR");
            if (!string.IsNullOrEmpty(envDir) && Directory.Exists(envDir))
                return envDir;

            string modsDir = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
            if (!string.IsNullOrEmpty(modsDir))
            {
                string loaderRoot = Path.GetDirectoryName(modsDir.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(loaderRoot))
                {
                    string candidate = Path.Combine(loaderRoot, "audio");
                    if (Directory.Exists(candidate)) return candidate;
                }
            }

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
            {
                string candidate = Path.Combine(localApp, "AstralParty_ModLoader", "audio");
                if (Directory.Exists(candidate)) return candidate;
            }

            return null;
        }

        private void SendCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return;
            EnsureInitialized();
            if (!_isAvailable) return;

            // 优先 UDP
            if (_socket != null && _endPoint != null)
            {
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(command);
                    _socket.SendTo(bytes, _endPoint);
                    return;
                }
                catch (Exception ex)
                {
                    SdkLog.Warn("AUDIO", "UDP 投递指令失败，回退文件通道: " + ex.Message);
                }
            }

            // 文件通道后备
            if (!string.IsNullOrEmpty(_channelDir))
            {
                try
                {
                    string reqPath = Path.Combine(_channelDir, "request.txt");
                    File.WriteAllText(reqPath, command + Environment.NewLine);
                }
                catch { }
            }
        }

        private static string ResolveClipPath(object clip)
        {
            if (clip == null) return null;
            if (clip is AudioClipHandle handle) return handle.SourcePath;
            if (clip is string path) return path;
            return null;
        }

        public object CreateAudioClip(string name, float[] samples, int channels, int sampleRate)
        {
            // NativeAudioBackend 不需要真实的 UnityEngine.AudioClip 对象，
            // 构造一个包装句柄即可携带格式元数据
            float duration = (channels > 0 && sampleRate > 0 && samples != null)
                ? (float)samples.Length / (channels * sampleRate)
                : 0f;
            return new AudioClipHandle(null, name, duration, channels, sampleRate, samples?.Length ?? 0, null);
        }

        public void PlayOneShot(object clip, float volume, AudioChannel channel)
        {
            string path = ResolveClipPath(clip);
            if (string.IsNullOrEmpty(path)) return;

            float channelVol = GetVolume(channel);
            float effectiveVolume = Mathf.Clamp01(volume * channelVol * _masterVolume);
            int mciVol = Mathf.Clamp(Mathf.RoundToInt(effectiveVolume * 1000f), 0, 1000);

            string ch = (channel == AudioChannel.Voice) ? "voice" : "sfx";
            SendCommand($"PLAY|{ch}|{mciVol}|{path}");
        }

        public void PlayBgm(object clip, bool loop, float volume, float fadeTime)
        {
            string path = ResolveClipPath(clip);
            if (string.IsNullOrEmpty(path)) return;

            float effectiveVolume = Mathf.Clamp01(volume * _bgmVolume * _masterVolume);
            int mciVol = Mathf.Clamp(Mathf.RoundToInt(effectiveVolume * 1000f), 0, 1000);

            SendCommand($"BGM_PLAY|{mciVol}|{(loop ? "1" : "0")}|{path}");
            _isBgmPlaying = true;
        }

        public void StopBgm(float fadeTime)
        {
            SendCommand("BGM_STOP");
            _isBgmPlaying = false;
        }

        public void PauseBgm()
        {
            SendCommand("BGM_PAUSE");
        }

        public void ResumeBgm()
        {
            SendCommand("BGM_RESUME");
        }

        public void PlayVoice(object clip, float volume)
        {
            string path = ResolveClipPath(clip);
            if (string.IsNullOrEmpty(path)) return;

            float effectiveVolume = Mathf.Clamp01(volume * _voiceVolume * _masterVolume);
            int mciVol = Mathf.Clamp(Mathf.RoundToInt(effectiveVolume * 1000f), 0, 1000);

            SendCommand($"PLAY|voice|{mciVol}|{path}");
            _isVoicePlaying = true;
        }

        public void StopVoice()
        {
            SendCommand("STOP|voice");
            _isVoicePlaying = false;
        }

        public void PlayClipAtPoint(object clip, Vector3 position, float volume)
        {
            // 原生 MCI 不支持 3D 空间坐标衰减，退化为普通 SFX 播放
            PlayOneShot(clip, volume, AudioChannel.Sfx);
        }

        public void SetVolume(AudioChannel channel, float volume)
        {
            float clamped = Mathf.Clamp01(volume);
            switch (channel)
            {
                case AudioChannel.Master:
                    _masterVolume = clamped;
                    if (_isBgmPlaying)
                    {
                        int bgmMciVol = Mathf.Clamp(Mathf.RoundToInt(_bgmVolume * _masterVolume * 1000f), 0, 1000);
                        SendCommand($"BGM_VOL|{bgmMciVol}");
                    }
                    break;
                case AudioChannel.Bgm:
                    _bgmVolume = clamped;
                    if (_isBgmPlaying)
                    {
                        int bgmMciVol = Mathf.Clamp(Mathf.RoundToInt(_bgmVolume * _masterVolume * 1000f), 0, 1000);
                        SendCommand($"BGM_VOL|{bgmMciVol}");
                    }
                    break;
                case AudioChannel.Sfx:
                    _sfxVolume = clamped;
                    break;
                case AudioChannel.Voice:
                    _voiceVolume = clamped;
                    break;
                case AudioChannel.Custom:
                    _customVolume = clamped;
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
            // 原生后端直接输出至 Windows 默认音频端点，不需要场景内 AudioListener
        }
    }
}
