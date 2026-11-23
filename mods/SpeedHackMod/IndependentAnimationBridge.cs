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
    /// Discovery is unsorted and staggered. CRI discovery visits active scene objects only.
    /// Reflection metadata is cached; no scene discovery runs more than once per tick.
    /// </summary>
    internal sealed class IndependentAnimationBridge
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int MaxTracked = 4096;
        private readonly Func<string, Type> _findType;
        private readonly Func<Type, object[]> _discover;
        private readonly Func<Type, object[]> _discoverActive;
        private readonly Dictionary<Type, VideoController> _videoControllers = new Dictionary<Type, VideoController>();
        private readonly Dictionary<Type, VideoPlayer> _videoPlayers = new Dictionary<Type, VideoPlayer>();
        private readonly Dictionary<Type, FieldInfo> _playReadyFields = new Dictionary<Type, FieldInfo>();
        private PropertyInfo _fightVideoRate;
        private float _now;
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
            internal MemberInfo Controller;
            internal MemberInfo Rate;
            internal MemberInfo Timeline;
            internal float NextVideoReport;
            internal float NextSkipReport;
        }

        private sealed class Change
        {
            internal object Owner;
            internal object Target;
            internal MemberInfo Access;
            internal object Original;
            internal object Applied;
            internal VideoController VideoController;
            internal VideoPlayer VideoPlayer;
            internal object Controller;
            internal MemberInfo Timeline;
            internal object ClipKey;
            internal object DropOriginal;
            internal object DropApplied;
            internal float OriginalVideoSpeed;
            internal float VideoSpeed;
        }

        private sealed class VideoController
        {
            internal PropertyInfo Player;
            internal MemberInfo Key;
            internal PropertyInfo Drop;
            internal object RaisedDrop;
        }

        private sealed class VideoPlayer
        {
            internal PropertyInfo Status;
            internal PropertyInfo MovieInfo;
            internal MemberInfo Audio;
            internal MethodInfo SetSpeed;
            internal object Playing;
            internal FieldInfo StatusCallback;
            internal bool IsPlaying(object player) => Equals(Status.GetValue(player, null), Playing);
        }

        internal IndependentAnimationBridge(Func<string, Type> findType, Func<Type, object[]> discover, Func<Type, object[]> discoverActive = null)
        {
            _findType = findType;
            _discover = discover;
            _discoverActive = discoverActive ?? discover;
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
            AddVideo("UI.CriManaMovieControllerForGGraph", null, false);
            AddVideo("Core.Unit.BattleCustomVideoQuad", "speed", false);
            AddVideo("Core.Unit.BattleFullScreenQuad", "_trackSpeed", true);
        }

        private void AddVideo(string name, string rate, bool timeline)
        {
            Type type = ResolveType(name);
            if (type == null) return;
            var source = new Source { Type = type, Video = true, Interval = 0.25f };
            if (rate != null)
            {
                source.Controller = FindMember(type, "controller");
                source.Rate = FindMember(type, rate);
                source.Timeline = timeline ? FindMember(type, "_syncWithTimeline") : null;
                if (source.Controller == null || source.Rate == null || (timeline && source.Timeline == null))
                {
                    Report?.Invoke("CRI " + name + "：战斗控制器字段不匹配");
                    return;
                }
            }
            _sources.Add(source);
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
            _now = now;
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
            try { objects = (selected.Video ? _discoverActive : _discover)(selected.Type); }
            catch (Exception e)
            {
                selected.Next = now + 10f;
                if (selected.Video) ReportVideo(selected, "活跃组件查询失败：" + e.Message);
                return;
            }
            if (objects == null) return;
            if (!selected.Reported)
            {
                selected.Reported = true;
                Report?.Invoke((selected.Video ? "CRI 视频通道 " : "独立动画通道 ") + selected.Type.FullName + "：发现 " + objects.Length + " 个场景组件");
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
                    else if (_pendingSource.Video) SynchronizeVideo(owner, _pendingSource, speed);
                    else SynchronizeMode(owner, _pendingSource);
                }
                catch (Exception e)
                {
                    if (_pendingSource.Video && _now >= _pendingSource.NextVideoReport)
                        ReportVideo(_pendingSource, (e.InnerException ?? e).Message);
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
                if (!stale && change.VideoPlayer != null) stale = !IsAlive(change.Controller);
                if (!stale) { _cleanupCursor++; continue; }
                if (_owned.TryGetValue(change.Owner, out var owned) && ReferenceEquals(owned, change))
                    _owned.Remove(change.Owner);
                if (change.VideoPlayer != null) ChangedVideos--;
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

        private void SynchronizeVideo(object owner, Source source, double speed)
        {
            if (!IsActive(owner)) return;
            if (source.Timeline != null && Equals(Read(source.Timeline, owner), true))
            {
                ReportVideo(source, "跳过 Timeline 同步视频");
                return;
            }
            object controller = source.Controller == null ? owner : Read(source.Controller, owner);
            if (!IsActive(controller)) return;
            Type type = controller.GetType();
            if (!_videoControllers.TryGetValue(type, out var metadata))
            {
                metadata = new VideoController
                {
                    Player = type.GetProperty("player", Members),
                    Key = FindMember(type, "CurrentVideoKey"),
                    Drop = type.GetProperty("maxFrameDrop", Members)
                };
                if (metadata.Drop != null && metadata.Drop.CanWrite && metadata.Drop.PropertyType.IsEnum)
                {
                    object raised = Enum.ToObject(metadata.Drop.PropertyType, 10);
                    if (Enum.IsDefined(metadata.Drop.PropertyType, raised)) metadata.RaisedDrop = raised;
                }
                _videoControllers.Add(type, metadata);
            }
            object player = metadata.Player?.GetValue(controller, null);
            if (metadata.Player == null) ReportVideo(source, "跳过：缺少 player 属性");
            if (player == null) return;

            type = player.GetType();
            if (!_videoPlayers.TryGetValue(type, out var api))
            {
                api = new VideoPlayer
                {
                    Status = type.GetProperty("status", Members),
                    MovieInfo = type.GetProperty("movieInfo", Members),
                    StatusCallback = type.GetField("statusChangeCallback", Members),
                    SetSpeed = type.GetMethod("SetSpeed", Members, null, new[] { typeof(float) }, null)
                };
                if (api.Status != null && api.Status.PropertyType.IsEnum && Enum.IsDefined(api.Status.PropertyType, "Playing"))
                    api.Playing = Enum.Parse(api.Status.PropertyType, "Playing");
                if (api.MovieInfo != null) api.Audio = FindMember(api.MovieInfo.PropertyType, "numAudioStreams");
                _videoPlayers.Add(type, api);
            }
            if (api.Playing == null || api.MovieInfo == null || api.Audio == null || api.SetSpeed == null)
            {
                ReportVideo(source, "跳过：CRI 播放状态、音轨或 SetSpeed 接口不匹配");
                return;
            }
            if (!api.IsPlaying(player)) return;
            object info = api.MovieInfo.GetValue(player, null);
            if (info == null)
            {
                ReportVideo(source, "等待 movieInfo");
                return;
            }
            object key = metadata.Key == null ? null : Read(metadata.Key, controller);
            uint audio = Convert.ToUInt32(Read(api.Audio, info));
            if (audio != 0)
            {
                if (_now >= source.NextSkipReport)
                {
                    source.NextSkipReport = _now + 2f;
                    SkippedAudioVideos++;
                    Report?.Invoke("CRI " + source.Type.FullName + "：跳过 " + (key ?? "(无片名)") +
                        "，numAudioStreams=" + audio + "，保留音画同步，SkippedAudioVideos=" + SkippedAudioVideos);
                }
                return;
            }

            float original = source.Rate == null ? ReadUiVideoRate(api, player) : Convert.ToSingle(Read(source.Rate, owner));
            if (float.IsNaN(original) || float.IsInfinity(original) || original <= 0f) return;
            float desired = (float)Math.Min(3.0, original * speed);
            if (desired <= original) return;
            _owned.TryGetValue(owner, out var change);
            if (change == null && _changes.Count >= MaxTracked) return;
            bool sameController = change != null && ReferenceEquals(change.Controller, controller);
            bool changed = !sameController || !ReferenceEquals(change.Target, player) ||
                !Equals(change.ClipKey, key) || Math.Abs(change.VideoSpeed - desired) >= 0.0001f;

            // The game can overwrite SetSpeed after Play even for the same player and clip.
            api.SetSpeed.Invoke(player, new object[] { desired });
            if (change == null)
            {
                change = new Change { Owner = owner };
                _owned.Add(owner, change);
                _changes.Add(change);
                ChangedVideos++;
            }
            if (!sameController) { change.DropOriginal = null; change.DropApplied = null; }
            change.Target = player;
            change.Controller = controller;
            change.VideoController = metadata;
            change.VideoPlayer = api;
            change.Timeline = source.Timeline;
            change.ClipKey = key;
            change.OriginalVideoSpeed = original;
            change.VideoSpeed = desired;

            // The game uses value 10 on Timeline clips. Require a declared enum value.
            var frameDrop = metadata.Drop;
            if (metadata.RaisedDrop != null)
            {
                object cap = frameDrop.GetValue(controller, null);
                int currentCap = Convert.ToInt32(cap);
                if (currentCap >= 0 && currentCap < 10)
                {
                    frameDrop.SetValue(controller, metadata.RaisedDrop, null);
                    change.DropOriginal = cap;
                    change.DropApplied = metadata.RaisedDrop;
                }
            }
            if (changed && _now >= source.NextVideoReport)
                ReportVideo(source, "静音 Playing " + (key ?? "(无片名)") +
                    "，倍率 " + original + "x → " + desired + "x，maxFrameDrop=" +
                    (change.DropApplied ?? (object)"未调整"));
        }

        private float ReadUiVideoRate(VideoPlayer api, object player)
        {
            // CriMovieManager stores ShowFightVideoLoader as onPlayReady in its
            // status callback closure. Read the original battle rate for that caller.
            var statusCallback = api.StatusCallback?.GetValue(player) as Delegate;
            object closure = statusCallback?.Target;
            if (closure == null) return 1f;

            Type type = closure.GetType();
            if (!_playReadyFields.TryGetValue(type, out var field))
            {
                field = type.DeclaringType?.FullName == "UI.CriMovieManager" ? type.GetField("onPlayReady", Members) : null;
                _playReadyFields.Add(type, field);
            }
            var playReady = field?.GetValue(closure) as Delegate;
            if (playReady?.Method.Name != "ShowFightVideoLoader" ||
                playReady.Method.DeclaringType?.FullName != "UI.FightWindow") return 1f;

            if (_fightVideoRate == null)
                _fightVideoRate = _findType("GameLogic.BattleConfig")?.GetProperty("RoleAnimatorSpeed", BindingFlags.Static | BindingFlags.Public);
            return _fightVideoRate == null ? 1f : Convert.ToSingle(_fightVideoRate.GetValue(null, null));
        }

        private void ReportVideo(Source source, string message)
        {
            if (_now < source.NextVideoReport) return;
            source.NextVideoReport = _now + 2f;
            Report?.Invoke("CRI " + source.Type.FullName + "：" + message);
        }

        private static bool IsActive(object owner) =>
            IsAlive(owner) && (!(owner is Behaviour behaviour) || ReadActive(behaviour));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadActive(Behaviour behaviour) => behaviour.isActiveAndEnabled;

        private static void RestoreVideo(Change change)
        {
            object controller = change.Controller;
            if (!IsActive(change.Owner) || !IsActive(controller)) return;
            if (change.Timeline != null && Equals(Read(change.Timeline, change.Owner), true)) return;
            var metadata = change.VideoController;
            if (change.DropApplied != null && Equals(metadata.Drop.GetValue(controller, null), change.DropApplied))
                metadata.Drop.SetValue(controller, change.DropOriginal, null);

            var api = change.VideoPlayer;
            if (!ReferenceEquals(metadata.Player.GetValue(controller, null), change.Target) ||
                !api.IsPlaying(change.Target) ||
                (metadata.Key != null && !Equals(Read(metadata.Key, controller), change.ClipKey))) return;
            object info = api.MovieInfo.GetValue(change.Target, null);
            if (info != null && Convert.ToUInt32(Read(api.Audio, info)) == 0)
                api.SetSpeed.Invoke(change.Target, new object[] { change.OriginalVideoSpeed });
        }


        internal void Restore()
        {
            foreach (var change in _changes)
                try
                {
                    if (change.VideoPlayer != null) RestoreVideo(change);
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static object[] FindActive(Type type)
        {
            return UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        }
    }
}
