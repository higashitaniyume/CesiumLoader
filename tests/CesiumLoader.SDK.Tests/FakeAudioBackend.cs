using System;
using System.Collections.Generic;
using UnityEngine;
using CesiumLoader.SDK.Audio;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 测试用纯托管音频后端模拟器。
    /// </summary>
    internal sealed class FakeAudioBackend : IAudioBackend
    {
        public sealed class PlayedSoundRecord
        {
            public object Clip;
            public float Volume;
            public AudioChannel Channel;
            public Vector3? Position;
        }

        public bool IsAvailable { get; set; } = true;

        public List<PlayedSoundRecord> PlayedSounds = new List<PlayedSoundRecord>();
        public object CurrentBgmClip;
        public bool CurrentBgmLoop;
        public float CurrentBgmVolume;
        public bool IsBgmPlaying { get; set; }

        public object CurrentVoiceClip;
        public float CurrentVoiceVolume;
        public bool IsVoicePlaying { get; set; }

        public bool AudioListenerEnsured;

        private float _masterVolume = 1.0f;
        private float _bgmVolume = 1.0f;
        private float _sfxVolume = 1.0f;
        private float _voiceVolume = 1.0f;
        private float _customVolume = 1.0f;

        public object CreateAudioClip(string name, float[] samples, int channels, int sampleRate)
        {
            if (!IsAvailable || samples == null || samples.Length == 0 || channels <= 0 || sampleRate <= 0)
                return null;

            return new AudioClipHandle(new object(), name, (float)samples.Length / channels / sampleRate, channels, sampleRate, samples.Length / channels);
        }

        public void PlayOneShot(object clip, float volume, AudioChannel channel)
        {
            PlayedSounds.Add(new PlayedSoundRecord
            {
                Clip = clip,
                Volume = volume,
                Channel = channel
            });
        }

        public void PlayBgm(object clip, bool loop, float volume, float fadeTime)
        {
            CurrentBgmClip = clip;
            CurrentBgmLoop = loop;
            CurrentBgmVolume = volume;
            IsBgmPlaying = true;
        }

        public void StopBgm(float fadeTime)
        {
            IsBgmPlaying = false;
        }

        public void PauseBgm()
        {
            IsBgmPlaying = false;
        }

        public void ResumeBgm()
        {
            if (CurrentBgmClip != null) IsBgmPlaying = true;
        }

        public void PlayVoice(object clip, float volume)
        {
            CurrentVoiceClip = clip;
            CurrentVoiceVolume = volume;
            IsVoicePlaying = true;
        }

        public void StopVoice()
        {
            IsVoicePlaying = false;
        }

        public void PlayClipAtPoint(object clip, Vector3 position, float volume)
        {
            PlayedSounds.Add(new PlayedSoundRecord
            {
                Clip = clip,
                Volume = volume,
                Channel = AudioChannel.Sfx,
                Position = position
            });
        }

        public void SetVolume(AudioChannel channel, float volume)
        {
            switch (channel)
            {
                case AudioChannel.Master: _masterVolume = volume; break;
                case AudioChannel.Bgm: _bgmVolume = volume; break;
                case AudioChannel.Sfx: _sfxVolume = volume; break;
                case AudioChannel.Voice: _voiceVolume = volume; break;
                case AudioChannel.Custom: _customVolume = volume; break;
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
            AudioListenerEnsured = true;
        }

        public void Reset()
        {
            PlayedSounds.Clear();
            CurrentBgmClip = null;
            IsBgmPlaying = false;
            CurrentVoiceClip = null;
            IsVoicePlaying = false;
            AudioListenerEnsured = false;
            _masterVolume = 1.0f;
            _bgmVolume = 1.0f;
            _sfxVolume = 1.0f;
            _voiceVolume = 1.0f;
            _customVolume = 1.0f;
        }
    }
}
