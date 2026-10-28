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
    public sealed class TurnClock : ModBase
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

        // =====================================================================
        // 计时器来源
        // =====================================================================

        /// <summary>丢掉已经探测到的成员(配置里换了成员名时), 下次用到重新探。</summary>
        private void ResetMembers()
        {
            _mode = Mode.None;
            _probedType = null;
            _elapsed = null;
            _start = null;
            _clock = null;
            _elapsedCandidates.Clear();
            _autoPicker.Reset();
            _pickerGaveUp = false;
            _compensator.Clear();
            _windowOpen = false;
        }

        /// <summary>
        /// 解析 <c>GameLogic.OperationTimer</c> 与它<b>私有</b>静态字段(只做一次)。
        ///
        /// 这里**必须**用 typeof 直接引用类型: 热更程序集里 <c>Type.GetType(string, bool)</c> /
        /// <c>Assembly.GetType(string, bool)</c> 这类"游戏自己不用的重载"会被裁剪, 调用它们会让
        /// 整个方法在 JIT 阶段就抛 MissingMethodException(try/catch 都拦不住)。
        /// </summary>
        private bool ResolveOperationTimer()
        {
            if (_dictField != null) return true;
            if (_resolveAttempted && _resolveFailed) return false;
            _resolveAttempted = true;

            Type type = typeof(OperationTimer);

            try
            {
                _dictField = type.GetField("timerDict", BindingFlags.Static | BindingFlags.NonPublic);
                _showField = type.GetField("ShowPlayerTimer", BindingFlags.Static | BindingFlags.NonPublic);
            }
            catch (Exception e)
            {
                Log.Warn("读取计时器字段失败: " + Describe(e));
            }

            if (_dictField == null)
            {
                _resolveFailed = true;
                Log.Warn("类型 " + type.FullName + " 上没有 timerDict 字段(游戏改版?), 本 mod 不生效");
                return false;
            }

            return true;
        }

        /// <summary>当前在场的计时器: 我的操作窗口(timerDict) + 别人回合的倒计时(ShowPlayerTimer)。</summary>
        private int CollectTimers(List<object> into)
        {
            into.Clear();

            try
            {
                object dict = _dictField.GetValue(null);
                if (dict != null)
                {
                    string path, error;
                    if (TimerDictionaryReader.TryCollect(dict, into, out path, out error))
                    {
                        if (_collectPath != path)
                        {
                            _collectPath = path;
                            Log.Info("读取 timerDict 走通: " + path);
                        }
                    }
                    else if (_collectError != error)
                    {
                        _collectError = error;
                        Log.Warn("枚举 timerDict 失败(" + (error ?? "未知") + "), 本轮不做补偿");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("读取 timerDict 失败: " + Describe(e));
            }

            if (_otherTimers && _showField != null)
            {
                try
                {
                    object show = _showField.GetValue(null);
                    if (show != null) into.Add(show);
                }
                catch { }
            }

            return into.Count;
        }

        /// <summary>第一次拿到计时器实例时探测成员, 并把成员清单打进日志(每种类型一次)。</summary>
        private void EnsureMembers(object instance)
        {
            Type type = instance.GetType();
            if (!ReferenceEquals(type, _probedType)) ProbeType(type, instance);

            if (_mode != Mode.Elapsed) return;

            if (_elapsed != null && !_elapsed.IsBroken) return;

            if (_elapsed != null)
            {
                Log.Warn("成员 " + _elapsed.Name + " 读写失败, 换下一个候选");
                _elapsed = null;
            }

            if (_elapsedCandidates.Count > 0)
            {
                _elapsed = _elapsedCandidates[0];
                _elapsedCandidates.RemoveAt(0);
                Log.Info("使用成员: " + _elapsed.Name +
                         (_duration != null ? " (时长 " + _duration.Name + ")" : " (时长未知)") +
                         (_dryRun ? " · dryRun(只记录不写)" : ""));
            }
        }

        /// <summary>按名字建候选 / 决定补偿方式 + 打一次成员清单。</summary>
        private void ProbeType(Type type, object instance)
        {
            _probedType = type;
            _elapsed = null;
            _start = null;
            _clock = null;
            _elapsedCandidates.Clear();
            _autoPicker.Reset();
            _pickerGaveUp = false;

            // ① Timer 把已用时长存成字段: 直接回退它(候选按可信度排队, 写坏一个换下一个)
            List<TimerMember> elapsedMembers = string.IsNullOrEmpty(_elapsedMemberName)
                ? TimerMemberProbe.FindAll(type, TimerMemberProbe.ElapsedNames)
                : SingleCandidate(type, _elapsedMemberName);

            for (int i = 0; i < elapsedMembers.Count; i++)
                if (elapsedMembers[i].CanWrite) _elapsedCandidates.Add(elapsedMembers[i]);

            // ② 没有 elapsed 字段(游戏实际用的就是这种): 已用时长 = 时钟 - 起点, 推后起点
            _start = PickWritable(type, TimerMemberProbe.StartNames, _startMemberName);
            _clock = PickWritable(type, TimerMemberProbe.ClockNames, _clockMemberName);

            _duration = string.IsNullOrEmpty(_durationMemberName)
                ? null
                : TimerMemberProbe.FindOne(type, _durationMemberName);

            if (_elapsedCandidates.Count > 0) _mode = Mode.Elapsed;
            else if (_start != null) _mode = Mode.Start;
            else _mode = Mode.None;

            if (_mode == Mode.Elapsed) { _elapsed = _elapsedCandidates[0]; _elapsedCandidates.RemoveAt(0); }

            Log.Info("计时器类型 " + type.FullName + " 的 float 成员: " +
                     TimerMemberProbe.Describe(type, instance));

            if (_mode == Mode.Elapsed)
            {
                Log.Info("补偿方式: 回退 elapsed(" + _elapsed.Name + ")" +
                         (_duration != null ? " · 时长 " + _duration.Name : "") +
                         (_dryRun ? " · dryRun(只记录不写)" : ""));
            }
            else if (_mode == Mode.Start)
            {
                Log.Info("补偿方式: 推后起点(" + _start.Name + ") · 时钟(" +
                         (_clock != null ? _clock.Name : "待认领") + ")" +
                         (_duration != null ? " · 时长 " + _duration.Name : "") +
                         (_dryRun ? " · dryRun(只记录不写)" : ""));
            }
            else
            {
                Log.Warn("没有可补偿的成员(既没有 elapsed 也没有 startTime), 本次运行不补偿");
            }
        }

        private static TimerMember PickWritable(Type type, string[] names, string overrideName)
        {
            if (!string.IsNullOrEmpty(overrideName))
            {
                TimerMember forced = TimerMemberProbe.FindOne(type, overrideName);
                if (forced != null && forced.CanWrite) return forced;
            }

            List<TimerMember> list = TimerMemberProbe.FindAll(type, names);
            for (int i = 0; i < list.Count; i++)
                if (list[i].CanWrite) return list[i];
            return null;
        }

        private static List<TimerMember> SingleCandidate(Type type, string name)
        {
            var list = new List<TimerMember>();
            TimerMember member = TimerMemberProbe.FindOne(type, name);
            if (member != null) list.Add(member);
            return list;
        }

        /// <summary>Start 模式没有时钟成员时: 观察几帧, 认领"每帧都在涨的那个 float 字段"。</summary>
        private void TryAutoPickClock(object instance)
        {
            if (_mode != Mode.Start || _clock != null || _pickerGaveUp) return;

            TimerMember picked = _autoPicker.Sample(instance.GetType(), instance);
            if (picked != null)
            {
                _clock = picked;
                Log.Info("按\"每帧递增\"认领时钟成员: " + picked.Name);
            }
            else if (_autoPicker.GaveUp)
            {
                _pickerGaveUp = true;
                Log.Warn("没有找到时钟成员, 本次运行不补偿(上面的成员清单可用于上报)");
            }
        }

        // =====================================================================
        // 窗口统计(日志里直接能看出补偿有没有生效)
        // =====================================================================

        private void OpenWindow(double now)
        {
            _windowOpen = true;
            _windowOpenedAt = now;
            _windowWrites = 0;
            _windowMaxElapsed = 0f;
        }

        private void CloseWindow(double now)
        {
            _windowOpen = false;

            double wall = now - _windowOpenedAt;
            if (!_logWindow || (_windowWrites == 0 && _windowMaxElapsed <= 0f)) return;

            Log.Info("操作窗口结束: 计时器 " + F(_windowMaxElapsed) + "s / 墙钟 " + F(wall) + "s, 倍率 " +
                     F(_speed) + "x, 补偿 " + _windowWrites + " 次");

            // 计时器涨得比墙钟快 = 补偿没落到实际推进上(1.0x 时本来就该一样快, 不判)
            if (_speed > 1.0001 && _windowMaxElapsed > 1f && wall < _windowMaxElapsed * 0.75)
            {
                Log.Warn("计时器推进快于真实时间(" + F(_windowMaxElapsed) + "s 游戏 / " + F(wall) +
                         "s 真实), 补偿可能没生效 —— 把这条日志和上面的成员清单一起反馈");
            }

            _windowWrites = 0;
            _windowMaxElapsed = 0f;
        }

        // =====================================================================
        // 倍率与时间
        // =====================================================================

        /// <summary>真实时间(秒)。不能用 Stopwatch / QPC / Time.realtimeSinceStartup —— 它们都被 hook 过。</summary>
        private static double NowSeconds()
        {
            return (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        }

        /// <summary>引擎当前倍率(按 speedRefreshSeconds 节流, 免得每帧读状态文件)。</summary>
        private double CurrentSpeed()
        {
            double now = NowSeconds();
            if (now < _nextSpeedReadAt) return _speed;

            _nextSpeedReadAt = now + _speedRefreshSeconds;
            try
            {
                double speed = SpeedHack.Speed;
                _speed = speed > 1.0 ? speed : 1.0;
            }
            catch
            {
                _speed = 1.0;
            }
            return _speed;
        }

        // =====================================================================
        // 配置
        // =====================================================================

        private void LoadConfig()
        {
            _config = Config;
            if (_config == null) return;

            _enabled = _config.GetBool("enabled", true);
            _otherTimers = _config.GetBool("otherPlayers", true);
            _dryRun = _config.GetBool("dryRun", false);
            _logWindow = _config.GetBool("logWindow", true);

            double refresh = _config.GetDouble("speedRefreshSeconds", DefaultSpeedRefreshSeconds);
            if (refresh < 0.05) refresh = 0.05;
            if (refresh > 5.0) refresh = 5.0;
            _speedRefreshSeconds = refresh;

            string previousElapsed = _elapsedMemberName;
            string previousStart = _startMemberName;
            string previousClock = _clockMemberName;

            string member = _config.GetString("elapsedMember", "");
            _elapsedMemberName = member == null ? "" : member.Trim();

            string startMember = _config.GetString("startMember", "");
            _startMemberName = startMember == null ? "" : startMember.Trim();

            string clockMember = _config.GetString("clockMember", "");
            _clockMemberName = clockMember == null ? "" : clockMember.Trim();

            string durationMember = _config.GetString("durationMember", "duration");
            _durationMemberName = durationMember == null ? "" : durationMember.Trim();

            // 热改成员名时重新探测, 不用重启游戏
            if (previousElapsed != _elapsedMemberName ||
                previousStart != _startMemberName ||
                previousClock != _clockMemberName)
            {
                ResetMembers();
            }

            // 首次运行: 把默认值整份写出来, 省得用户翻文档猜键名
            bool docsChanged = TurnClockConfigDocs.Apply(_config);
            if (!_config.Has("enabled"))
            {
                SaveDefaults();
                Log.Info("已生成默认配置: " + (_config.Path ?? "(未知路径)"));
            }
            else if (docsChanged)
            {
                _config.SaveIfDirty();
            }
        }

        private void SaveDefaults()
        {
            if (_config == null) return;
            try
            {
                _config.Set("enabled", _enabled);
                _config.Set("otherPlayers", _otherTimers);
                _config.Set("dryRun", _dryRun);
                _config.Set("logWindow", _logWindow);
                _config.Set("speedRefreshSeconds", _speedRefreshSeconds);
                _config.Set("elapsedMember", _elapsedMemberName);
                _config.Set("startMember", _startMemberName);
                _config.Set("clockMember", _clockMemberName);
                _config.Set("durationMember", _durationMemberName);
                _config.SaveIfDirty();
            }
            catch (Exception e) { Log.ReportCrash("SaveDefaults", e); }
        }

        // =====================================================================
        // 工具
        // =====================================================================

        private string Describe()
        {
            return "变速时把我的操作倒计时按真实时间走" +
                   (_otherTimers ? "(别人的倒计时也按真实时间显示)" : "(只处理我的操作窗口)") +
                   (_dryRun ? " · dryRun(只记录不写)" : "");
        }

        private static string Describe(Exception e)
        {
            try { return e.GetType().Name + ": " + e.Message; }
            catch { return "未知异常"; }
        }

        private static string F(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
