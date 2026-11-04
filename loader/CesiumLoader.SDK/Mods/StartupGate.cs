using System;
using System.Threading;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;
using Cysharp.Threading.Tasks;

namespace CesiumLoader.SDK.Mods
{
    /// <summary>启动门判定结果。</summary>
    public enum StartupOutcome
    {
        /// <summary>还没到: 游戏未就绪, 且等待时间未达上限。</summary>
        Pending = 0,

        /// <summary>游戏已就绪: 可以立即初始化, 不必等满上限。</summary>
        GameReady = 1,

        /// <summary>等到上限仍未就绪: 按旧行为(固定延迟)初始化。</summary>
        Timeout = 2,

        /// <summary>不需要等待: 上限 &lt;= 0(调用方显式要求不等待)。</summary>
        Disabled = 3
    }

    /// <summary>
    /// 启动门: 把 <see cref="ModBase.Run(Action, Action, int, string)"/> 的 <c>delayMs</c>
    /// 从"固定等待"改成"<b>等待上限</b>"。
    ///
    /// 行为:
    ///  - 游戏一旦就绪(<see cref="GameBootProbe"/> 判定), 立刻结束等待并初始化;
    ///  - 最坏情况仍只等到 <c>delayMs</c>(游戏时间, 与旧实现相同的 UniTask.Delay 口径),
    ///    所以任何 mod 都不会比改动前更晚初始化;
    ///  - 就绪结果会被锁存, 之后启动的 mod 直接跳过等待。
    ///
    /// 为什么可以提前: 旧实现固定等 30 秒是为了避开"游戏启动早期访问 NetManager/UIManager
    /// 等单例"的崩溃窗口。现在已经能非破坏性地判断"游戏已经进入可玩场景且有面板打开",
    /// 那一时刻再碰单例与 30 秒后碰单例没有区别, 而它通常远早于 30 秒。
    ///
    /// 关闭方式: 环境变量 <c>CESIUM_MOD_BOOT_GATE=0</c>(或 <c>false</c>/<c>off</c>),
    /// 或运行时把 <see cref="Enabled"/> 置 false; 关闭后恢复"固定等待 delayMs"的旧行为。
    /// </summary>
    public static class StartupGate
    {
        /// <summary>轮询间隔(毫秒, 游戏时间)。</summary>
        public const int DefaultPollIntervalMs = 100;

        /// <summary>默认就绪后稳定期(毫秒, 游戏时间): 让场景/面板切换先落定再初始化。</summary>
        public const int DefaultSettleMs = 1000;

        /// <summary>错峰初始化间隔(毫秒): 各 mod 错开执行 OnInitialize，避免同一帧尖峰。</summary>
        public const int DefaultStaggerIntervalMs = 35;

        /// <summary>启动门开关对应的环境变量名。</summary>
        public const string EnabledEnvName = "CESIUM_MOD_BOOT_GATE";

        private static int _enabled = 1;
        private static int _enabledResolved;
        private static int _ready;
        private static int _readyAtMs = -1;
        private static int _pollIntervalMs = DefaultPollIntervalMs;
        private static int _settleMs = DefaultSettleMs;
        private static int _staggerIntervalMs = DefaultStaggerIntervalMs;
        private static int _staggerSlot;
        private static int _timeoutLogged;
        private static int _noPumpLogged;
        private static int _probeStarted;
        private static int _sharedDone;
        private static int _sharedElapsedMs;

        /// <summary>
        /// 启动门是否启用(默认启用)。设为 false 恢复"固定等待 delayMs"的旧行为。
        /// 首次读取时会按环境变量 <c>CESIUM_MOD_BOOT_GATE</c> 解析一次。
        /// </summary>
        public static bool Enabled
        {
            get
            {
                if (Volatile.Read(ref _enabledResolved) == 0)
                {
                    Volatile.Write(ref _enabled, ResolveEnabledFromEnv() ? 1 : 0);
                    Volatile.Write(ref _enabledResolved, 1);
                }
                return Volatile.Read(ref _enabled) != 0;
            }
            set
            {
                Volatile.Write(ref _enabled, value ? 1 : 0);
                Volatile.Write(ref _enabledResolved, 1);
            }
        }

        /// <summary>轮询间隔(毫秒, 游戏时间)。过小会增加每帧取单例的次数, 不需要改。</summary>
        public static int PollIntervalMs
        {
            get { int v = Volatile.Read(ref _pollIntervalMs); return v > 0 ? v : DefaultPollIntervalMs; }
            set { Volatile.Write(ref _pollIntervalMs, value); }
        }

        /// <summary>
        /// 就绪后额外等待的稳定期(毫秒, 游戏时间), 默认 <see cref="DefaultSettleMs"/>。
        /// 就绪 + 稳定期超过上限时只等到上限。0 = 就绪即初始化。
        /// </summary>
        public static int SettleMs
        {
            get { int v = Volatile.Read(ref _settleMs); return v >= 0 ? v : 0; }
            set { Volatile.Write(ref _settleMs, value); }
        }

