using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Mods;
using Tools;
using UI;

namespace TurnClockMod
{
    public sealed partial class TurnClock : ModBase
    {
        // =====================================================================
        // 界面倒计时(选人 / 结算 / 房间等待 / 剧情自动播放)
        //
        // 这些界面各自注册自己的 UnityTimer.Timer, 到点替玩家自动做动作, 和 OperationTimer 一样
        // 会被变速一起加速。它们不在 OperationTimer 的字典里, 只能按"宿主类型 + 字段名"找出来:
        // 面板实例在 UIManager 的 _cachedPanelDict, 剧情窗口在 UIManager 的 _cachedStoryWindow。
        //
        // 找到的计时器与操作计时器走完全相同的补偿路径(同一个 TimerCompensator), 所以
        // "时钟 − 起点" 那套推后逻辑对它们同样成立。
        // =====================================================================

        private readonly ClockSourceTable _clockSources = new ClockSourceTable();
        private readonly Dictionary<string, bool> _clockEnabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly List<object> _panelHosts = new List<object>();
        private readonly List<object> _extraBuffer = new List<object>();
        private readonly Dictionary<object, TimerSlot> _extraSlots =
            new Dictionary<object, TimerSlot>(TimerReferenceComparer.Instance);
        private readonly Dictionary<object, ClockTrace> _traces =
            new Dictionary<object, ClockTrace>(TimerReferenceComparer.Instance);
        private readonly List<object> _finishedTraces = new List<object>();
        private readonly Dictionary<Type, bool> _fallbackLogged = new Dictionary<Type, bool>();

        private FieldInfo _panelDictField;
        private FieldInfo _storyWindowField;
        private bool _uiResolveAttempted;
        private bool _uiResolveFailed;
        private string _uiError;
        private string _panelDictError;
        private bool _storyFieldWarned;

        private readonly List<TimerFlag> _finishFlags = new List<TimerFlag>();
        private bool _doneProbeAttempted;
        /// <summary>
        /// 计时器上的一个 bool 成员(isDone / isCompleted / isCancelled):
        /// 游戏自己也可能把结束状态做成字段, 所以字段与属性都试。
        /// </summary>
        private sealed class TimerFlag
        {
            internal PropertyInfo Property;
            internal FieldInfo Field;

            internal bool Read(object timer)
            {
                try
                {
                    object value = Property != null
                        ? Property.GetValue(timer, null)
                        : (Field != null ? Field.GetValue(timer) : null);
                    return value is bool && (bool)value;
                }
                catch { return false; }
            }
        }

        /// <summary>一个界面倒计时的统计(日志里能看出补偿有没有落到实际推进上)。</summary>
        private sealed class ClockTrace
        {
            internal TimerSlot Slot;
            internal float MaxElapsed;
            internal int Writes;
        }

        // =====================================================================
        // 配置
        // =====================================================================

        /// <summary>读四处界面倒计时的开关(缺省值来自登记表)。</summary>
        private void LoadClockConfig()
        {
            _clockEnabled.Clear();
            ClockSource[] sources = ClockSourceTable.Default;
            for (int i = 0; i < sources.Length; i++)
            {
                bool on = sources[i].DefaultOn;
                if (_config != null) on = _config.GetBool(sources[i].Key, on);
                _clockEnabled[sources[i].Key] = on;
            }
        }

        private bool IsClockEnabled(string key)
        {
            bool on;
            if (_clockEnabled.TryGetValue(key, out on)) return on;

            ClockSource[] sources = ClockSourceTable.Default;
            for (int i = 0; i < sources.Length; i++)
                if (sources[i].Key == key) return sources[i].DefaultOn;
            return false;
        }

        /// <summary>是否至少开了一处界面倒计时。</summary>
        private bool AnyClockEnabled()
        {
            ClockSource[] sources = ClockSourceTable.Default;
            for (int i = 0; i < sources.Length; i++)
                if (IsClockEnabled(sources[i].Key)) return true;
            return false;
        }

