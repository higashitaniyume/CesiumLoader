namespace SpeedHackMod.Tests;
internal enum FakeAnimatorMode { Normal, AnimatePhysics, UnscaledTime }
internal sealed class FakeAnimator : UnityEngine.Object { public FakeAnimatorMode updateMode { get; set; } = FakeAnimatorMode.UnscaledTime; }
internal enum FakeDirectorMode { DSPClock, GameTime, UnscaledGameTime, Manual }
internal sealed class FakeDirector { public FakeDirectorMode timeUpdateMode { get; set; } = FakeDirectorMode.UnscaledGameTime; }
internal sealed class FakeSkeleton { public bool unscaledTime = true; }
internal sealed class FakeMovieInfo { public uint numAudioStreams; }
internal sealed class FakeMoviePlayer
{
    public FakeMovieInfo movieInfo { get; } = new();
    public float Speed = 1f;
    public int Writes;
    public void SetSpeed(float speed) { Speed = speed; Writes++; }
}
internal sealed class FakeMovieController : UnityEngine.Object { public FakeMoviePlayer player { get; set; } = new(); }
internal sealed class FakeCameraBrain { public bool m_IgnoreTimeScale = true; }
internal sealed class FakeWorldPanel
{
    private readonly object _ui;
    public FakeWorldPanel(object ui) { _ui = ui; }
    public object ui => throw new Exception("Lazy UIPanel.ui getter must never be invoked");
}