        /// <summary>
        /// 错峰初始化间隔(毫秒), 默认 <see cref="DefaultStaggerIntervalMs"/>。
        /// 0 = 不错峰。
        /// </summary>
        public static int StaggerIntervalMs
        {
            get { int v = Volatile.Read(ref _staggerIntervalMs); return v >= 0 ? v : 0; }
            set { Volatile.Write(ref _staggerIntervalMs, value); }
        }

        /// <summary>本次进程内是否已确认"游戏就绪"(一旦确认即锁存)。</summary>
        public static bool IsGameReady { get { return Volatile.Read(ref _ready) != 0; } }

        /// <summary>确认就绪时已经等了多少毫秒; 未确认为 -1。</summary>
        public static int ReadyAtMs { get { return Volatile.Read(ref _readyAtMs); } }

        // ---------- 判定 ----------

        /// <summary>
        /// 纯判定: 给定"游戏是否就绪 / 已等毫秒 / 上限毫秒", 返回启动门该做什么。
        /// 不读 Unity、不读时间, 可离线单测。
        /// </summary>
        /// <param name="gameReady">游戏是否已就绪。</param>
        /// <param name="elapsedMs">已等待毫秒(调用方计时)。</param>
        /// <param name="capMs">等待上限毫秒(&lt;= 0 表示不等待)。</param>
        public static StartupOutcome Decide(bool gameReady, int elapsedMs, int capMs)
        {
            if (capMs <= 0) return StartupOutcome.Disabled;
            if (gameReady) return StartupOutcome.GameReady;
            if (elapsedMs >= capMs) return StartupOutcome.Timeout;
            return StartupOutcome.Pending;
        }

        // ---------- 等待 ----------

        /// <summary>
        /// 等到"游戏就绪"或到达上限(取先到者), 并返回实际等待的毫秒数。
        /// 由 <see cref="ModHost"/> 在 OnLoad 之后调用; 也可由 mod 自行调用。
        /// </summary>
        /// <param name="capMs">等待上限(毫秒, 游戏时间)。&lt;= 0 表示不等待。</param>
        /// <param name="label">日志标签。</param>
        public static async UniTask<int> WaitAndSettleAsync(int capMs, string label)
        {
            if (capMs <= 0) return 0;

            if (!Enabled)
            {
                // 启动门被关掉: 完全退回旧行为(固定等待上限)。
                await DelaySafe(capMs);
                SdkLog.Debug(label, "启动门已关闭, 固定等待 " + capMs + "ms");
                return capMs;
            }

            if (IsGameReady) return 0;

            // 起点必须在主线程取: Unity 时间只能在主线程读。
            // 主线程泵还没起来时(极少见)退回旧行为, 保证 mod 不会卡住不初始化。
            float start = MainThread.Run(() => Clock(), 0f);
            if (start <= 0f)
            {
                await DelaySafe(capMs);
                LogNoPumpOnce(label, capMs);
                return capMs;
            }

            // 单例探测轮询：所有 mod 共享同一个探测任务，避免 N 个 mod 产生 N*10 次/秒轮询回调
            if (Interlocked.CompareExchange(ref _probeStarted, 1, 0) == 0)
            {
                RunSharedGateProbeAsync(start, capMs, label).Forget();
            }

            // 等共享探测结束，但**绝不越过本 mod 自己的上限**：
            //  - 旧实现每个 mod 各自有界(固定等待 delayMs)，这里保持同样的保证，
            //    避免"首个 mod 的上限较大, 后来上限较小的 mod 被拖长";
            //  - 共享探测协程若异常退出(Forget 吞掉异常)也不会让 mod 永久停在启动门。
            int waited = 0;
            while (Volatile.Read(ref _sharedDone) == 0 && waited < capMs)
            {
                if (!await DelaySafe(PollIntervalMs)) break;
                waited += PollIntervalMs;
            }

            int elapsed = Volatile.Read(ref _sharedElapsedMs);
            if (elapsed < 0) elapsed = 0;

            // 错峰初始化：各 mod 间隔 30-50ms，避免全部 mod 在同一帧集中调用 OnInitialize 造成主界面卡顿
            int slot = Interlocked.Increment(ref _staggerSlot) - 1;
            int staggerMs = StaggerIntervalMs;
            if (slot > 0 && staggerMs > 0)
            {
                await DelaySafe(slot * staggerMs);
            }

            return elapsed;
        }