        /// <summary>日志用: 开着的界面倒计时清单。</summary>
        private string DescribeClocks()
        {
            var text = new System.Text.StringBuilder();
            ClockSource[] sources = ClockSourceTable.Default;
            for (int i = 0; i < sources.Length; i++)
            {
                if (!IsClockEnabled(sources[i].Key)) continue;
                if (text.Length > 0) text.Append('、');
                text.Append(sources[i].Label);
            }
            return text.Length > 0 ? text.ToString() : "无";
        }

        /// <summary>
        /// 旧版本生成的 config.json 没有这几处开关: 补上(不动已有的值),
        /// 让用户在文件里和 Toys 配置页都能看到。返回是否新增了键。
        /// </summary>
        private bool EnsureClockKeys()
        {
            if (_config == null) return false;

            bool added = false;
            ClockSource[] sources = ClockSourceTable.Default;
            for (int i = 0; i < sources.Length; i++)
                added |= _config.SetDefault(sources[i].Key, IsClockEnabled(sources[i].Key));
            return added;
        }

        // =====================================================================
        // 找计时器
        // =====================================================================

        /// <summary>解析 UIManager 上放面板/窗口的两个私有字段(只做一次)。</summary>
        private bool ResolveUI()
        {
            if (_panelDictField != null) return true;
            if (_uiResolveAttempted && _uiResolveFailed) return false;
            _uiResolveAttempted = true;

            try
            {
                Type type = typeof(UIManager);
                _panelDictField = type.GetField("_cachedPanelDict", BindingFlags.Instance | BindingFlags.NonPublic);
                _storyWindowField = type.GetField("_cachedStoryWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            catch (Exception e)
            {
                _uiError = Describe(e);
            }

            if (_panelDictField == null)
            {
                _uiResolveFailed = true;
                Log.Warn("UIManager 上没有面板缓存字段(_cachedPanelDict), 界面倒计时不生效" +
                         (_uiError != null ? ": " + _uiError : ""));
                return false;
            }

            return true;
        }

        /// <summary>收集这一帧在场的界面倒计时(选人、结算、房间等待、剧情)。</summary>
        private int CollectExtraClocks(List<object> into)
        {
            into.Clear();
            _extraSlots.Clear();

            if (!AnyClockEnabled()) return 0;

            UIManager ui;
            try
            {
                // 没实例就不要去创建它: 主菜单/启动早期碰 UIManager 会崩
                if (!SimpleSingletonProvider<UIManager>.hasInstance) return 0;
                ui = SimpleSingletonProvider<UIManager>.inst;
            }
            catch { return 0; }

            if (ui == null) return 0;
            if (!ResolveUI()) return 0;

            try
            {
                object dict = _panelDictField.GetValue(ui);
                if (dict != null)
                {
                    _panelHosts.Clear();
                    string path, error;
                    if (TimerDictionaryReader.TryCollect(dict, _panelHosts, out path, out error))
                    {
                        for (int i = 0; i < _panelHosts.Count; i++) AddHostClocks(_panelHosts[i], into);
                    }
                    else if (error != _panelDictError)
                    {
                        _panelDictError = error;
                        Log.Warn("枚举面板缓存失败(" + (error ?? "未知") + "), 界面倒计时本轮不做补偿");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("读取面板缓存失败: " + Describe(e));
            }

            if (IsClockEnabled("storyAuto"))
            {
                if (_storyWindowField != null)
                {
                    object story = null;
                    try { story = _storyWindowField.GetValue(ui); } catch { }
                    if (story != null) AddHostClocks(story, into);
                }
                else if (!_storyFieldWarned)
                {
                    _storyFieldWarned = true;
                    Log.Warn("UIManager 上没有剧情窗口字段(_cachedStoryWindow), 剧情自动播放不做补偿");
                }
            }

            return into.Count;
        }

        /// <summary>把宿主(某个面板/窗口)上的计时器字段加进本帧的补偿列表。</summary>
        private void AddHostClocks(object host, List<object> into)
        {
            if (host == null) return;

            Type type = host.GetType();
            List<TimerSlot> slots = _clockSources.Resolve(type);
            for (int i = 0; i < slots.Count; i++)
            {
                TimerSlot slot = slots[i];
                if (!IsClockEnabled(slot.Key)) continue;

                object timer = slot.Read(host);
                if (timer == null) continue;

                if (slot.FromFallback && !_fallbackLogged.ContainsKey(type))
                {
                    _fallbackLogged[type] = true;
                    Log.Info("字段名没对上, 按\"类型是 Timer\"认领 " + type.Name + "." + slot.FieldName);
                }

                into.Add(timer);
                _extraSlots[timer] = slot;
            }
        }

        // =====================================================================
        // 日志(每个倒计时一行总结, 和操作窗口的日志一个意思)
        // =====================================================================

        /// <summary>记一笔: 这一帧给某个界面倒计时补偿了多少。</summary>
        private void TrackExtraClock(object timer, float elapsed, bool wrote)
        {
            ClockTrace trace;
            if (_traces.TryGetValue(timer, out trace))
            {
                if (elapsed > trace.MaxElapsed) trace.MaxElapsed = elapsed;
                if (wrote) trace.Writes++;
                return;
            }

            TimerSlot slot;
            if (!_extraSlots.TryGetValue(timer, out slot)) return;

            trace = new ClockTrace();
            trace.Slot = slot;
            trace.MaxElapsed = elapsed;
            trace.Writes = wrote ? 1 : 0;
            _traces[timer] = trace;

            if (_logWindow && slot.Trace)
                Log.Info("接管 " + slot.Label + "(" + slot.FieldName + ")");
        }

        /// <summary>给已经结束(完成/取消/界面关掉)的界面倒计时打一行总结。</summary>
        private void FinishTraces()
        {
            if (_traces.Count == 0) return;

            _finishedTraces.Clear();
            foreach (KeyValuePair<object, ClockTrace> pair in _traces)
            {
                if (!_extraSlots.ContainsKey(pair.Key) || IsTimerFinished(pair.Key)) _finishedTraces.Add(pair.Key);
            }
            if (_finishedTraces.Count == 0) return;

            for (int i = 0; i < _finishedTraces.Count; i++)
            {
                object timer = _finishedTraces[i];
                ClockTrace trace;
                if (!_traces.TryGetValue(timer, out trace)) continue;
                _traces.Remove(timer);

                if (!_logWindow || trace.Slot == null || !trace.Slot.Trace) continue;
                Log.Info(trace.Slot.Label + "(" + trace.Slot.FieldName + ") 结束: 计时器 " +
                         F(trace.MaxElapsed) + "s, 补偿 " + trace.Writes + " 次");
            }

            _finishedTraces.Clear();
        }

        /// <summary>放下全部跟踪(配置变更/卸载)。</summary>
        private void ResetExtraClocks()
        {
            _extraBuffer.Clear();
            _extraSlots.Clear();
            _traces.Clear();
            _finishedTraces.Clear();
            _fallbackLogged.Clear();
        }

        /// <summary>计时器是不是已经完成/取消(都读不到时当作"还没结束", 只影响日志)。</summary>
        private bool IsTimerFinished(object timer)
        {
            if (timer == null) return true;

            if (!_doneProbeAttempted)
            {
                _doneProbeAttempted = true;
                ProbeFinishFlags(timer.GetType());
            }

            for (int i = 0; i < _finishFlags.Count; i++)
                if (_finishFlags[i].Read(timer)) return true;
            return false;
        }

        private void ProbeFinishFlags(Type timerType)
        {
            string[] names = { "isDone", "isCompleted", "isCancelled" };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    PropertyInfo property = timerType.GetProperty(names[i],
                        BindingFlags.Instance | BindingFlags.Public);
                    if (property != null && property.PropertyType == typeof(bool))
                    {
                        var flag = new TimerFlag();
                        flag.Property = property;
                        _finishFlags.Add(flag);
                        continue;
                    }

                    FieldInfo field = timerType.GetField(names[i],
                        BindingFlags.Instance | BindingFlags.Public);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        var flag = new TimerFlag();
                        flag.Field = field;
                        _finishFlags.Add(flag);
                    }
                }
                catch { }
            }
        }
    }
}
