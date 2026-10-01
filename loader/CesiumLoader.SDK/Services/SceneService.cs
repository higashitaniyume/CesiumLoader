using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CesiumLoader.SDK
{
    /// <summary>场景订阅句柄。</summary>
    public sealed class SceneSubscription : IDisposable
    {
        private Action _dispose;

        internal SceneSubscription(Action dispose) { _dispose = dispose; }

        /// <summary>是否已退订。</summary>
        public bool IsDisposed { get { return _dispose == null; } }

        /// <summary>退订。</summary>
        public void Dispose()
        {
            var d = Interlocked.Exchange(ref _dispose, null);
            if (d != null) { try { d(); } catch { } }
        }
    }

    /// <summary>
    /// 场景服务: 场景查询 + 场景事件 + "等场景加载完成"。
    ///
    /// 关键设计:
    ///  - 真实订阅 UnityEngine.SceneManagement.SceneManager 的 sceneLoaded /
    ///    sceneUnloaded / activeSceneChanged(这些事件在主线程触发), 再转发给 mod。
    ///  - 额外每帧兜底检测活动场景变化: 某些切场景方式不触发 Unity 事件。
    ///  - 订阅按 (回调, 归属) 去重, 防止重复事件; 退订按 id 精确移除, 防止误删与泄漏。
    ///  - 只有存在订阅者时才挂 Unity 事件, 避免 SDK 常驻开销。
    /// </summary>
    public static class SceneService
    {
        private sealed class Sub<TDelegate> where TDelegate : class
        {
            public long Id;
            public TDelegate Callback;
            public ModContext Owner;
        }

        private readonly struct WaitEntry
        {
            public readonly long Id;
            public readonly string SceneName;
            public readonly Action<Scene> Callback;
            public readonly ModContext Owner;
            public readonly float Deadline; // <=0 表示不超时

            public WaitEntry(long id, string sceneName, Action<Scene> callback, ModContext owner, float deadline)
            {
                Id = id;
                SceneName = sceneName;
                Callback = callback;
                Owner = owner;
                Deadline = deadline;
            }
        }

        private static readonly object _lock = new object();
        private static readonly List<Sub<Action<Scene, LoadSceneMode>>> _loaded = new List<Sub<Action<Scene, LoadSceneMode>>>();
        private static readonly List<Sub<Action<Scene>>> _unloaded = new List<Sub<Action<Scene>>>();
        private static readonly List<Sub<Action<Scene, Scene>>> _activeChanged = new List<Sub<Action<Scene, Scene>>>();
        private static readonly List<WaitEntry> _waits = new List<WaitEntry>();

        private static Sub<Action<Scene, LoadSceneMode>>[] _loadedSnapshot = new Sub<Action<Scene, LoadSceneMode>>[0];
        private static Sub<Action<Scene>>[] _unloadedSnapshot = new Sub<Action<Scene>>[0];
        private static Sub<Action<Scene, Scene>>[] _activeSnapshot = new Sub<Action<Scene, Scene>>[0];

        private static long _nextId;
        private static int _installed;
        private static int _unityHooked;

        private static bool _hasLastScene;
        private static int _lastSceneHandle = -1;
        private static string _lastSceneName;

        private static string _cachedActiveSceneName;
        private static int _cachedActiveSceneHandle = -1;

        // Unity 事件处理器必须保持引用(退订时要用)
        private static readonly UnityEngine.Events.UnityAction<Scene, LoadSceneMode> _onSceneLoaded = HandleUnitySceneLoaded;
        private static readonly UnityEngine.Events.UnityAction<Scene> _onSceneUnloaded = HandleUnitySceneUnloaded;
        private static readonly UnityEngine.Events.UnityAction<Scene, Scene> _onActiveSceneChanged = HandleUnityActiveSceneChanged;

        // =====================================================================
        // 状态
        // =====================================================================

        /// <summary>SceneLoaded 订阅数。</summary>
        public static int SceneLoadedSubscriberCount { get { return Volatile.Read(ref _loadedSnapshot).Length; } }

        /// <summary>SceneUnloaded 订阅数。</summary>
        public static int SceneUnloadedSubscriberCount { get { return Volatile.Read(ref _unloadedSnapshot).Length; } }

        /// <summary>ActiveSceneChanged 订阅数。</summary>
        public static int ActiveSceneChangedSubscriberCount { get { return Volatile.Read(ref _activeSnapshot).Length; } }

        /// <summary>已转发的场景加载次数。</summary>
        public static long ForwardedLoadCount { get { return Interlocked.Read(ref _forwardedLoads); } }
        private static long _forwardedLoads;

        /// <summary>已转发的活动场景切换次数(含兜底检测)。</summary>
        public static long ForwardedActiveChangeCount { get { return Interlocked.Read(ref _forwardedActive); } }
        private static long _forwardedActive;

        /// <summary>等场景超时次数。</summary>
        public static long WaitTimeoutCount { get { return Interlocked.Read(ref _waitTimeouts); } }
        private static long _waitTimeouts;

        // =====================================================================
        // 查询
        // =====================================================================

        /// <summary>当前活动场景。</summary>
        public static Scene GetActiveScene()
        {
            Guard("GetActiveScene");
            return UnityCall.GetActiveScene();
        }

        /// <summary>当前活动场景名(带缓存, 场景切换时自动失效)。</summary>
        public static string GetActiveSceneName()
        {
            Guard("GetActiveSceneName");
            try
            {
                var scene = UnityCall.GetActiveScene();
                if (_cachedActiveSceneName != null && _cachedActiveSceneHandle == UnityCall.SceneHandle(scene))
                    return _cachedActiveSceneName;

                _cachedActiveSceneName = UnityCall.SceneName(scene);
                _cachedActiveSceneHandle = UnityCall.SceneHandle(scene);
                return _cachedActiveSceneName;
            }
            catch { return null; }
        }

        /// <summary>当前活动场景句柄(0 = 无效场景, 离线时也返回 0 而不抛异常)。</summary>
        public static int GetActiveSceneHandle()
        {
            Guard("GetActiveSceneHandle");
            return UnityCall.SceneHandle(UnityCall.GetActiveScene());
        }

        /// <summary>已加载场景数量。</summary>
        public static int GetSceneCount()
        {
            Guard("GetSceneCount");
            return UnityCall.SceneCount();
        }

        /// <summary>按索引取场景。</summary>
        public static Scene GetSceneAt(int index)
        {
            Guard("GetSceneAt");
            try
            {
                if (index < 0 || index >= UnityCall.SceneCount()) return default(Scene);
                return UnityCall.GetSceneAt(index);
            }
            catch { return default(Scene); }
        }

        /// <summary>按名字取场景(未加载时返回无效 Scene)。</summary>
        public static Scene GetSceneByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return default(Scene);
            Guard("GetSceneByName");
            return UnityCall.GetSceneByName(name);
        }

        /// <summary>场景是否已加载。</summary>
        public static bool IsSceneLoaded(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                var scene = GetSceneByName(name);
                return UnityCall.SceneIsValid(scene) && UnityCall.SceneIsLoaded(scene);
            }
            catch { return false; }
        }

        /// <summary>已加载场景名列表。</summary>
        public static List<string> GetLoadedSceneNames()
        {
            var result = new List<string>();
            int count = GetSceneCount();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var scene = UnityCall.GetSceneAt(i);
                    result.Add(UnityCall.SceneName(scene));
                }
                catch { }
            }
            return result;
        }

        /// <summary>活动场景的根对象。</summary>
        public static GameObject[] GetRootGameObjects()
        {
            return GetRootGameObjects(GetActiveScene());
        }

        /// <summary>指定场景的根对象。</summary>
        public static GameObject[] GetRootGameObjects(Scene scene)
        {
            Guard("GetRootGameObjects");
            try
            {
                if (!UnityCall.SceneIsValid(scene) || !UnityCall.SceneIsLoaded(scene)) return new GameObject[0];
                return UnityCall.SceneRootGameObjects(scene);
            }
            catch { return new GameObject[0]; }
        }

        /// <summary>
        /// 在活动场景中查找所有 T 组件(含未激活)。
        /// 只在场景根对象下递归, 不扫描全工程资源, 比 Object.FindObjectsOfType 可控。
        /// 按需调用, 不要每帧调用。
        /// </summary>
        public static List<T> FindInScene<T>() where T : Component
        {
            var result = new List<T>();
            Guard("FindInScene<" + typeof(T).Name + ">");
            try
            {
                var roots = GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    var root = roots[i];
                    if (root == null) continue;
                    var found = root.GetComponentsInChildren(typeof(T), true);
                    if (found == null) continue;
                    for (int j = 0; j < found.Length; j++)
                    {
                        var item = found[j] as T;
                        if (item != null) result.Add(item);
                    }
                }
            }
            catch (Exception e)
            {
                SdkLog.Error("SCENE", "FindInScene<" + typeof(T).Name + "> 失败: " + e.Message);
            }
            return result;
        }

        /// <summary>在活动场景中按名字查找(递归, 含未激活)。</summary>
        public static GameObject FindInScene(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Guard("FindInScene");
            try
            {
                var roots = GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    var found = FindRecursive(roots[i], name);
                    if (found != null) return found;
                }
            }
            catch (Exception e)
            {
                SdkLog.Error("SCENE", "FindInScene 失败: " + e.Message);
            }
            return null;
        }

        /// <summary>清空场景查询缓存(场景切换时由 SDK 自动调用)。</summary>
        public static void InvalidateCaches()
        {
            _cachedActiveSceneName = null;
            _cachedActiveSceneHandle = -1;
        }

        // =====================================================================
        // 订阅
        // =====================================================================

        /// <summary>订阅场景加载完成(主线程)。</summary>
        public static SceneSubscription SubscribeSceneLoaded(Action<Scene, LoadSceneMode> callback, ModContext owner = null)
        {
            return Subscribe(_loaded, callback, owner, "SceneLoaded");
        }

        /// <summary>订阅场景加载完成(只要场景名)。</summary>
        public static SceneSubscription SubscribeSceneLoaded(Action<SceneLoadedEventArgs> callback, ModContext owner = null)
        {
            if (callback == null) return new SceneSubscription(null);
            return SubscribeSceneLoaded((scene, mode) =>
            {
                var args = new SceneLoadedEventArgs
                {
                    Name = SafeSceneName(scene),
                    Path = SafeScenePath(scene),
                    BuildIndex = SafeBuildIndex(scene),
                    LoadMode = (int)mode
                };
                callback(args);
            }, owner);
        }

        /// <summary>订阅场景卸载(主线程)。</summary>
        public static SceneSubscription SubscribeSceneUnloaded(Action<Scene> callback, ModContext owner = null)
        {
            return Subscribe(_unloaded, callback, owner, "SceneUnloaded");
        }

        /// <summary>订阅活动场景切换(主线程)。</summary>
        public static SceneSubscription SubscribeActiveSceneChanged(Action<Scene, Scene> callback, ModContext owner = null)
        {
            return Subscribe(_activeChanged, callback, owner, "ActiveSceneChanged");
        }

        /// <summary>退订某个场景加载回调。</summary>
        public static bool UnsubscribeSceneLoaded(Action<Scene, LoadSceneMode> callback)
        {
            return RemoveByCallback(_loaded, callback);
        }

        /// <summary>退订某个场景卸载回调。</summary>
        public static bool UnsubscribeSceneUnloaded(Action<Scene> callback)
        {
            return RemoveByCallback(_unloaded, callback);
        }

        /// <summary>移除某个 mod 的全部场景订阅。</summary>
        public static int UnsubscribeAll(ModContext owner)
        {
            if (owner == null) return 0;
            int removed = 0;
            lock (_lock)
            {
                removed += _loaded.RemoveAll(s => s.Owner == owner);
                removed += _unloaded.RemoveAll(s => s.Owner == owner);
                removed += _activeChanged.RemoveAll(s => s.Owner == owner);
                RebuildSnapshotsLocked();
            }
            return removed;
        }

        private static SceneSubscription Subscribe<TDelegate>(List<Sub<TDelegate>> list, TDelegate callback,
            ModContext owner, string label) where TDelegate : class
        {
            if (callback == null) return new SceneSubscription(null);

            var resolvedOwner = owner ?? SafeCurrentContext();

            long id;
            lock (_lock)
            {
                // 去重: 同一回调 + 同一归属 只保留一份, 防止重复事件
                foreach (var existing in list)
                {
                    if (ReferenceEquals(existing.Callback, callback) && existing.Owner == resolvedOwner)
                        return new SceneSubscription(() => RemoveById(list, existing.Id));
                }

                id = ++_nextId;
                list.Add(new Sub<TDelegate> { Id = id, Callback = callback, Owner = resolvedOwner });
                RebuildSnapshotsLocked();
            }

            EnsureInstalled();
            label = label ?? "Scene";

            if (resolvedOwner != null)
                resolvedOwner.RegisterCleanup(() => RemoveById(list, id));

            var captured = list;
            return new SceneSubscription(() => RemoveById(captured, id));
        }

        // =====================================================================
        // 等待场景
        // =====================================================================

        /// <summary>
        /// 等待某个场景加载完成。
        /// 若场景已经加载, 回调会立即(主线程)执行。
        /// </summary>
        /// <param name="sceneName">目标场景名(忽略大小写)。</param>
        /// <param name="callback">加载完成后的回调(主线程)。</param>
        /// <param name="owner">归属 mod(卸载时自动取消等待)。</param>
        /// <param name="timeoutMs">超时毫秒; &lt;=0 表示不超时。</param>
        public static SceneSubscription WaitForScene(string sceneName, Action<Scene> callback,
            ModContext owner = null, int timeoutMs = 0)
        {
            if (string.IsNullOrEmpty(sceneName) || callback == null) return new SceneSubscription(null);

            var resolvedOwner = owner ?? SafeCurrentContext();

            if (IsSceneLoaded(sceneName))
            {
                var scene = GetSceneByName(sceneName);
                MainThread.Run(() =>
                {
                    try { callback(scene); }
                    catch (Exception e) { SdkLog.ReportCrash("SCENE", "WaitForScene 立即回调", e); }
                });
                return new SceneSubscription(null);
            }

            long id;
            float deadline = 0f;
            if (timeoutMs > 0)
            {
                deadline = UnityCall.RealtimeSinceStartup() + timeoutMs / 1000f;
            }

            lock (_lock)
            {
                id = ++_nextId;
                _waits.Add(new WaitEntry(id, sceneName, callback, resolvedOwner, deadline));
            }

            EnsureInstalled();

            if (resolvedOwner != null)
                resolvedOwner.RegisterCleanup(() => CancelWait(id));

            return new SceneSubscription(() => CancelWait(id));
        }

        /// <summary>等场景加载完成后执行(等价 <see cref="WaitForScene"/>)。</summary>
        public static SceneSubscription RunWhenSceneLoaded(string sceneName, Action<Scene> callback,
            ModContext owner = null, int timeoutMs = 0)
        {
            return WaitForScene(sceneName, callback, owner, timeoutMs);
        }

        private static void CancelWait(long id)
        {
            lock (_lock) { _waits.RemoveAll(w => w.Id == id); }
        }

        // =====================================================================
        // 安装 / 转发
        // =====================================================================

        private static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;

            // 兜底检测需要每帧一次; 平时也用于等待超时
            try { UpdateService.SubscribeUpdate(Tick, null, "SceneService.Tick"); }
            catch (Exception e) { SdkLog.Warn("SCENE", "注册每帧检测失败: " + e.Message); }

            HookUnityEvents();
        }

        private static void HookUnityEvents()
        {
            if (Interlocked.Exchange(ref _unityHooked, 1) != 0) return;
            try
            {
                UnityCall.HookSceneEvents(_onSceneLoaded, _onSceneUnloaded, _onActiveSceneChanged);

                var active = UnityCall.GetActiveScene();
                _hasLastScene = true;
                _lastSceneHandle = UnityCall.SceneHandle(active);
                _lastSceneName = UnityCall.SceneName(active);

                SdkLog.Debug("SCENE", "已挂钩 Unity 场景事件");
            }
            catch (Exception e)
            {
                SdkLog.Warn("SCENE", "挂钩 Unity 场景事件失败(将只使用每帧兜底检测): " + e.Message);
            }
        }

        private static void HandleUnitySceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                _forwardedLoads++;
                InvalidateCaches();

                var args = new SceneLoadedEventArgs
                {
                    Name = SafeSceneName(scene),
                    Path = SafeScenePath(scene),
                    BuildIndex = SafeBuildIndex(scene),
                    LoadMode = (int)mode
                };

                // 1) 门面事件
                SafeRaise(() => SceneEvents.RaiseSceneLoaded(args), "SceneEvents.SceneLoaded");

                // 2) 订阅者
                var snapshot = Volatile.Read(ref _loadedSnapshot);
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var entry = snapshot[i];
                    try { entry.Callback(scene, mode); }
                    catch (Exception e) { SdkLog.ReportCrash("SCENE", "SceneLoaded 订阅者", e); }
                }

                // 3) 等待列表
                DrainWaits(scene);
            }
            catch (Exception e)
            {
                SdkLog.Error("SCENE", "处理场景加载事件失败: " + e.Message);
            }
        }

        private static void HandleUnitySceneUnloaded(Scene scene)
        {
            try
            {
                string name = SafeSceneName(scene);
                InvalidateCaches();

                SafeRaise(() => SceneEvents.RaiseSceneUnloaded(name), "SceneEvents.SceneUnloaded");

                var snapshot = Volatile.Read(ref _unloadedSnapshot);
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var entry = snapshot[i];
                    try { entry.Callback(scene); }
                    catch (Exception e) { SdkLog.ReportCrash("SCENE", "SceneUnloaded 订阅者", e); }
                }
            }
            catch (Exception e)
            {
                SdkLog.Error("SCENE", "处理场景卸载事件失败: " + e.Message);
            }
        }

        private static void HandleUnityActiveSceneChanged(Scene previous, Scene current)
        {
            try
            {
                _hasLastScene = true;
                _lastSceneHandle = UnityCall.SceneHandle(current);
                _lastSceneName = UnityCall.SceneName(current);
                InvalidateCaches();

                ForwardActiveChanged(previous, current);
            }
            catch (Exception e)
            {
                SdkLog.Error("SCENE", "处理活动场景切换失败: " + e.Message);
            }
        }

        private static void ForwardActiveChanged(Scene previous, Scene current)
        {
            _forwardedActive++;

            string prevName = SafeSceneName(previous);
            string currName = SafeSceneName(current);

            SafeRaise(() => SceneEvents.RaiseActiveSceneChanged(prevName, currName), "SceneEvents.ActiveSceneChanged");

            var snapshot = Volatile.Read(ref _activeSnapshot);
            for (int i = 0; i < snapshot.Length; i++)
            {
                var entry = snapshot[i];
                try { entry.Callback(previous, current); }
                catch (Exception e) { SdkLog.ReportCrash("SCENE", "ActiveSceneChanged 订阅者", e); }
            }
        }

        /// <summary>每帧: 兜底检测活动场景变化 + 等待超时。</summary>
        private static void Tick()
        {
            try { CheckActiveSceneFallback(); }
            catch (Exception e) { SdkLog.Error("SCENE", "活动场景兜底检测失败: " + e.Message); }

            try { SweepWaitTimeouts(); }
            catch (Exception e) { SdkLog.Error("SCENE", "等待场景超时检查失败: " + e.Message); }
        }

        private static void CheckActiveSceneFallback()
        {
            if (Volatile.Read(ref _unityHooked) == 0) return;

            var active = UnityCall.GetActiveScene();
            if (!_hasLastScene)
            {
                _hasLastScene = true;
                _lastSceneHandle = UnityCall.SceneHandle(active);
                _lastSceneName = UnityCall.SceneName(active);
                return;
            }

            if (UnityCall.SceneHandle(active) == _lastSceneHandle) return;

            // Unity 事件没触发(或触发顺序不同): 这里补一次
            var previous = default(Scene);
            previous = UnityCall.GetSceneAt(0);

            string prevName = _lastSceneName;
            _lastSceneHandle = UnityCall.SceneHandle(active);
            _lastSceneName = UnityCall.SceneName(active);
            InvalidateCaches();

            SdkLog.Debug("SCENE", "兜底检测到活动场景变化: " + prevName + " -> " + UnityCall.SceneName(active));
            ForwardActiveChanged(previous, active);
        }

        private static void DrainWaits(Scene scene)
        {
            WaitEntry[] pending;
            lock (_lock)
            {
                if (_waits.Count == 0) return;
                pending = _waits.ToArray();
            }

            string name = SafeSceneName(scene);
            for (int i = 0; i < pending.Length; i++)
            {
                var entry = pending[i];
                if (!string.Equals(entry.SceneName, name, StringComparison.OrdinalIgnoreCase)) continue;

                CancelWait(entry.Id);
                try { entry.Callback(scene); }
                catch (Exception e) { SdkLog.ReportCrash("SCENE", "WaitForScene(" + entry.SceneName + ")", e); }
            }
        }

        private static void SweepWaitTimeouts()
        {
            WaitEntry[] pending;
            lock (_lock)
            {
                if (_waits.Count == 0) return;
                pending = _waits.ToArray();
            }

            float now;
            now = UnityCall.RealtimeSinceStartup();

            for (int i = 0; i < pending.Length; i++)
            {
                var entry = pending[i];
                if (entry.Deadline <= 0f || now < entry.Deadline) continue;

                CancelWait(entry.Id);
                Interlocked.Increment(ref _waitTimeouts);
                SdkLog.Warn("SCENE", "WaitForScene(" + entry.SceneName + ") 超时");
            }
        }

        // =====================================================================
        // 内部工具
        // =====================================================================

        private static void RemoveById<TDelegate>(List<Sub<TDelegate>> list, long id) where TDelegate : class
        {
            lock (_lock)
            {
                list.RemoveAll(s => s.Id == id);
                RebuildSnapshotsLocked();
            }
        }

        private static bool RemoveByCallback<TDelegate>(List<Sub<TDelegate>> list, TDelegate callback) where TDelegate : class
        {
            if (callback == null) return false;
            lock (_lock)
            {
                int removed = list.RemoveAll(s => ReferenceEquals(s.Callback, callback));
                if (removed > 0) RebuildSnapshotsLocked();
                return removed > 0;
            }
        }

        private static void RebuildSnapshotsLocked()
        {
            Volatile.Write(ref _loadedSnapshot, _loaded.ToArray());
            Volatile.Write(ref _unloadedSnapshot, _unloaded.ToArray());
            Volatile.Write(ref _activeSnapshot, _activeChanged.ToArray());
        }

        private static ModContext SafeCurrentContext()
        {
            try { return ModContext.Current; }
            catch { return null; }
        }

        private static void SafeRaise(Action action, string what)
        {
            try { action(); }
            catch (Exception e) { SdkLog.ReportCrash("SCENE", what, e); }
        }

        private static GameObject FindRecursive(GameObject go, string name)
        {
            if (go == null) return null;
            try
            {
                if (string.Equals(UnityCall.Name(go), name, StringComparison.Ordinal)) return go;
                var t = go.transform;
                int count = t.childCount;
                for (int i = 0; i < count; i++)
                {
                    var child = t.GetChild(i);
                    if (child == null) continue;
                    var found = FindRecursive(child.gameObject, name);
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        private static string SafeSceneName(Scene scene)
        {
            try { return UnityCall.SceneIsValid(scene) ? UnityCall.SceneName(scene) : null; } catch { return null; }
        }

        private static string SafeScenePath(Scene scene)
        {
            try { return UnityCall.SceneIsValid(scene) ? UnityCall.ScenePath(scene) : null; } catch { return null; }
        }

        private static int SafeBuildIndex(Scene scene)
        {
            try { return UnityCall.SceneIsValid(scene) ? UnityCall.SceneBuildIndex(scene) : -1; } catch { return -1; }
        }

        private static void Guard(string what)
        {
            MainThread.AssertMainThread("SceneService." + what);
        }
    }
}
