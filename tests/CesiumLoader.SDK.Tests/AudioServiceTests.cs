using System;
using System.IO;
using UnityEngine;
using CesiumLoader.SDK.Audio;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    public class AudioServiceTests
    {
        private readonly FakeAudioBackend _backend;

        public AudioServiceTests()
        {
            _backend = new FakeAudioBackend();
            AudioService.Backend = _backend;
            AudioService.SyncWithGameSettings = false; // 测试时关闭游戏设置联动，保持纯净测试
            AudioService.ClearReplacements();
            AudioService.ClearClipCache();
        }

        [Fact]
        public void PlayOneShot_PlaysThroughBackend()
        {
            var clip = new object();
            AudioService.PlayOneShot(clip, 0.75f, AudioChannel.Sfx);

            Assert.Single(_backend.PlayedSounds);
            var record = _backend.PlayedSounds[0];
            Assert.Same(clip, record.Clip);
            Assert.Equal(0.75f, record.Volume);
            Assert.Equal(AudioChannel.Sfx, record.Channel);
        }

        [Fact]
        public void PlayBgm_ControlsBgmPlayback()
        {
            var bgmClip = new object();
            AudioService.PlayBgm(bgmClip, true, 0.8f);

            Assert.True(AudioService.IsBgmPlaying);
            Assert.Same(bgmClip, _backend.CurrentBgmClip);
            Assert.True(_backend.CurrentBgmLoop);
            Assert.Equal(0.8f, _backend.CurrentBgmVolume);

            AudioService.PauseBgm();
            Assert.False(AudioService.IsBgmPlaying);

            AudioService.ResumeBgm();
            Assert.True(AudioService.IsBgmPlaying);

            AudioService.StopBgm();
            Assert.False(AudioService.IsBgmPlaying);
        }

        [Fact]
        public void PlayVoice_ControlsVoicePlayback()
        {
            var voiceClip = new object();
            AudioService.PlayVoice(voiceClip, 0.9f);

            Assert.True(AudioService.IsVoicePlaying);
            Assert.Same(voiceClip, _backend.CurrentVoiceClip);
            Assert.Equal(0.9f, _backend.CurrentVoiceVolume);

            AudioService.StopVoice();
            Assert.False(AudioService.IsVoicePlaying);
        }

        [Fact]
        public void Volume_GetAndSet_WorksPerChannel()
        {
            AudioService.SetVolume(AudioChannel.Bgm, 0.6f);
            AudioService.SetVolume(AudioChannel.Voice, 0.7f);

            Assert.Equal(0.6f, AudioService.GetVolume(AudioChannel.Bgm));
            Assert.Equal(0.7f, AudioService.GetVolume(AudioChannel.Voice));
        }

        [Fact]
        public void ClipCache_GetOrLoadClip_CachesInstance()
        {
            string tempDir = TestEnv.NewTempDir("audio-service-cache");
            string wavPath = Path.Combine(tempDir, "sample.wav");
            File.WriteAllBytes(wavPath, WavUtility.CreateSyntheticTone(440f, 0.1f));

            object clip1 = AudioService.GetOrLoadClip(wavPath);
            Assert.NotNull(clip1);

            object clip2 = AudioService.GetOrLoadClip(wavPath);
            Assert.Same(clip1, clip2);

            AudioService.UnloadClip(wavPath);
            object clip3 = AudioService.GetOrLoadClip(wavPath);
            Assert.NotNull(clip3);
        }

        [Fact]
        public void ReplacementRule_MatchesConfigId()
        {
            var customClip = new object();
            AudioService.RegisterReplacement(1001, customClip, 0.8f);

            var args1 = new SoundPlayEventArgs(1001, "Test_Sound", 9999);
            var rule1 = AudioService.FindMatchingRule(args1);
            Assert.NotNull(rule1);
            Assert.Same(customClip, rule1.ResolveClip(args1));
            Assert.Equal(0.8f, rule1.Volume);

            var args2 = new SoundPlayEventArgs(1002, "Other_Sound", 8888);
            var rule2 = AudioService.FindMatchingRule(args2);
            Assert.Null(rule2);
        }

        [Fact]
        public void ReplacementRule_MatchesEventNameExactAndPrefix()
        {
            var clipExact = new object();
            var clipPrefix = new object();

            AudioService.RegisterReplacement("ui_btn_click", clipExact, prefix: false);
            AudioService.RegisterReplacement("voice_hero_1001_", clipPrefix, prefix: true);

            var clickArgs = new SoundPlayEventArgs(500, "UI_BTN_CLICK", 123);
            var clickRule = AudioService.FindMatchingRule(clickArgs);
            Assert.NotNull(clickRule);
            Assert.Same(clipExact, clickRule.ResolveClip(clickArgs));

            var voiceArgs = new SoundPlayEventArgs(600, "Voice_Hero_1001_Atk", 456);
            var voiceRule = AudioService.FindMatchingRule(voiceArgs);
            Assert.NotNull(voiceRule);
            Assert.Same(clipPrefix, voiceRule.ResolveClip(voiceArgs));

            var otherVoice = new SoundPlayEventArgs(700, "Voice_Hero_1002_Atk", 789);
            Assert.Null(AudioService.FindMatchingRule(otherVoice));
        }

        [Fact]
        public void ReplacementRule_CustomPredicate()
        {
            var specialClip = new object();
            AudioService.RegisterReplacement(
                filter: args => args.EventConfigId >= 1000 && args.EventConfigId < 2000,
                resolver: args => specialClip,
                volume: 0.95f
            );

            var inRange = new SoundPlayEventArgs(1500, "Sound_1500", 11);
            var matched = AudioService.FindMatchingRule(inRange);
            Assert.NotNull(matched);
            Assert.Same(specialClip, matched.ResolveClip(inRange));

            var outOfRange = new SoundPlayEventArgs(2500, "Sound_2500", 22);
            Assert.Null(AudioService.FindMatchingRule(outOfRange));
        }

        [Fact]
        public void PlayClipAtPoint_CallsBackend()
        {
            var clip = new object();
            Vector3 pos = new Vector3(10f, 2f, 5f);
            AudioService.PlayClipAtPoint(clip, pos, 0.8f);

            Assert.Single(_backend.PlayedSounds);
            var record = _backend.PlayedSounds[0];
            Assert.Same(clip, record.Clip);
            Assert.Equal(pos, record.Position);
        }
    }
}
