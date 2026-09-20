using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
namespace CesiumLoader.SDK
{
    /// <summary>
    /// 每个 mod 一份的上下文: mod 不应再去读环境变量 / 猜自己的 DLL 路径 / 拼日志路径,
    /// 统一从这里取。
    ///
    /// 生命周期: ModRegistry 在 mod 注册时创建, <see cref="Cleanup"/> 在卸载时运行
    /// 全部登记过的清理动作(事件退订 / Update 取消 / 协程停止 / 对象销毁)。
    /// </summary>
    public sealed class ModContext
    {
        private readonly List<Action> _cleanups = new List<Action>();
        private readonly object _lock = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly CancellationToken _token;   // 构造时缓存: Dispose 之后 CancellationToken 属性仍可安全读取
        private ModLogger _logger;
        private ModConfig _config;
        private bool _cleaned;

        internal ModContext(string modId, string version, string author, string description, Assembly assembly)
        {
            ModId = string.IsNullOrEmpty(modId) ? "Unknown" : modId;
            Version = string.IsNullOrEmpty(version) ? "1.0.0" : version;
            Author = author ?? "";
            Description = description ?? "";
            Assembly = assembly;
            Directory = ResolveDirectory(ModId);
            State = ModLifecycleState.Loaded;
            CreatedUtc = DateTime.UtcNow;
            _token = _cts.Token;
        }

        /// <summary>mod 标识(通常等于程序集名, 也是 mods 下的文件夹名)。</summary>
        public string ModId { get; private set; }

        /// <summary>声明版本。</summary>
        public string Version { get; private set; }

        /// <summary>声明作者。</summary>
        public string Author { get; private set; }

        /// <summary>声明描述。</summary>
        public string Description { get; private set; }

        /// <summary>mod 程序集(可能为 null, 例如手工构造的上下文)。</summary>
        public Assembly Assembly { get; private set; }

        /// <summary>mod 目录: CESIUM_MODS_DIR\{ModId}(不存在时回退到 mods 目录本身)。</summary>
        public string Directory { get; private set; }

        /// <summary>当前生命周期状态。</summary>
        public ModLifecycleState State { get; internal set; }

        /// <summary>创建时间(UTC)。</summary>
        public DateTime CreatedUtc { get; private set; }

        /// <summary>最近一次错误(没有则为 null)。</summary>
        public Exception LastError { get; internal set; }

        /// <summary>mod 被卸载 / SDK 关停时取消。</summary>
        public CancellationToken CancellationToken { get { return _token; } }

        /// <summary>是否已清理。</summary>
        public bool IsCleaned { get { return _cleaned; } }

        /// <summary>绑定了本 mod 标识的日志器(自动加 [ModId] 标签)。</summary>
        public ModLogger Logger
        {
            get { return _logger ?? (_logger = new ModLogger(ModId)); }
        }

        /// <summary>本 mod 的配置(懒加载)。</summary>
        public ModConfig Config
        {
            get { return _config ?? (_config = ModConfig.ForMod(this)); }
        }

        /// <summary>当前调用者所属的 mod 上下文(按调用方程序集解析, 失败返回 null)。</summary>
        public static ModContext Current
        {
            get { return ModRegistry.ForAssembly(SdkInfo.CallingAssembly()); }
        }

        /// <summary>登记一个清理动作; Cleanup 时按后进先出执行。</summary>
        public void RegisterCleanup(Action cleanup)
        {
            if (cleanup == null) return;
            lock (_lock)
            {
                if (_cleaned) { SafeInvoke(cleanup, "RegisterCleanup(已清理)"); return; }
                _cleanups.Add(cleanup);
            }
        }

        /// <summary>
        /// 运行全部清理动作: 事件退订 / Update 取消 / 协程停止 / UI 注销 / 对象销毁 都登记在这里。
        /// 幂等, 单个清理动作抛异常不影响其它动作。
        /// </summary>
        public void Cleanup()
        {
            Action[] actions;
            lock (_lock)
            {
                if (_cleaned) return;
                _cleaned = true;
                actions = _cleanups.ToArray();
                _cleanups.Clear();
            }

            State = ModLifecycleState.Stopping;

            // 后进先出: 后登记的先清理
            for (int i = actions.Length - 1; i >= 0; i--)
                SafeInvoke(actions[i], "Cleanup");

            // mod 自己没清理干净的 UI 登记(窗口/覆盖层/通知/GUI 回调)由 SDK 兜底移除,
            // 否则卸载后的 mod 会留下关不掉的窗口。
            SafeInvoke(() => UiService.RemoveAllForMod(this), "Cleanup:UI");

            try { _cts.Cancel(); } catch { }
            try { _cts.Dispose(); } catch { }

            State = ModLifecycleState.Stopped;
        }

        /// <summary>供日志/诊断使用的简述。</summary>
        public override string ToString()
        {
            return ModId + " v" + Version + " [" + State + "]";
        }

        private static void SafeInvoke(Action action, string what)
        {
            try { action(); }
            catch (Exception e) { SdkLog.Error("MODCTX", what + " 失败: " + e.Message); }
        }

        private static string ResolveDirectory(string modId)
        {
            try
            {
                string modsDir = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
                if (string.IsNullOrEmpty(modsDir)) return null;
                var dir = Path.Combine(modsDir, modId);
                if (System.IO.Directory.Exists(dir)) return dir;
                return modsDir;
            }
            catch { return null; }
        }
    }
}
