using System;
using System.Reflection;
using CesiumLoader.SDK.Inputs;
using CesiumLoader.SDK.Mods;
using Tools;
using UI;
using UnityEngine;

namespace CrashProbeMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new CrashProbeController());
        }
    }

    /// <summary>
    /// 崩溃探针: 打开设置页(或按热键)后故意写非法指针, 让游戏以原生访问冲突(0xC0000005)退出,
    /// 用来验证加载器的崩溃转储(见 <c>logs\crash\</c>)是否正常产生。
    ///
    /// **它会真的让游戏崩溃** —— 只放在测试环境里, 验证完就删掉模组目录。
    ///
    /// 为什么用 unsafe 指针: 热更程序集不能 P/Invoke(拿不到 RaiseException 之类的入口),
    /// 而托管异常会被 SDK 的回调护栏接住、不会让进程退出。裸指针写地址 0 走的是 IL 的
    /// <c>stind.i4</c>, HybridCLR 解释执行时就是一次原生写 —— 内核抛 0xC0000005,
    /// 加载器的 VEH 才有机会落 dump。
    /// </summary>
    public sealed class CrashProbeController : ModBase
    {
        // 设置页窗口缓存字段(与 LanguageUnlockMod 同一套判定): 游戏在 UIManager 里缓存
        // SettingWindow, 用 isShowing 判断"当前是否打开", 不会触发窗口的懒构造。
        private static readonly FieldInfo SettingsWindowField =
            typeof(UIManager).GetField("_cachedSettingWindow", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly CrashProbeConfig _cfg = new CrashProbeConfig();
        private DateTime _fireAt = DateTime.MaxValue;
        private string _source;
        private bool _fired;
        private bool _settingsWasOpen;

        public override string Name => "崩溃探针";
        public override string Version => "1.0.0";
        public override string Author => "CesiumLoader";
        public override string Description => "打开设置页或按热键后故意触发原生崩溃，用于验证加载器的崩溃转储";

        public override void OnInitialize()
        {
            ReadConfig(isInitial: true);
            ReportDumpConfig();

            if (SettingsWindowField == null)
                Log.Warn("没找到 UIManager._cachedSettingWindow: 设置页触发不可用, 只能用热键 " + _cfg.Hotkey);
            else if (!_cfg.TriggerOnSettings)
                Log.Info("设置页触发已关闭(triggerOnSettings=false), 只用热键 " + _cfg.Hotkey);
            else
                Log.Info("已就绪: 打开设置页(或按 " + _cfg.Hotkey + ")后 " + _cfg.DelayMs + "ms 崩溃");

            Log.Warn("这是故意的崩溃探针: 触发后游戏会退出, 验证完请删除 mods\\CrashProbeMod");
        }

        public override void OnConfigChanged()
        {
            ReadConfig(isInitial: false);
        }

        public override void OnUpdate()
        {
            if (!_cfg.Enabled || _fired) return;

            // 1) 热键兜底: 设置页判定失效(游戏改了窗口类/字段)时仍然能触发。
            if (_source == null && _cfg.Hotkey != KeyCode.None && InputService.IsKeyPressed(_cfg.Hotkey))
                Arm("热键 " + _cfg.Hotkey);

            // 2) 设置页打开沿: 只在"从关到开"的那一帧武装, 避免关窗口/重复打开反复触发。
            if (_source == null && _cfg.TriggerOnSettings)
            {
                bool open = IsSettingsOpen();
                if (open && !_settingsWasOpen) Arm("设置页");
                _settingsWasOpen = open;
            }

            if (_source != null && DateTime.UtcNow >= _fireAt) Crash();
        }

        // ---------- 触发 ----------

        private void Arm(string source)
        {
            _source = source;
            _fireAt = DateTime.UtcNow.AddMilliseconds(_cfg.DelayMs);
            Log.Warn("已武装(" + source + "): " + _cfg.DelayMs + "ms 后让游戏崩溃, 日志会先落盘");
        }

        private void Crash()
        {
            _fired = true;
            Log.Warn("正在触发崩溃: 写空指针(预期 0xC0000005)");

            // 三种退化路径: 万一某种写法被 HybridCLR 拦成托管异常(会被回调护栏接住),
            // 换下一种。真的访问冲突会直接终止进程, 下面的 catch 不会执行。
            try
            {
                CrashNullWrite();
            }
            catch (Exception e)
            {
                Log.Warn("空指针写被托管层拦下(" + e.GetType().Name + "), 改试未映射地址");
                try
                {
                    CrashBadPointer();
                }
                catch (Exception e2)
                {
                    Log.Warn("未映射地址写也被拦下(" + e2.GetType().Name + "), 改试栈溢出");
                    CrashStackOverflow(0);
                }
            }

            Log.Error("三种方式都没能让进程退出 —— 这次没有产生 dump, 请把本行连同 activity 日志一起反馈");
        }

        private static unsafe void CrashNullWrite()
        {
            // 写地址 0 再读回: 读回让这次写"可观察", 不会被编译器优化掉
            // (局部指针变量不能标 volatile, CS0106)。
            int* p = null;
            *p = 0x1337;
            if (*p != 0x1337) throw new InvalidOperationException();
        }

        private static unsafe void CrashBadPointer()
        {
            // 0xDEADBEE0 在 x64 用户态基本不会映射, 写它必崩。
            int* p = (int*)0xDEADBEE0;
            *p = 0x1337;
            if (*p != 0x1337) throw new InvalidOperationException();
        }

        private static int CrashStackOverflow(int depth)
        {
            return CrashStackOverflow(depth + 1) + 1;
        }

        // ---------- 设置页检测 ----------

        private bool IsSettingsOpen()
        {
            try
            {
                if (SettingsWindowField == null) return false;

                // hasInstance: 别在 UIManager 还没建好时把它构造出来(inst 的 getter 会 new)。
                if (!SimpleSingletonProvider<UIManager>.hasInstance) return false;

                object manager = SimpleSingletonProvider<UIManager>.inst;
                if (manager == null) return false;

                SettingWindow window = SettingsWindowField.GetValue(manager) as SettingWindow;
                return window != null && window.isShowing;
            }
            catch
            {
                return false;
            }
        }

        // ---------- 配置 ----------

        private void ReadConfig(bool isInitial)
        {
            if (Config != null)
            {
                if (isInitial)
                {
                    CrashProbeConfigDocs.Apply(Config);
                    Config.SetDefault("enabled", true);
                    Config.SetDefault("triggerOnSettings", true);
                    Config.SetDefault("delayMs", 1500);
                    Config.SetDefault("hotkey", "F8");
                }

                _cfg.Enabled = Config.GetBool("enabled", true);
                _cfg.TriggerOnSettings = Config.GetBool("triggerOnSettings", true);
                _cfg.DelayMs = CrashProbeConfig.Clamp(Config.GetInt("delayMs", 1500), 0, 60000, 1500);
                _cfg.Hotkey = CrashProbeConfig.ParseKey(Config.GetString("hotkey", "F8"), Log);
            }
        }

        /// <summary>
        /// 启动时顺手核对"崩溃转储是开着的", 并把转储目录写进日志 ——
        /// 否则"崩溃了但没 dump"经常只是因为配置里关了它。
        /// </summary>
        private void ReportDumpConfig()
        {
            try
            {
                string loaderDir = Environment.GetEnvironmentVariable("CESIUM_LOADER_DIR");
                if (string.IsNullOrEmpty(loaderDir))
                {
                    Log.Warn("拿不到 CESIUM_LOADER_DIR, 无法核对 crashDumpEnabled");
                    return;
                }

                Log.Info("崩溃后到 " + loaderDir + "\\logs\\crash 下找 <会话>.dmp 与同名 .txt");

                string path = loaderDir + "\\doorstop_config.json";
                if (!System.IO.File.Exists(path)) return;

                string text = System.IO.File.ReadAllText(path);
                int key = text.IndexOf("\"crashDumpEnabled\"", StringComparison.Ordinal);
                if (key < 0) return;

                int colon = text.IndexOf(':', key);
                if (colon < 0) return;

                int i = colon + 1;
                while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n')) i++;
                if (i + 5 <= text.Length && string.Compare(text, i, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                    Log.Error("doorstop_config.json 里 crashDumpEnabled=false: 崩溃不会产生 dump, 请先改成 true");
            }
            catch (Exception e)
            {
                Log.Warn("核对 crashDumpEnabled 失败: " + e.Message);
            }
        }
    }
}
