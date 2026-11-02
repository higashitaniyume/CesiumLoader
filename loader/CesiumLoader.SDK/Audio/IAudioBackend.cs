using System;
using UnityEngine;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 音频底层抽象接口。
    /// 
    /// 使得 AudioService 的绝大部分逻辑、音量控制、替换规则与事件调度均可在
    /// 无 Unity 引擎原生运行时的测试宿主中进行 100% 单元测试。
    /// </summary>
    public interface IAudioBackend
    {
        /// <summary>后端是否可用。</summary>
        bool IsAvailable { get; }

        /// <summary>从 PCM 采样数据创建 AudioClip 原生句柄（返回 object）。</summary>
        object CreateAudioClip(string name, float[] samples, int channels, int sampleRate);

        /// <summary>播放单次音效。</summary>
        void PlayOneShot(object clip, float volume, AudioChannel channel);

        /// <summary>播放背景音乐。</summary>
        void PlayBgm(object clip, bool loop, float volume, float fadeTime);

        /// <summary>停止背景音乐。</summary>
        void StopBgm(float fadeTime);

        /// <summary>暂停背景音乐。</summary>
        void PauseBgm();

        /// <summary>恢复背景音乐。</summary>
        void ResumeBgm();

        /// <summary>背景音乐是否正在播放。</summary>
        bool IsBgmPlaying { get; }

        /// <summary>播放角色语音（独占 Voice 通道，可打断上一句）。</summary>
        void PlayVoice(object clip, float volume);

        /// <summary>停止角色语音。</summary>
        void StopVoice();

        /// <summary>角色语音是否正在播放。</summary>
        bool IsVoicePlaying { get; }

        /// <summary>在指定 3D 坐标播放声音。</summary>
        void PlayClipAtPoint(object clip, Vector3 position, float volume);

        /// <summary>设置通道音量 (0.0f ~ 1.0f)。</summary>
        void SetVolume(AudioChannel channel, float volume);

        /// <summary>获取通道音量。</summary>
        float GetVolume(AudioChannel channel);

        /// <summary>确保场景中存在 AudioListener。</summary>
        void EnsureAudioListener();
    }
}
