using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using CesiumLoader.SDK.Runtime;

namespace SpeedHackMod
{
    /// <summary>
    /// Adapt known independent animation clocks instead of scaling process clocks.
    /// Discovery is unsorted, staggered, and includes inactive pooled scene objects.
    /// Reflection metadata is cached; no scene discovery runs more than once per tick.
    /// </summary>
    internal sealed class IndependentAnimationBridge
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int MaxTracked = 4096;
        private readonly Func<string, Type> _findType;
        private readonly Func<Type, object[]> _discover;
        private readonly List<Source> _sources = new List<Source>();
        private readonly List<Change> _changes = new List<Change>();
        private readonly Dictionary<object, Change> _owned = new Dictionary<object, Change>();
        private readonly HashSet<string> _resolved = new HashSet<string>();
        private float _nextResolveAt;
        private float _nextDiscoveryAt;
        private Source _pendingSource;
        private object[] _pendingObjects;
        private int _pendingIndex;
        private int _cleanupCursor;
        internal int ChangedComponents => _changes.Count;
        internal int ChangedVideos { get; private set; }
        internal int SkippedAudioVideos { get; private set; }
        internal Action<string> Report { get; set; }
        internal Action<object> RegisterUiRoot { get; set; }

        private sealed class Source
        {
            internal Type Type;
            internal string Member;
            internal string From;
            internal string To;
            internal bool Boolean;
            internal bool Video;
            internal float Interval;
            internal float Next;
            internal MemberInfo Access;
            internal bool Reported;
            internal bool Panel;
        }

        private sealed class Change
        {
            internal object Owner;
            internal object Target;
            internal MemberInfo Access;
            internal object Original;
            internal object Applied;
            internal MethodInfo SetVideoSpeed;
            internal PropertyInfo PlayerProperty;
            internal float VideoSpeed;
        }

        internal IndependentAnimationBridge(Func<string, Type> findType, Func<Type, object[]> discover)
        {
            _findType = findType;
            _discover = discover;
        }

        private void Resolve()
        {
            Add("UnityEngine.Animator", "updateMode", "UnscaledTime", "Normal", false, 0.25f);
            Add("UnityEngine.Playables.PlayableDirector", "timeUpdateMode", "UnscaledGameTime", "GameTime", false, 0.5f);
            Add("Spine.Unity.SkeletonAnimation", "unscaledTime", null, null, true, 1f);
            Add("Spine.Unity.SkeletonGraphic", "unscaledTime", null, null, true, 1f);
            Add("Cinemachine.CinemachineBrain", "m_IgnoreTimeScale", null, null, true, 0.5f);
            Type panel = ResolveType("FairyGUI.UIPanel");
            MemberInfo panelUi = panel != null ? FindMember(panel, "_ui") : null;
            if (panelUi != null) _sources.Add(new Source { Type = panel, Panel = true, Access = panelUi, Interval = 0.25f });
            Type video = ResolveType("UI.CriManaMovieControllerForGGraph");
            if (video != null) _sources.Add(new Source { Type = video, Video = true, Interval = 0.5f });
        }

        private void Add(string name, string member, string from, string to, bool boolean, float interval)
        {
            Type type = ResolveType(name);
            if (type == null) return;
            MemberInfo access = FindMember(type, member);
            if (access != null) _sources.Add(new Source { Type = type, Member = member, Access = access, From = from, To = to, Boolean = boolean, Interval = interval });
        }

        private Type ResolveType(string name)
        {
            if (_resolved.Contains(name)) return null;
            Type type;
            try { type = _findType(name); }
            catch { return null; }
            if (type != null) _resolved.Add(name);
            return type;
        }

        internal void Tick(float now, double speed)
        {
            // Retry only missing optional types, not every render frame.
            if (now >= _nextResolveAt) { _nextResolveAt = now + 5f; Resolve(); }
            PruneDestroyed();
            if (_pendingObjects != null) { ProcessBatch(speed); return; }
            if (now < _nextDiscoveryAt) return;
            Source selected = null;
            foreach (var source in _sources)
                if (now >= source.Next && (selected == null || source.Next < selected.Next)) selected = source;
            if (selected == null) return;
            selected.Next = now + selected.Interval;
            _nextDiscoveryAt = now + 0.05f;
            object[] objects;
            try { objects = _discover(selected.Type); }
            catch { selected.Next = now + 10f; return; }
            if (objects == null) return;
            if (!selected.Reported)
            {
                selected.Reported = true;
                Report?.Invoke("独立动画通道 " + selected.Type.FullName + "：发现 " + objects.Length + " 个场景组件");
            }
            _pendingSource = selected;
            _pendingObjects = objects;
            _pendingIndex = 0;
            ProcessBatch(speed);
        }

        private void ProcessBatch(double speed)
        {
            // Finish a query across frames instead of repeatedly visiting its first
            // 512 entries. Release each processed reference, including destroyed ones.
            int end = Math.Min(_pendingObjects.Length, _pendingIndex + 512);
            while (_pendingIndex < end)
            {
                object owner = _pendingObjects[_pendingIndex];
                _pendingObjects[_pendingIndex++] = null;
                if (!IsAlive(owner)) continue;
                try
                {
                    if (_pendingSource.Panel) RegisterUiRoot?.Invoke(Read(_pendingSource.Access, owner));
                    else if (_pendingSource.Video) SynchronizeVideo(owner, speed);
                    else SynchronizeMode(owner, _pendingSource);
                }
                catch
                {
                    // Destroyed components and clips preparing asynchronously are normal;
                    // retry only at the staggered discovery cadence, not every render frame.
                }
            }
            if (_pendingIndex >= _pendingObjects.Length)
            {
                _pendingObjects = null;
                _pendingSource = null;
                _pendingIndex = 0;
            }
        }

