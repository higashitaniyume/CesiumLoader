using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

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
        private bool _resolved;
        private float _nextDiscoveryAt;
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
            _resolved = true;
            Add("UnityEngine.Animator", "updateMode", "UnscaledTime", "Normal", false, 0.25f);
            Add("UnityEngine.Playables.PlayableDirector", "timeUpdateMode", "UnscaledGameTime", "GameTime", false, 0.5f);
            Add("Spine.Unity.SkeletonAnimation", "unscaledTime", null, null, true, 1f);
            Add("Spine.Unity.SkeletonGraphic", "unscaledTime", null, null, true, 1f);
            Add("Cinemachine.CinemachineBrain", "m_IgnoreTimeScale", null, null, true, 0.5f);
            Type panel = _findType("FairyGUI.UIPanel");
            MemberInfo panelUi = panel != null ? FindMember(panel, "_ui") : null;
            if (panelUi != null) _sources.Add(new Source { Type = panel, Panel = true, Access = panelUi, Interval = 0.25f });
            Type video = _findType("UI.CriManaMovieControllerForGGraph");
            if (video != null) _sources.Add(new Source { Type = video, Video = true, Interval = 0.5f });
        }

        private void Add(string name, string member, string from, string to, bool boolean, float interval)
        {
            Type type = _findType(name);
            if (type == null) return;
            MemberInfo access = FindMember(type, member);
            if (access != null) _sources.Add(new Source { Type = type, Member = member, Access = access, From = from, To = to, Boolean = boolean, Interval = interval });
        }

        internal void Tick(float now, double speed)
        {
            if (!_resolved) Resolve();
            if (now < _nextDiscoveryAt || _changes.Count >= MaxTracked) return;
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
            // Discovery itself is one native type-index query. Bound subsequent work too.
            int count = Math.Min(objects.Length, 512);
            for (int i = 0; i < count && _changes.Count < MaxTracked; i++)
            {
                object owner = objects[i];
                if (owner == null) continue;
                try
                {
                    if (selected.Panel) RegisterUiRoot?.Invoke(Read(selected.Access, owner));
                    else if (selected.Video) SynchronizeVideo(owner, speed);
                    else SynchronizeMode(owner, selected);
                }
                catch
                {
                    // Destroyed components and clips preparing asynchronously are normal;
                    // retry only at the staggered discovery cadence, not every render frame.
                }
            }
        }

        private void SynchronizeMode(object owner, Source source)
        {
            object current = Read(source.Access, owner);
            bool matches = source.Boolean ? current is bool && (bool)current : current != null && current.ToString() == source.From;
            if (!matches) return;
            object desired = source.Boolean ? (object)false : Enum.Parse(current.GetType(), source.To);
            if (!_owned.TryGetValue(owner, out var change))
            {
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
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static object[] Find(Type type)
        {
            return UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None);
        }
    }
}
