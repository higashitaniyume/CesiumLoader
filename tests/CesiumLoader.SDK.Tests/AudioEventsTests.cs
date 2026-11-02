using System;
using CesiumLoader.SDK.Audio;
using Xunit;

namespace CesiumLoader.SDK.Tests
{
    public class AudioEventsTests
    {
        private readonly FakeAudioBackend _backend;

        public AudioEventsTests()
        {
            _backend = new FakeAudioBackend();
            AudioService.Backend = _backend;
            AudioService.SyncWithGameSettings = false;
            AudioService.ClearReplacements();
            AudioEvents.ResetStatsForTesting();
        }

        [Fact]
        public void NormalSound_InvokesOriginalCallback()
        {
            bool originalCalled = false;
            int playedId = 0;

            bool intercepted = AudioEvents.DispatchSoundForTesting(1001, id =>
            {
                originalCalled = true;
                playedId = id;
            });

            Assert.False(intercepted);
            Assert.True(originalCalled);
            Assert.Equal(1001, playedId);
            Assert.Equal(1, AudioEvents.InterceptedCount);
            Assert.Equal(0, AudioEvents.ReplacedCount);
            Assert.Equal(0, AudioEvents.MutedCount);
        }

        [Fact]
        public void CancelSound_PreventsOriginalCallback()
        {
            EventHandler<SoundPlayEventArgs> handler = (sender, e) =>
            {
                if (e.EventConfigId == 2002)
                {
                    e.Cancel = true; // 静音
                }
            };

            AudioEvents.BeforeSoundPlay += handler;
            try
            {
                bool originalCalled = false;
                bool intercepted = AudioEvents.DispatchSoundForTesting(2002, _ => originalCalled = true);

                Assert.True(intercepted);
                Assert.False(originalCalled);
                Assert.Equal(1, AudioEvents.MutedCount);
            }
            finally
            {
                AudioEvents.BeforeSoundPlay -= handler;
            }
        }

        [Fact]
        public void ReplaceSound_PlaysCustomClipAndPreventsOriginal()
        {
            var customClip = new object();
            AudioService.RegisterReplacement(3003, customClip, 0.85f);

            bool originalCalled = false;
            bool intercepted = AudioEvents.DispatchSoundForTesting(3003, _ => originalCalled = true);

            Assert.True(intercepted);
            Assert.False(originalCalled);
            Assert.Equal(1, AudioEvents.ReplacedCount);

            Assert.Single(_backend.PlayedSounds);
            var record = _backend.PlayedSounds[0];
            Assert.Same(customClip, record.Clip);
            Assert.Equal(0.85f, record.Volume);
        }

        [Fact]
        public void RedirectSound_CallsTargetConfigId()
        {
            EventHandler<SoundPlayEventArgs> handler = (sender, e) =>
            {
                if (e.EventConfigId == 4004)
                {
                    e.RedirectConfigId = 5005; // 重定向至 5005
                }
            };

            AudioEvents.BeforeSoundPlay += handler;
            try
            {
                int invokedId = 0;
                bool intercepted = AudioEvents.DispatchSoundForTesting(4004, id => invokedId = id);

                Assert.True(intercepted);
                Assert.Equal(5005, invokedId);
            }
            finally
            {
                AudioEvents.BeforeSoundPlay -= handler;
            }
        }

        [Fact]
        public void AfterSoundPlay_FiresOnPlayedSound()
        {
            int afterId = 0;
            Action<int, string> handler = (id, name) => afterId = id;

            AudioEvents.AfterSoundPlay += handler;
            try
            {
                AudioEvents.DispatchSoundForTesting(6006, _ => { });
                Assert.Equal(6006, afterId);
            }
            finally
            {
                AudioEvents.AfterSoundPlay -= handler;
            }
        }
    }
}
