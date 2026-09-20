using System;
using System.Collections.Generic;
using System.Reflection;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// mod 注册表: 维护 ModId → <see cref="ModContext"/> 以及程序集 → 上下文的反查。
    ///
    /// 说明: 当前原生加载器没有真正的 Assembly 卸载能力, 每次进程启动加载一次 mod;
    /// 注册表让 SDK 能统一管理"每个 mod 有什么", 以便卸载时清理订阅/协程/对象。
    /// </summary>
    public static class ModRegistry
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, ModContext> _byId =
            new Dictionary<string, ModContext>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Assembly, ModContext> _byAssembly =
            new Dictionary<Assembly, ModContext>();
        private static readonly List<ModContext> _ordered = new List<ModContext>();

        /// <summary>已注册的 mod 数量。</summary>
        public static int Count { get { lock (_lock) { return _ordered.Count; } } }

        /// <summary>已注册的 mod(注册顺序)。</summary>
        public static IReadOnlyList<ModContext> All
        {
            get { lock (_lock) { return _ordered.ToArray(); } }
        }

        /// <summary>按 ModId 查找。</summary>
        public static ModContext Get(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return null;
            lock (_lock)
            {
                ModContext ctx;
                return _byId.TryGetValue(modId, out ctx) ? ctx : null;
            }
        }

        /// <summary>按程序集查找(未注册返回 null)。</summary>
        public static ModContext ForAssembly(Assembly assembly)
        {
            if (assembly == null) return null;
            if (assembly == typeof(ModRegistry).Assembly) return null; // SDK 自身不是 mod

            lock (_lock)
            {
                ModContext ctx;
                return _byAssembly.TryGetValue(assembly, out ctx) ? ctx : null;
            }
        }

        /// <summary>按程序集查找, 不存在时依据 [ModManifest] 自动注册。</summary>
        public static ModContext GetOrRegister(Assembly assembly)
        {
            if (assembly == null) return null;
            if (assembly == typeof(ModRegistry).Assembly) return null;

            lock (_lock)
            {
                ModContext existing;
                if (_byAssembly.TryGetValue(assembly, out existing)) return existing;
            }

            string modId = SafeAssemblyName(assembly);
            ModManifestAttribute manifest = null;
            try { manifest = SdkInfo.ManifestOf(assembly); } catch { }

            return Register(
                modId,
                manifest != null ? manifest.Version : "1.0.0",
                manifest != null ? manifest.Author : "",
                manifest != null ? manifest.Description : "",
                assembly);
        }

        /// <summary>显式注册一个 mod 上下文(同 ModId 重复注册返回已有实例)。</summary>
        public static ModContext Register(string modId, string version = "1.0.0",
            string author = "", string description = "", Assembly assembly = null)
        {
            if (string.IsNullOrEmpty(modId)) modId = "Unknown";

            lock (_lock)
            {
                ModContext existing;
                if (_byId.TryGetValue(modId, out existing)) return existing;

                var ctx = new ModContext(modId, version, author, description, assembly);
                _byId[modId] = ctx;
                _ordered.Add(ctx);
                if (assembly != null) _byAssembly[assembly] = ctx;
                return ctx;
            }
        }

        /// <summary>注销并清理一个 mod。</summary>
        public static void Unregister(string modId)
        {
            ModContext ctx = Get(modId);
            if (ctx == null) return;

            lock (_lock)
            {
                _byId.Remove(modId);
                _ordered.Remove(ctx);
                if (ctx.Assembly != null) _byAssembly.Remove(ctx.Assembly);
            }

            try { ctx.Cleanup(); }
            catch (Exception e) { SdkLog.Error("MODREG", "清理 " + modId + " 失败: " + e.Message); }
        }

        /// <summary>清理全部 mod(SDK 关停时使用)。</summary>
        public static void CleanupAll()
        {
            ModContext[] all;
            lock (_lock) { all = _ordered.ToArray(); }
            foreach (var ctx in all)
            {
                try { ctx.Cleanup(); } catch { }
            }
        }

        private static string SafeAssemblyName(Assembly assembly)
        {
            try
            {
                var name = assembly.GetName();
                return name != null && !string.IsNullOrEmpty(name.Name) ? name.Name : "Unknown";
            }
            catch { return "Unknown"; }
        }
    }
}
