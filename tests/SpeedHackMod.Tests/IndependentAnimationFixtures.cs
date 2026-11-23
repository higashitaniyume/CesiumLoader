namespace SpeedHackMod.Tests
{
    internal enum FakeAnimatorMode { Normal, AnimatePhysics, UnscaledTime }
    internal sealed class FakeAnimator : UnityEngine.Object { public FakeAnimatorMode updateMode { get; set; } = FakeAnimatorMode.UnscaledTime; }
    internal enum FakeDirectorMode { DSPClock, GameTime, UnscaledGameTime, Manual }
    internal sealed class FakeDirector { public FakeDirectorMode timeUpdateMode { get; set; } = FakeDirectorMode.UnscaledGameTime; }
    internal sealed class FakeSkeleton { public bool unscaledTime = true; }
    internal enum FakeMovieStatus { Stop, Dechead, WaitPrep, Playing, StopProcessing }
    internal enum FakeFrameDrop { Infinite = -1, None = 0, Two = 2, Ten = 10 }
    internal sealed class FakeMovieInfo { public uint numAudioStreams; }
    internal sealed class FakeMoviePlayer
    {
        public FakeMovieInfo movieInfo { get; set; } = new();
        public FakeMovieStatus status { get; set; } = FakeMovieStatus.Playing;
        public Action<FakeMovieStatus> statusChangeCallback;
        public float Speed = 1f;
        public int Writes;
        public void SetSpeed(float speed) { Speed = speed; Writes++; }
    }
    internal sealed class FakeMovieController : UnityEngine.Behaviour
    {
        public FakeMoviePlayer player { get; set; } = new();
        public string CurrentVideoKey = "first";
        public FakeFrameDrop maxFrameDrop { get; set; } = FakeFrameDrop.Two;
    }

    internal sealed class FakeBattleQuad : UnityEngine.Behaviour
    {
        public readonly FakeMovieController controller = new();
        public float speed = 1.25f;
        public float _trackSpeed = 1.25f;
        public bool _syncWithTimeline = true;
    }
    internal sealed class FakeCameraBrain { public bool m_IgnoreTimeScale = true; }
    internal sealed class FakeWorldPanel
    {
        private readonly object _ui;
        public FakeWorldPanel(object ui) { _ui = ui; }
        public object ui => throw new Exception("Lazy UIPanel.ui getter must never be invoked");
    }
}

namespace UI
{
    internal sealed class CriMovieManager
    {
        internal sealed class PlayCallback
        {
            private readonly Action onPlayReady;

            internal PlayCallback(Action onPlayReady) => this.onPlayReady = onPlayReady;

            internal void OnStatus(SpeedHackMod.Tests.FakeMovieStatus status)
            {
                if (status == SpeedHackMod.Tests.FakeMovieStatus.Playing) onPlayReady();
            }
        }
    }

    internal sealed class FightWindow
    {
        internal void ShowFightVideoLoader() { }
    }
}

namespace GameLogic
{
    internal static class BattleConfig
    {
        public static float RoleAnimatorSpeed => 1.25f;
    }
}
