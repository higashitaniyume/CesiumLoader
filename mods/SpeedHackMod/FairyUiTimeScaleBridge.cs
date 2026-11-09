using System;
using System.Collections.Generic;
using System.Reflection;
using FairyGUI;
using CesiumLoader.SDK.Runtime;

namespace SpeedHackMod
{
    /// <summary>
    /// FairyGUI transitions default to ignoring Unity timeScale in this game.
    /// Make visible UI animation respect the engine scale, so Unity acceleration
    /// also covers card/window transitions without accelerating system clocks.
    /// Walk a bounded number of nodes per update; do not scan the whole scene,
    /// allocate child arrays, or create GRoot when the UI is not ready.
    /// </summary>
    internal sealed class FairyUiTimeScaleBridge
    {
        private const int WorkPerTick = 64;
        private const int MaxTracked = 4096;
        private const int MaxWalkNodes = 16384;
        private static readonly FieldInfo RootInstance = typeof(GRoot).GetField("_inst", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo Transitions = typeof(GComponent).GetField("_transitions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private struct Frame
        {
            internal GObject Node;
            internal int Phase;
            internal int Index;
            internal IList<Transition> Transitions;
        }

        private readonly List<Frame> _stack = new List<Frame>(32);
        private readonly Dictionary<Transition, GComponent> _transitions = new Dictionary<Transition, GComponent>();
        private readonly Dictionary<GObject, IAnimationGear> _gears = new Dictionary<GObject, IAnimationGear>();
        private float _nextSweepAt;
        private int _walkNodes;
        private GRoot _lastRoot;
        private int _lastRootChildren = -1;
        private readonly List<GObject> _externalRoots = new List<GObject>();
        private readonly HashSet<GObject> _externalSet = new HashSet<GObject>();
        private readonly Type _tweenManagerOverride;
        internal FairyUiTimeScaleBridge(Type tweenManagerOverride = null) { _tweenManagerOverride = tweenManagerOverride; }
        private bool _tweensResolved;
        private FieldInfo _activeTweens;
        private FieldInfo _activeTweenCount;
        private static readonly FieldInfo TweenListener = typeof(GTweener).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo TransitionOwner = typeof(Transition).GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private struct TweenToken { internal object Target; internal Transition Transition; }
        private readonly Dictionary<GTweener, TweenToken> _tweenOwners = new Dictionary<GTweener, TweenToken>();
        private int _tweenCursor;
        internal int ExternalRoots => _externalRoots.Count;
        internal bool HasState => _stack.Count > 0 || _externalRoots.Count > 0 || _transitions.Count > 0 || _gears.Count > 0;
        internal int TrackedTransitions => _transitions.Count;
        internal int TrackedAnimations => _gears.Count;

        internal void RegisterExternalRoot(object value)
        {
            var root = value as GObject;
            if (root == null || root.isDisposed || _externalRoots.Count >= 512 || !_externalSet.Add(root)) return;
            _externalRoots.Add(root);
            _stack.Clear();
            _nextSweepAt = 0f;
        }

        internal void Tick(float now)
        {
            SynchronizeLiveTransitions();
            if (RootInstance == null || Transitions == null)
                throw new MissingFieldException("FairyGUI root/transition metadata unavailable");
            var root = RootInstance.GetValue(null) as GRoot;
            if (root == null || root.isDisposed) return;
            if (!ReferenceEquals(root, _lastRoot) || root.numChildren != _lastRootChildren)
            {
                // A short newly opened window must not wait behind an old UI sweep.
                _stack.Clear();
                _nextSweepAt = 0f;
                _lastRoot = root;
                _lastRootChildren = root.numChildren;
            }
            if (_stack.Count == 0)
            {
                if (now < _nextSweepAt) return;
                _stack.Add(new Frame { Node = root });
                // UIPanel world-space UI is not parented under GRoot. Battle dice and
                // combat player panels live here and need the same animation treatment.
                foreach (var external in _externalRoots)
                    if (external != null && !external.isDisposed) _stack.Add(new Frame { Node = external });
                _walkNodes = 0;
            }

            for (int work = 0; work < WorkPerTick && _stack.Count > 0; work++)
            {
                int top = _stack.Count - 1;
                Frame frame = _stack[top];
                GObject node = frame.Node;
                if (node == null || node.isDisposed || !node.visible)
                {
                    _stack.RemoveAt(top);
                    continue;
                }
                if (frame.Phase == 0)
                {
                    if (++_walkNodes > MaxWalkNodes) { _stack.Clear(); break; }
                    var gear = node as IAnimationGear;
                    if (gear != null && gear.ignoreEngineTimeScale && _gears.Count < MaxTracked)
                    {
                        if (!_gears.ContainsKey(node)) _gears.Add(node, gear);
                        gear.ignoreEngineTimeScale = false;
                    }
                    var component = node as GComponent;
                    if (component == null) { _stack.RemoveAt(top); continue; }
                    frame.Transitions = Transitions.GetValue(component) as IList<Transition>;
                    frame.Phase = 1;
                    _stack[top] = frame;
                }
                else if (frame.Phase == 1)
                {
                    if (frame.Transitions != null && frame.Index < frame.Transitions.Count)
                    {
                        Transition transition = frame.Transitions[frame.Index++];
                        _stack[top] = frame;
                        if (transition != null && transition.ignoreEngineTimeScale && _transitions.Count < MaxTracked)
                        {
                            if (!_transitions.ContainsKey(transition)) _transitions.Add(transition, (GComponent)node);
                            transition.ignoreEngineTimeScale = false;
                        }
                    }
                    else
                    {
                        frame.Phase = 2; frame.Index = ((GComponent)node).numChildren - 1; _stack[top] = frame;
                    }
                }
                else
                {
                    var component = (GComponent)node;
                    if (frame.Index >= component.numChildren) frame.Index = component.numChildren - 1;
                    if (frame.Index >= 0)
                    {
                        var child = component.GetChildAt(frame.Index--);
                        _stack[top] = frame;
                        _stack.Add(new Frame { Node = child });
                    }
                    else _stack.RemoveAt(top);
                }
            }
            if (_stack.Count == 0) _nextSweepAt = now + 0.1f;
        }

        private void SynchronizeLiveTransitions()
        {
            if (!_tweensResolved)
            {
                _tweensResolved = true;
                Type manager = _tweenManagerOverride ?? RuntimeAssemblyService.FindType("FairyGUI.TweenManager");
                if (manager != null)
                {
                    _activeTweens = manager.GetField("_activeTweens", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    _activeTweenCount = manager.GetField("_totalActiveTweens", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                }
            }
            if (_activeTweens == null || _activeTweenCount == null || TweenListener == null || TransitionOwner == null) return;
            var tweens = _activeTweens.GetValue(null) as GTweener[];
            if (tweens == null) return;
            int count = Math.Min((int)_activeTweenCount.GetValue(null), tweens.Length);
            if (count <= 0) return;
            int budget = Math.Min(count, 128);
            for (int step = 0; step < budget; step++)
            {
                GTweener tween = tweens[(_tweenCursor + step) % count];
                if (tween == null) continue;
                if (tween.completed) { _tweenOwners.Remove(tween); continue; }
                object target = tween.target;
                Transition transition;
                if (target != null && _tweenOwners.TryGetValue(tween, out var token) && ReferenceEquals(target, token.Target))
                    transition = token.Transition;
                else
                {
                    transition = TweenListener.GetValue(tween) as Transition;
                    if (target != null)
                    {
                        if (_tweenOwners.Count >= 1024) _tweenOwners.Clear();
                        _tweenOwners[tween] = new TweenToken { Target = target, Transition = transition };
                    }
                }
                if (transition == null || !transition.ignoreEngineTimeScale || _transitions.Count >= MaxTracked) continue;
                var owner = TransitionOwner.GetValue(transition) as GComponent;
                if (owner == null || owner.isDisposed) continue;
                if (!_transitions.ContainsKey(transition)) _transitions.Add(transition, owner);
                // Change the transition's clock once. It propagates to its live tweens
                // and later child tweens, without seeking, killing, or firing callbacks.
                transition.ignoreEngineTimeScale = false;
            }
            _tweenCursor = (_tweenCursor + budget) % count;
        }

        internal void Restore()
        {
            // Transition setters also restore flags on their active tweens.
            foreach (var pair in _transitions)
                try
                {
                    if (!pair.Value.isDisposed && !pair.Key.ignoreEngineTimeScale)
                        pair.Key.ignoreEngineTimeScale = true;
                }
                catch { }
            foreach (var pair in _gears)
                try
                {
                    if (!pair.Key.isDisposed && !pair.Value.ignoreEngineTimeScale)
                        pair.Value.ignoreEngineTimeScale = true;
                }
                catch { }
            _transitions.Clear();
            _gears.Clear();
            _stack.Clear();
            _nextSweepAt = 0f;
            _lastRoot = null;
            _lastRootChildren = -1;
            _externalRoots.Clear();
            _externalSet.Clear();
            _tweenOwners.Clear();
            _tweenCursor = 0;
        }
    }
}
