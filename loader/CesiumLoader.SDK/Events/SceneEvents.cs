using System;
using System.Threading;

namespace CesiumLoader.SDK
{
    /// <summary>场景加载完成参数。</summary>
    public struct SceneLoadedEventArgs
    {
        /// <summary>场景名字。</summary>
        public string Name;

        /// <summary>场景路径。</summary>
        public string Path;

        /// <summary>构建索引(-1 表示不在 Build Settings 中)。</summary>
        public int BuildIndex;

        /// <summary>加载模式(0=Single, 1=Additive)。</summary>
        public int LoadMode;

        /// <summary>可读文本。</summary>
        public override string ToString()
        {
            return (string.IsNullOrEmpty(Name) ? "(未命名)" : Name) + " mode=" + LoadMode + " index=" + BuildIndex;
        }
    }

    /// <summary>
    /// 场景事件(门面形式)。
    ///
    /// 与 <see cref="CesiumLoader.SDK.SceneService"/> 的分工: SceneService 是
    /// 订阅 API(带 mod 归属/自动退订/超时), 本类提供 event 语法。
    /// 两者都会被触发, 且都在主线程。
    /// 场景事件由 SDK 转发自 UnityEngine.SceneManagement.SceneManager, 另带一个
    /// 每帧兜底检测, 防止个别加载方式(如 SetActiveScene)不触发 Unity 事件。
    /// </summary>
    public static class SceneEvents
    {
        /// <summary>场景加载完成。</summary>
        public static event Action<SceneLoadedEventArgs> SceneLoaded;

        /// <summary>场景卸载(参数为场景名)。</summary>
        public static event Action<string> SceneUnloaded;

        /// <summary>活动场景切换(旧场景名, 新场景名)。</summary>
        public static event Action<string, string> ActiveSceneChanged;

        /// <summary>SDK 内部调用。</summary>
        internal static void RaiseSceneLoaded(SceneLoadedEventArgs args)
        {
            var handler = SceneLoaded;
            if (handler == null) return;
            foreach (Action<SceneLoadedEventArgs> d in handler.GetInvocationList())
            {
                try { d(args); }
                catch (Exception e) { SdkLog.ReportCrash("SceneEvents", "SceneLoaded", e); }
            }
        }

        /// <summary>SDK 内部调用。</summary>
        internal static void RaiseSceneUnloaded(string name)
        {
            var handler = SceneUnloaded;
            if (handler == null) return;
            foreach (Action<string> d in handler.GetInvocationList())
            {
                try { d(name); }
                catch (Exception e) { SdkLog.ReportCrash("SceneEvents", "SceneUnloaded", e); }
            }
        }

        /// <summary>SDK 内部调用。</summary>
        internal static void RaiseActiveSceneChanged(string previous, string current)
        {
            var handler = ActiveSceneChanged;
            if (handler == null) return;
            foreach (Action<string, string> d in handler.GetInvocationList())
            {
                try { d(previous, current); }
                catch (Exception e) { SdkLog.ReportCrash("SceneEvents", "ActiveSceneChanged", e); }
            }
        }

        /// <summary>清空全部订阅(测试/关停用)。</summary>
        public static void Clear()
        {
            SceneLoaded = null;
            SceneUnloaded = null;
            ActiveSceneChanged = null;
        }

        /// <summary>当前订阅者总数。</summary>
        public static int SubscriberCount
        {
            get
            {
                int n = 0;
                var a = SceneLoaded; if (a != null) n += a.GetInvocationList().Length;
                var b = SceneUnloaded; if (b != null) n += b.GetInvocationList().Length;
                var c = ActiveSceneChanged; if (c != null) n += c.GetInvocationList().Length;
                return n;
            }
        }
    }
}
