namespace FairyGUI;
public class GObject
{
    public bool isDisposed { get; set; }
    public bool visible { get; set; } = true;
}
public class GComponent : GObject
{
    private readonly List<Transition> _transitions = new();
    private readonly List<GObject> _children = new();
    public int numChildren => _children.Count;
    public GObject GetChildAt(int index) => _children[index];
    public void AddChild(GObject child) => _children.Add(child);
    public void AddTransition(Transition transition) => _transitions.Add(transition);
}
public class GRoot : GComponent
{
    internal static GRoot _inst;
    public static void SetExisting(GRoot root) => _inst = root;
}
public interface ITweenListener { }
public class Transition : ITweenListener
{
    private readonly GComponent _owner;
    public Transition(GComponent owner = null) { _owner = owner; }
    private bool _ignore = true;
    public int Writes;
    public bool ignoreEngineTimeScale { get => _ignore; set { _ignore = value; Writes++; } }
    public float Advance(float unscaledDt, float scale) => unscaledDt * (_ignore ? 1 : scale);
}
public class GTweener
{
    private readonly ITweenListener _listener;
    public object target { get; }
    public bool completed { get; set; }
    public GTweener(ITweenListener listener, object target) { _listener = listener; this.target = target; }
}
public static class FakeTweenManager
{
    private static GTweener[] _activeTweens = Array.Empty<GTweener>();
    private static int _totalActiveTweens;
    public static void SetActive(params GTweener[] tweens) { _activeTweens = tweens; _totalActiveTweens = tweens.Length; }
}
public interface IAnimationGear { bool ignoreEngineTimeScale { get; set; } }
public class GMovieClip : GObject, IAnimationGear
{
    public bool ignoreEngineTimeScale { get; set; } = true;
}