        private void PruneDestroyed()
        {
            int count = Math.Min(16, _changes.Count);
            for (int i = 0; i < count && _changes.Count > 0; i++)
            {
                if (_cleanupCursor >= _changes.Count) _cleanupCursor = 0;
                var change = _changes[_cleanupCursor];
                bool stale = !IsAlive(change.Owner);
                if (!stale && change.SetVideoSpeed != null)
                    try { stale = !ReferenceEquals(change.PlayerProperty.GetValue(change.Owner, null), change.Target); }
                    catch { }
                if (!stale) { _cleanupCursor++; continue; }
                if (_owned.TryGetValue(change.Owner, out var owned) && ReferenceEquals(owned, change))
                    _owned.Remove(change.Owner);
                if (change.SetVideoSpeed != null) ChangedVideos--;
                int last = _changes.Count - 1;
                _changes[_cleanupCursor] = _changes[last];
                _changes.RemoveAt(last);
            }
        }

        private static bool IsAlive(object value)
        {
            if (value == null) return false;
            var unityObject = value as UnityEngine.Object;
            return ReferenceEquals(unityObject, null) || unityObject != null;
        }

        private void SynchronizeMode(object owner, Source source)
        {
            object current = Read(source.Access, owner);
            bool matches = source.Boolean ? current is bool && (bool)current : current != null && current.ToString() == source.From;
            if (!matches) return;
            object desired = source.Boolean ? (object)false : Enum.Parse(current.GetType(), source.To);
            if (!_owned.TryGetValue(owner, out var change))
            {
                if (_changes.Count >= MaxTracked) return;
                Write(source.Access, owner, desired);
                change = new Change { Owner = owner, Target = owner, Access = source.Access, Original = current, Applied = desired };
                _owned.Add(owner, change); _changes.Add(change);
            }
            else Write(source.Access, owner, desired);
        }

        private void SynchronizeVideo(object owner, double speed)
        {
            PropertyInfo playerProperty = owner.GetType().GetProperty("player", Members);
            object player = playerProperty?.GetValue(owner, null);
            if (player == null) return;
            if (_owned.TryGetValue(owner, out var existing) && !ReferenceEquals(existing.Target, player))
            {
                _owned.Remove(owner); // old disposed player stays only in the bounded restoration list
                existing = null;
            }
            object info = player.GetType().GetProperty("movieInfo", Members)?.GetValue(player, null);
            if (info == null) return;
            MemberInfo audioMember = FindMember(info.GetType(), "numAudioStreams");
            if (audioMember == null) return; // unknown audio layout: do not change a playing clip
            if (Convert.ToUInt32(Read(audioMember, info)) != 0)
            {
                SkippedAudioVideos++;
                return;
            }
            float desired = (float)Math.Min(3.0, Math.Max(1.0, speed));
            if (existing != null && Math.Abs(existing.VideoSpeed - desired) < 0.0001f) return;
            if (existing == null && _changes.Count >= MaxTracked) return;
            MethodInfo setter = player.GetType().GetMethod("SetSpeed", Members, null, new[] { typeof(float) }, null);
            if (setter == null) return;
            setter.Invoke(player, new object[] { desired });
            if (existing == null)
            {
                // CriMovieManager.Play/SetAsset never sets a custom speed for this UI
                // controller in the inspected game source; its normal rate is 1x.
                // Full-screen timeline controllers with custom _trackSpeed are excluded.
                existing = new Change { Owner = owner, Target = player, SetVideoSpeed = setter, PlayerProperty = playerProperty };
                _owned[owner] = existing; _changes.Add(existing); ChangedVideos++;
            }
            existing.VideoSpeed = desired;
        }

        internal void Restore()
        {
            foreach (var change in _changes)
                try
                {
                    if (change.SetVideoSpeed != null)
                    {
                        // Do not apply a stale player's reset to a newly-created player.
                        if (ReferenceEquals(change.PlayerProperty.GetValue(change.Owner, null), change.Target))
                            change.SetVideoSpeed.Invoke(change.Target, new object[] { 1f });
                    }
                    else if (Equals(Read(change.Access, change.Target), change.Applied))
                        Write(change.Access, change.Target, change.Original);
                }
                catch { }
            _changes.Clear(); _owned.Clear();
            ChangedVideos = 0; SkippedAudioVideos = 0;
            _nextDiscoveryAt = 0f;
            _pendingObjects = null;
            _pendingSource = null;
            _pendingIndex = 0;
            _cleanupCursor = 0;
            foreach (var source in _sources) source.Next = 0f;
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            return (MemberInfo)type.GetProperty(name, Members) ?? type.GetField(name, Members);
        }
        private static object Read(MemberInfo access, object target)
        {
            var property = access as PropertyInfo;
            return property != null ? property.GetValue(target, null) : ((FieldInfo)access).GetValue(target);
        }
        private static void Write(MemberInfo access, object target, object value)
        {
            var property = access as PropertyInfo;
            if (property != null) property.SetValue(target, value, null);
            else ((FieldInfo)access).SetValue(target, value);
        }
    }

    internal static class UnityAnimationDiscovery
    {
        internal static Type FindType(string name)
        {
            // The SDK's global lookup caches misses. The assembly-specific overload
            // bypasses that cache without invalidating other mods' metadata.
            foreach (var assembly in RuntimeAssemblyService.GetAssemblies())
            {
                try
                {
                    Type type = RuntimeAssemblyService.FindType(assembly.GetName().Name, name);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static object[] Find(Type type)
        {
            return UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None);
        }
    }
}
