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
    public sealed partial class TurnClock : ModBase
    {
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
                Log.Warn("类型 " + type.FullName + " 上没有 timerDict 字段(游戏改版?), 操作计时器不补偿");
                return false;
            }

            return true;
        }

        /// <summary>当前在场的计时器: 我的操作窗口(timerDict) + 别人回合的倒计时(ShowPlayerTimer)。</summary>
        private int CollectTimers(List<object> into)
        {
            into.Clear();
            if (_dictField == null) return 0;

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
            LoadClockConfig();
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
            bool keysAdded = EnsureClockKeys();
            if (!_config.Has("enabled"))
            {
                SaveDefaults();
                Log.Info("已生成默认配置: " + (_config.Path ?? "(未知路径)"));
            }
            else if (docsChanged || keysAdded)
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

                ClockSource[] clocks = ClockSourceTable.Default;
                for (int i = 0; i < clocks.Length; i++)
                    _config.Set(clocks[i].Key, IsClockEnabled(clocks[i].Key));

                _config.SaveIfDirty();
            }
            catch (Exception e) { Log.ReportCrash("SaveDefaults", e); }
        }

        // =====================================================================
        // 工具
        // =====================================================================

        private string Describe()
        {
            return "变速时把我的倒计时按真实时间走" +
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