        private static async UniTaskVoid RunSharedGateProbeAsync(float start, int capMs, string label)
        {
            int elapsed = 0;
            while (true)
            {
                // 先等一轮再判定: 让续体回到主线程, 之后才可以读 Unity / 游戏单例。
                if (!await DelaySafe(PollIntervalMs))
                {
                    CompleteProbe(elapsed);
                    return;
                }

                bool ready;
                try { ready = GameBootProbe.IsBooted(); }
                catch (Exception e)
                {
                    SdkLog.Debug(label, "就绪探测异常(继续等待): " + e.Message);
                    ready = false;
                }

                elapsed = (int)((Clock() - start) * 1000f);
                if (elapsed < 0) elapsed = 0;

                StartupOutcome outcome = Decide(ready, elapsed, capMs);

                if (outcome == StartupOutcome.GameReady)
                {
                    int settle = SettleMs;
                    if (settle > 0 && elapsed + settle <= capMs)
                    {
                        if (!await DelaySafe(settle))
                        {
                            CompleteProbe(elapsed);
                            return;
                        }
                        elapsed += settle;
                    }

                    MarkReady(elapsed);
                    SdkLog.Info("STARTUP",
                        "游戏已就绪: 启动门等待 " + elapsed + "ms(上限 " + capMs + "ms), 各 mod 错峰开始初始化");
                    CompleteProbe(elapsed);
                    return;
                }

                if (outcome == StartupOutcome.Timeout)
                {
                    if (Interlocked.Exchange(ref _timeoutLogged, 1) == 0)
                    {
                        SdkLog.Warn("STARTUP",
                            "就绪探测超时: 已等 " + elapsed + "ms(上限 " + capMs +
                            "ms), 按固定延迟初始化。若每次都超时, 说明游戏启动流程与本版本 SDK 的判定条件不一致");
                    }
                    else
                    {
                        SdkLog.Debug(label, "就绪探测超时, 已等 " + elapsed + "ms");
                    }
                    MarkReady(elapsed);
                    CompleteProbe(elapsed);
                    return;
                }
            }
        }

        /// <summary>标记共享探测结束(幂等): 等待中的 mod 轮询到该标记后立即初始化。</summary>
        private static void CompleteProbe(int elapsedMs)
        {
            if (elapsedMs < 0) elapsedMs = 0;
            Volatile.Write(ref _sharedElapsedMs, elapsedMs);
            Volatile.Write(ref _sharedDone, 1);
        }

        private static void LogNoPumpOnce(string label, int capMs)
        {
            if (Interlocked.Exchange(ref _noPumpLogged, 1) == 0)
                SdkLog.Warn(label, "主线程不可用, 退回固定启动延迟 " + capMs + "ms");
        }

        private static async UniTask<bool> DelaySafe(int ms)
        {
            try { await UniTask.Delay(ms); return true; }
            catch (Exception e)
            {
                SdkLog.Warn("STARTUP", "等待异常(提前结束): " + e.Message);
                return false;
            }
        }

        // ---------- 内部状态 ----------

        internal static void MarkReady(int waitedMs)
        {
            if (Interlocked.Exchange(ref _ready, 1) == 0)
                Volatile.Write(ref _readyAtMs, waitedMs);
        }

        /// <summary>仅供离线测试: 清空锁存与开关缓存。</summary>
        internal static void ResetForTesting()
        {
            Volatile.Write(ref _ready, 0);
            Volatile.Write(ref _readyAtMs, -1);
            Volatile.Write(ref _enabledResolved, 0);
            Volatile.Write(ref _enabled, 1);
            Volatile.Write(ref _pollIntervalMs, DefaultPollIntervalMs);
            Volatile.Write(ref _settleMs, DefaultSettleMs);
            Volatile.Write(ref _staggerIntervalMs, DefaultStaggerIntervalMs);
            Volatile.Write(ref _staggerSlot, 0);
            Volatile.Write(ref _timeoutLogged, 0);
            Volatile.Write(ref _noPumpLogged, 0);
            Volatile.Write(ref _probeStarted, 0);
            Volatile.Write(ref _sharedDone, 0);
            Volatile.Write(ref _sharedElapsedMs, 0);
            Clock = DefaultClock;
        }

        private static bool ResolveEnabledFromEnv()
        {
            try
            {
                string raw = Environment.GetEnvironmentVariable(EnabledEnvName);
                if (string.IsNullOrEmpty(raw)) return true;
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "0":
                    case "false":
                    case "off":
                    case "no":
                    case "disable":
                    case "disabled":
                        return false;
                    default:
                        return true;
                }
            }
            catch { return true; }
        }

        /// <summary>
        /// 计时源: 默认用游戏时间(受变速影响, 与 UniTask.Delay 口径一致);
        /// Unity 时间不可用时退回进程运行秒数(仍然单调递增)。测试可替换。
        /// </summary>
        internal static Func<float> Clock = DefaultClock;

        private static readonly DateTime ProcessStartUtc = DateTime.UtcNow;

        private static float DefaultClock()
        {
            float game;
            try { game = UnityTime.TimeSinceLevelLoad; }
            catch { game = 0f; }
            if (game > 0f) return game;

            try { return (float)(DateTime.UtcNow - ProcessStartUtc).TotalSeconds; }
            catch { return 0f; }
        }
    }
}
