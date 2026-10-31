using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Speed;
using GameLogic;

namespace TurnClockMod
{
    /// <summary>
    /// 入口。原生加载器调用 <c>TurnClockMod.ModEntry.Main()</c>(boot 线程, 非主线程)。
    /// </summary>
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new TurnClock());
        }
    }

    /// <summary>
    /// 回合时钟: 变速时把"我自己的操作倒计时"按真实时间走。
    ///
    /// 变速 hook 的是系统时间, 引擎里只有一个时钟 —— 所以 30 秒的操作窗口在 2 倍速下 15 秒
    /// 墙钟就被客户端判超时(客户端会替你把动作发出去)。这个 mod 不碰全局时间, 只把计时器
    /// 自身"还差多久"的时间轴拨回真实速度:
    ///
    /// <code>
    ///   2.0x: 动画/战斗/别人回合/怪物回合 = 2 倍速
    ///         我的操作倒计时               = 1 倍速(30 秒窗口就是 30 秒真实时间)
    /// </code>
    ///
    /// 游戏用的 Unity Timer 把已用时长**算出来**而不是存字段(实测成员:
    /// <c>_startTime</c> / <c>_lastUpdateTime</c> / <c>duration</c>), 所以补偿方式是把
    /// <c>_startTime</c> 按"这帧游戏时钟多走了多少"往后推, 让 <c>时钟 - 起点</c> 只涨真实时间:
    ///
    /// <code>
    ///   delta = 这帧时钟推进(游戏秒)   push = delta - delta / 倍率
    ///   _startTime += push              => 剩余时间按 delta/倍率 递减
    /// </code>
    ///
    /// 另外也兼容"有 elapsed 字段"的 Timer 实现(直接回退 elapsed)。两条路都探不到时只打
    /// 一次诊断并自动失效, 绝不改全局时间、绝不崩游戏。
    /// </summary>
    public sealed partial class TurnClock : ModBase
    {
        /// <summary>补偿方式。</summary>
        private enum Mode
        {
            /// <summary>没探到可补偿的成员。</summary>
            None = 0,

            /// <summary>Timer 里有 elapsed 字段, 直接回退它。</summary>
            Elapsed = 1,

            /// <summary>Timer 里没有 elapsed, 已用时长是算出来的: 推后 _startTime。</summary>
            Start = 2,
        }

        // =====================================================================
        // 常量
        // =====================================================================
        private const double DefaultSpeedRefreshSeconds = 0.2;

        /// <summary>连续异常多少次后停用(异常通常是"反射拿到了意想不到的类型")。</summary>
        private const int MaxErrors = 3;

        // =====================================================================
        // 配置
        // =====================================================================
        private ModConfig _config;
        private bool _enabled = true;
        private bool _otherTimers = true;
        private bool _dryRun;
        private bool _logWindow = true;
        private double _speedRefreshSeconds = DefaultSpeedRefreshSeconds;
        private string _elapsedMemberName = "";
        private string _startMemberName = "";
        private string _clockMemberName = "";
        private string _durationMemberName = "duration";

        // =====================================================================
        // 探针(游戏类型 + Unity Timer 成员)
        // =====================================================================
        private bool _resolveAttempted;
        private bool _resolveFailed;
        private FieldInfo _dictField;
        private FieldInfo _showField;
        private string _collectPath;
        private string _collectError;

        private Mode _mode = Mode.None;
        private Type _probedType;
        private TimerMember _elapsed;
        private readonly List<TimerMember> _elapsedCandidates = new List<TimerMember>();
        private TimerMember _start;
        private TimerMember _clock;
        private TimerMember _duration;
        private readonly IncreasingFieldPicker _autoPicker = new IncreasingFieldPicker();
        private bool _pickerGaveUp;

        // =====================================================================
        // 运行态
        // =====================================================================
        private readonly TimerCompensator _compensator = new TimerCompensator();
        private readonly List<object> _buffer = new List<object>();
        private double _speed = 1.0;
        private double _nextSpeedReadAt;
        private double _lastFrameAt;
        private bool _windowOpen;
        private double _windowOpenedAt;
        private int _windowWrites;
        private float _windowMaxElapsed;
        private int _errors;

        public override string Name { get { return "回合时钟"; } }
        public override string Version { get { return "1.3.0"; } }
        public override string Author { get { return "CesiumLoader"; } }

        public override string Description
        {
            get
            {
                return "变速时只把我的操作倒计时按真实时间走: 2 倍速下 30 秒的回合窗口仍是 30 秒真实时间, " +
                       "动画、战斗、别人回合与怪物回合照常加速。";
            }
        }

        // =====================================================================
        // 生命周期
        // =====================================================================

        public override void OnInitialize()
        {
            LoadConfig();

            if (!ResolveOperationTimer())
            {
                Log.Warn("回合时钟不生效: 没有可用的游戏计时器类型(见上一条日志)");
                return;
            }

            Log.Info("已启用: " + Describe());
            Log.Info("计时器来源: GameLogic.OperationTimer.timerDict" +
                     (_showField != null ? " / ShowPlayerTimer" : "") + " 已就绪");

            if (!SpeedHack.IsAvailable)
                Log.Info("变速引擎当前不可用: 这个 mod 只在倍率 >1x 时才需要工作, 不装变速时可以关掉它");
        }

        public override void OnConfigChanged()
        {
            LoadConfig();
            if (!_enabled)
            {
                _compensator.Clear();
                _windowOpen = false;
            }
            Log.Info("配置已重载: " + Describe());
        }

        public override void OnUnload()
        {
            _compensator.Clear();
            Log.Info("回合时钟已卸载");
        }

        /// <summary>
        /// 每帧(主线程, LateUpdate)。补偿算法与调用顺序无关 —— 它只比较"计时器这帧自己走了多少",
        /// 所以无论本 mod 在游戏更新计时器之前还是之后被调用都成立。
        /// </summary>
        public override void OnLateUpdate()
        {
            if (!_enabled || _errors >= MaxErrors) return;

            try
            {
                if (_dictField == null && !ResolveOperationTimer()) return;

                double now = NowSeconds();
                double realDt = now - _lastFrameAt;
                _lastFrameAt = now;
                // 首帧 / 长卡顿(切场景): 不做"实测速率 vs 缓存倍率"的判断
                if (!(realDt > 0.0) || realDt > 0.5) realDt = 0.0;

                int count = CollectTimers(_buffer);
                if (count == 0)
                {
                    if (_windowOpen) CloseWindow(now);
                    if (_compensator.Count > 0) _compensator.Clear();
                    if (_pickerGaveUp || _autoPicker.Samples > 0)
                    {
                        _autoPicker.Reset();
                        _pickerGaveUp = false;
                    }
                    return;
                }

                // 只有真的在计时器窗口里才去读倍率(约 5 次/秒读状态文件), 主菜单/房间里零开销
                double speed = CurrentSpeed();

                EnsureMembers(_buffer[0]);
                if (!IsReady())
                {
                    TryAutoPickClock(_buffer[0]);
                    if (!IsReady()) return;
                }

                if (!_windowOpen)
                {
                    OpenWindow(now);
                    // 窗口刚开: 强制重读一次真实倍率, 免得沿用上个窗口的缓存
                    _nextSpeedReadAt = 0.0;
                    speed = CurrentSpeed();
                }

                for (int i = 0; i < count; i++) TickTimer(_buffer[i], speed, realDt);

                _compensator.Prune(_buffer);
            }
            catch (Exception e)
            {
                _errors++;
                Log.Warn("回合时钟运行异常(" + _errors + "/" + MaxErrors + "): " + Describe(e));
                if (_errors >= MaxErrors) Log.Warn("异常过多, 本次运行不再尝试补偿(不影响游戏本身)");
            }
        }

        /// <summary>成员是否已经就绪到可以补偿。</summary>
        private bool IsReady()
        {
            if (_mode == Mode.Elapsed) return _elapsed != null && !_elapsed.IsBroken;
            if (_mode == Mode.Start) return _start != null && !_start.IsBroken && _clock != null && !_clock.IsBroken;
            return false;
        }

        /// <summary>处理一个计时器: 读当前进度 → 按真实时间回退/推后 → 记录窗口统计。</summary>
        private void TickTimer(object timer, double speed, double realDt)
        {
            float elapsed;
            bool wrote;

            if (_mode == Mode.Elapsed)
            {
                float value;
                if (!_elapsed.TryRead(timer, out value)) return;

                float duration = 0f;
                if (_duration != null) _duration.TryRead(timer, out duration);

                elapsed = value;
                wrote = _dryRun
                    ? _compensator.TickElapsed(timer, value, duration, 1.0, _elapsed)
                    : _compensator.TickElapsed(timer, value, duration, speed, _elapsed);
            }
            else
            {
                float clock, start;
                if (!_clock.TryRead(timer, out clock)) return;
                if (!_start.TryRead(timer, out start)) return;

                elapsed = clock - start;
                wrote = _dryRun
                    ? _compensator.TickStart(timer, clock, start, 1.0, _start)
                    : _compensator.TickStart(timer, clock, start, speed, _start);
            }

            if (elapsed > _windowMaxElapsed) _windowMaxElapsed = elapsed;
            if (wrote) _windowWrites++;

            if (realDt > 0.0) CheckMeasuredSpeed(realDt);

            if (_mode == Mode.Start && _start.IsBroken)
            {
                Log.Warn("成员 " + _start.Name + " 读写失败, 本次运行不再补偿");
                _mode = Mode.None;
            }
        }

        /// <summary>
        /// 用"计时器时钟这帧推进 ÷ 真实帧时长"反推实际倍率, 和缓存的倍率对不上就立刻重读。
        /// 这样热键改倍率后不用等满 speedRefreshSeconds(默认 0.2s) —— 那一小段漏补在 9x 下能差出 2 秒多。
        /// </summary>
        private void CheckMeasuredSpeed(double realDt)
        {
            float gameDelta = _compensator.LastDelta;
            if (gameDelta <= 0f) return;                  // 计时器暂停: 量不出速率

            double measured = gameDelta / realDt;
            if (measured > _speed * 1.35 + 0.05 || measured < _speed * 0.74 - 0.05)
                _nextSpeedReadAt = 0.0;                   // 下一帧重新读
        }

    }
}
