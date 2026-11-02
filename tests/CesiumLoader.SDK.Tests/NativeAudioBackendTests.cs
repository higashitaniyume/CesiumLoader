using System;
using System.IO;
using CesiumLoader.SDK.Audio;
using UnityEngine;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    public class NativeAudioBackendTests
    {
        [Fact]
        public void CreateAudioClip_CreatesHandleWithMetadata()
        {
            var backend = new NativeAudioBackend();
            float[] samples = new float[44100 * 2]; // 1 second of stereo
            object clip = backend.CreateAudioClip("test", samples, 2, 44100);

            Assert.NotNull(clip);
            var handle = Assert.IsType<AudioClipHandle>(clip);
            Assert.Equal("test", handle.Name);
            Assert.Equal(2, handle.Channels);
            Assert.Equal(44100, handle.Frequency);
            Assert.Equal(1.0f, handle.Duration, 2);
        }

        [Fact]
        public void SetVolume_ClampsAndStoresPerChannel()
        {
            var backend = new NativeAudioBackend();
            backend.SetVolume(AudioChannel.Sfx, 1.5f);
            Assert.Equal(1.0f, backend.GetVolume(AudioChannel.Sfx));

            backend.SetVolume(AudioChannel.Sfx, -0.5f);
            Assert.Equal(0.0f, backend.GetVolume(AudioChannel.Sfx));

            backend.SetVolume(AudioChannel.Bgm, 0.42f);
            Assert.Equal(0.42f, backend.GetVolume(AudioChannel.Bgm), 2);
        }

        [Fact]
        public void PlayOneShot_WithAudioClipHandleOrString_DoesNotThrow()
        {
            var backend = new NativeAudioBackend();
            var handle = new AudioClipHandle(null, "click", 0.5f, 2, 44100, 44100, @"C:\fake\click.wav");

            // Should not throw even when socket or server is not listening
            backend.PlayOneShot(handle, 0.8f, AudioChannel.Sfx);
            backend.PlayOneShot(@"C:\fake\direct.wav", 1.0f, AudioChannel.Sfx);
            backend.PlayOneShot(null, 1.0f, AudioChannel.Sfx);
        }

        [Fact]
        public void BgmAndVoiceControls_DoNotThrow()
        {
            var backend = new NativeAudioBackend();
            backend.PlayBgm(@"C:\fake\bgm.wav", true, 0.7f, 0.5f);
            Assert.True(backend.IsBgmPlaying);

            backend.PauseBgm();
            backend.ResumeBgm();
            backend.StopBgm(0.5f);
            Assert.False(backend.IsBgmPlaying);

            backend.PlayVoice(@"C:\fake\voice.wav", 0.9f);
            Assert.True(backend.IsVoicePlaying);

            backend.StopVoice();
            Assert.False(backend.IsVoicePlaying);
        }
    }
}
