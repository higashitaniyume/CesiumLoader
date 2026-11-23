using System;
using CesiumLoader.SDK.Audio;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    /// <summary>
    /// 背景音乐（BGM）替换链路测试。
    ///
    /// 游戏 BGM 走 BGMHelper → AudioManager.PlayBGM → BGMModule → AkSoundEngine.PostEvent，
    /// 不经过 Stage.playSoundCallback，因此 SDK 用「当前在播哪首 BGM」重新判定规则，
    /// 命中后用循环 BGM 覆盖并静音原版 Wwise 音乐。这里的测试直接投递识别到的 BGM 事件。
    /// </summary>
    public class AudioBgmTests
    {
        private readonly FakeAudioBackend _backend;

        public AudioBgmTests()
        {
            _backend = new FakeAudioBackend();
            AudioService.Backend = _backend;
            AudioService.SyncWithGameSettings = false;
            AudioService.ClearBgmOverride();
            AudioService.ClearReplacements();
            AudioEvents.ResetStatsForTesting();
        }

        [Fact]
        public void BgmRule_WithCustomClip_OverridesGameBgmAndLoops()
        {
            var clip = new object();
            // 故意用默认通道(Sfx)注册: BGM 语义由事件类型决定，不依赖 Mod 是否声明通道。
            AudioService.RegisterReplacement(10011, clip, 0.9f);

            bool suppressed = AudioEvents.DispatchBgmForTesting(10011, "BGM_Login", bankId: 1001);

            Assert.True(suppressed);
            Assert.True(AudioService.IsBgmOverrideActive);
            Assert.False(AudioService.IsBgmMuteOnly);
            Assert.Same(clip, _backend.CurrentBgmClip);
            Assert.True(_backend.CurrentBgmLoop);
            Assert.Equal(0.9f, _backend.CurrentBgmVolume);
            Assert.Equal(1, AudioEvents.BgmReplacedCount);
            Assert.Equal(0, AudioEvents.BgmMutedCount);
            Assert.Equal(10011, AudioEvents.CurrentBgmEventConfigId);
            Assert.Equal("BGM_Login", AudioEvents.CurrentBgmEventName);
        }

        [Fact]
        public void BgmRule_LoopDisabled_HonoursRuleLoop()
        {
            var clip = new object();
            var rule = AudioReplacementRule.ForConfigId(10021, clip);
            rule.Channel = AudioChannel.Bgm;
            rule.Loop = false;
            AudioService.RegisterReplacement(rule);

            AudioEvents.DispatchBgmForTesting(10021, "BGM_Home");

            Assert.Same(clip, _backend.CurrentBgmClip);
            Assert.False(_backend.CurrentBgmLoop);
        }

        [Fact]
        public void BgmMuteRule_SilencesOriginalWithoutCustomMusic()
        {
            AudioService.RegisterReplacement(AudioReplacementRule.Mute(10011));

            bool suppressed = AudioEvents.DispatchBgmForTesting(10011, "BGM_Login");

            Assert.True(suppressed);
            Assert.True(AudioService.IsBgmOverrideActive);
            Assert.True(AudioService.IsBgmMuteOnly);
            Assert.Null(_backend.CurrentBgmClip);
            Assert.False(_backend.IsBgmPlaying);
            Assert.Equal(1, AudioEvents.BgmMutedCount);
            Assert.Equal(0, AudioEvents.BgmReplacedCount);
        }

        [Fact]
        public void BgmWithoutRule_RestoresOriginalMusic()
        {
            AudioService.RegisterReplacement(10011, new object(), 0.5f);
            AudioEvents.DispatchBgmForTesting(10011, "BGM_Login");
            Assert.True(AudioService.IsBgmOverrideActive);

            // 换到一首没有配置替换的曲子: 自定义音乐必须停下来，原版音乐放回去。
            bool suppressed = AudioEvents.DispatchBgmForTesting(20031, "BGM_Map_Phoenix", bankId: 2003);

            Assert.False(suppressed);
            Assert.False(AudioService.IsBgmOverrideActive);
            Assert.False(_backend.IsBgmPlaying);
            Assert.Equal(20031, AudioEvents.CurrentBgmEventConfigId);
        }

        [Fact]
        public void BgmStopped_ReleasesOverrideAfterWatchTicks()
        {
            AudioService.RegisterReplacement(10011, new object(), 1.0f);
            AudioEvents.DispatchBgmForTesting(10011, "BGM_Login");
            Assert.True(AudioService.IsBgmOverrideActive);

            // 测试环境没有游戏 BGM 状态: 连续两个监视 tick 未发现播放中的 BGM 即视为已停止。
            AudioEvents.ProcessBgmWatch();
            AudioEvents.ProcessBgmWatch();

            Assert.False(AudioService.IsBgmOverrideActive);
            Assert.Equal(0, AudioEvents.CurrentBgmEventConfigId);
        }

        [Fact]
        public void RulesCleared_ReleasesOverriddenBgm()
        {
            AudioService.RegisterReplacement(10011, new object(), 1.0f);
            AudioEvents.DispatchBgmForTesting(10011, "BGM_Login");
            Assert.True(AudioService.IsBgmOverrideActive);

            AudioService.ClearReplacements();
            AudioEvents.ProcessBgmWatch();

            Assert.False(AudioService.IsBgmOverrideActive);
        }

        [Fact]
        public void BeforeBgmPlay_SubscriberCanProvideCustomClip()
        {
            var clip = new object();
            int seenBankId = 0;

            EventHandler<BgmPlayEventArgs> handler = (sender, e) =>
            {
                seenBankId = e.BankId;
                e.Cancel = true;
                e.CustomClip = clip;
                e.CustomVolume = 0.4f;
                e.Loop = true;
            };

            AudioEvents.BeforeBgmPlay += handler;
            try
            {
                bool suppressed = AudioEvents.DispatchBgmForTesting(20151, "BGM_Map_School", bankId: 2015);

                Assert.True(suppressed);
                Assert.Equal(2015, seenBankId);
                Assert.Same(clip, _backend.CurrentBgmClip);
                Assert.Equal(0.4f, _backend.CurrentBgmVolume);
                Assert.Equal(1, AudioEvents.BgmReplacedCount);
            }
            finally
            {
                AudioEvents.BeforeBgmPlay -= handler;
            }
        }

        [Fact]
        public void BgmRedirectRule_IsIgnoredSoOriginalKeepsPlaying()
        {
            AudioService.RegisterReplacement(AudioReplacementRule.Redirect(10011, 20031));

            bool suppressed = AudioEvents.DispatchBgmForTesting(10011, "BGM_Login");

            Assert.False(suppressed);
            Assert.False(AudioService.IsBgmOverrideActive);
        }

        [Fact]
        public void BgmRules_DoNotLeakIntoOneShotSfxPlayback()
        {
            var clip = new object();
            AudioService.RegisterReplacement(10011, clip, 1.0f);

            AudioEvents.DispatchBgmForTesting(10011, "BGM_Login");

            // BGM 必须是循环覆盖，不能像音效那样走 PlayOneShot。
            Assert.Empty(_backend.PlayedSounds);
            Assert.Same(clip, _backend.CurrentBgmClip);
        }
    }
}
