using System;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 每帧事件(Update / LateUpdate)。
    ///
    /// 与 <see cref="CesiumLoader.SDK.UpdateService"/> 的分工:
    /// UpdateService 是"订阅 API"(带 mod 归属与自动清理), 本类是"事件形式的门面",
    /// 供喜欢 event 语法的 mod 使用。两者在同一个主线程帧内被触发。
    ///
    /// 所有回调都在 Unity 主线程执行。
    /// </summary>
    public static class UpdateEvents
    {
        /// <summary>每帧 Update(主线程)。</summary>
        public static event Action Update;

        /// <summary>每帧 LateUpdate(主线程)。</summary>
        public static event Action LateUpdate;

        /// <summary>已执行的 Update 帧数。</summary>
        public static long UpdateFrameCount { get { return _updateFrames; } }

        /// <summary>已执行的 LateUpdate 帧数。</summary>
        public static long LateUpdateFrameCount { get { return _lateFrames; } }

        private static long _updateFrames;
        private static long _lateFrames;

        /// <summary>SDK 内部调用(由主线程泵触发)。</summary>
        internal static void RaiseUpdate()
        {
            _updateFrames++;
            var handler = Update;
            if (handler == null) return;

            foreach (Action d in handler.GetInvocationList())
            {
                try { d(); }
                catch (Exception e) { SdkLog.ReportCrash("UpdateEvents", "Update 处理器", e); }
            }
        }

        /// <summary>SDK 内部调用(由主线程泵触发)。</summary>
        internal static void RaiseLateUpdate()
        {
            _lateFrames++;
            var handler = LateUpdate;
            if (handler == null) return;

            foreach (Action d in handler.GetInvocationList())
            {
                try { d(); }
                catch (Exception e) { SdkLog.ReportCrash("UpdateEvents", "LateUpdate 处理器", e); }
            }
        }

        /// <summary>清空全部订阅(SDK 关停/测试用)。</summary>
        public static void Clear()
        {
            Update = null;
            LateUpdate = null;
        }
    }
}
