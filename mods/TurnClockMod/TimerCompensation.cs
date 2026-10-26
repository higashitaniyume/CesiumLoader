// TimerCompensation.cs - "把我自己的操作倒计时拨回真实时间"的核心
//
// 纯 BCL: 不引用 SDK / UnityEngine, 可以直接编进测试工程离线跑。
//
// 背景: 加载器的变速 hook 了系统时间, 引擎感知的一切都跟着变快 —— 包括我自己的操作倒计时
// (GameLogic.OperationTimer 用 UnityTimer.Timer 注册, 按 deltaTime 推进)。2 倍速下 30 秒的
// 窗口 15 秒墙钟就被客户端判超时, 客户端会替我把动作发出去(商店=空买、出牌=自动过牌)。
//
// 做法: 每帧读计时器当前的 elapsed, 和"上一帧我们留在字段里的值"比 —— 这一帧游戏把它推进了
// delta(游戏秒), 真实过去的时间只有 delta/speed, 于是把字段退回到"上一帧的值 + delta/speed"。
// 只后退、只动计时器自己的字段, 不碰全局时间: 动画/战斗/别人回合仍然是满倍速。
//
// 为什么与"本 mod 在游戏更新计时器之前还是之后被调用"无关: 两次采样之间恰好发生一次计时器
// 推进(每帧一次), 所以 delta 始终等于这一帧游戏推进的量。重复/漏采才会出错, 而每帧一次采样
// 恰好避免了这一点。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TurnClockMod
{
    /// <summary>按引用比较的键(计时器实例); netstandard2.0 没有 ReferenceEqualityComparer。</summary>
    internal sealed class TimerReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly TimerReferenceComparer Instance = new TimerReferenceComparer();

        public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }

        public int GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
    }

    /// <summary>能从计时器实例读写的 float 成员(字段或属性)。</summary>
    internal sealed class TimerMember
    {
        private readonly FieldInfo _field;
        private readonly PropertyInfo _property;
        private bool _broken;

        private TimerMember(string name, FieldInfo field, PropertyInfo property)
        {
            Name = name;
            _field = field;
            _property = property;
        }

        /// <summary>成员名(日志与配置里用这个名字)。</summary>
        internal string Name { get; private set; }

        /// <summary>是字段(false = 属性)。</summary>
        internal bool IsField { get { return _field != null; } }

        /// <summary>读/写抛过一次异常后就停用, 免得每帧刷异常。</summary>
        internal bool IsBroken { get { return _broken; } }

        /// <summary>
        /// 看起来能不能写。只读字段这里只能返回 true —— 真的写不进去会由 <see cref="TryWrite"/> 兜住,
        /// 然后顺位换下一个候选(热更程序集里 FieldInfo.IsInitOnly / GetSetMethod 都可能被裁剪)。
        /// </summary>
        internal bool CanWrite
        {
            get
            {
                if (_broken) return false;
                if (_field != null) return true;
                return _property != null && _property.CanWrite;
            }
        }

        internal static TimerMember ForField(FieldInfo field)
        {
            return new TimerMember(field != null ? field.Name : "", field, null);
        }

        internal static TimerMember ForProperty(PropertyInfo property)
        {
            return new TimerMember(property != null ? property.Name : "", null, property);
        }

        internal bool TryRead(object instance, out float value)
        {
            value = 0f;
            if (_broken || instance == null) return false;
            try
            {
                object raw = _field != null ? _field.GetValue(instance) : _property.GetValue(instance, null);
                if (!(raw is float)) return false;
                value = (float)raw;
                return true;
            }
            catch
            {
                _broken = true;
                return false;
            }
        }

        internal bool TryWrite(object instance, float value)
        {
            if (_broken || instance == null) return false;
            if (!CanWrite)
            {
                // 明确不可写(只读属性): 当成永久失败, 让调用方顺位换下一个候选
                _broken = true;
                return false;
            }
            try
            {
                if (_field != null) _field.SetValue(instance, value);
                else _property.SetValue(instance, value, null);
                return true;
            }
            catch
            {
                _broken = true;
                return false;
            }
        }
    }

    /// <summary>
    /// 在 <c>UnityTimer.Timer</c> 上找"已用时长"和"总时长"两个成员。
    ///
    /// 游戏没有随包发 Unity Timer 的托管 dll(AOT 编进 GameAssembly), 所以成员名只能运行时探;
    /// 先按已知名字找, 全对不上再用 <see cref="ElapsedAutoPicker"/> 观察"每帧都在涨的那个"。
    /// </summary>
    internal static class TimerMemberProbe
    {
        internal const BindingFlags InstanceAll =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>elapsed 候选名, 按可信度排序。</summary>
        internal static readonly string[] ElapsedNames =
        {
            "elapsedTime", "_elapsedTime", "m_ElapsedTime", "<elapsedTime>k__BackingField",
            "elapsed", "_elapsed", "m_elapsed",
            "currentTime", "_currentTime", "m_CurrentTime", "progressTime",
        };

        /// <summary>总时长候选名(只用于把写回值夹在终点之前, 找不到也不影响补偿)。</summary>
        internal static readonly string[] DurationNames =
        {
            "duration", "_duration", "m_Duration", "<duration>k__BackingField",
            "totalTime", "loopDuration",
        };

        /// <summary>
        /// 起点候选名: 游戏实际用的 Unity Timer 把已用时长算成 <c>时钟 - 起点</c>,
        /// 补偿方式就是把这个起点往后推(见 <see cref="TimerCompensator.TickStart"/>)。
        /// </summary>
        internal static readonly string[] StartNames =
        {
            "_startTime", "startTime", "m_StartTime", "<startTime>k__BackingField",
            "_beginTime", "beginTime", "_beginAt", "startAt",
        };

        /// <summary>
        /// 时钟候选名: 每帧被刷成"当前游戏时间"的字段, 用来算这一帧时钟推进了多少。
        /// 找不到时由 <see cref="IncreasingFieldPicker"/> 观察"每帧递增的字段"来认领。
        /// </summary>
        internal static readonly string[] ClockNames =
        {
            "_lastUpdateTime", "lastUpdateTime", "m_LastUpdateTime", "<lastUpdateTime>k__BackingField",
            "_lastTickTime", "lastTickTime", "_lastTime", "lastTime",
        };

        /// <summary>按名字找第一个 float 成员。</summary>
        internal static TimerMember Find(Type type, string[] names)
        {
            List<TimerMember> all = FindAll(type, names);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>按名字列表找出所有 float 成员(按可信度排序, 同名去重)。</summary>
        internal static List<TimerMember> FindAll(Type type, string[] names)
        {
            var list = new List<TimerMember>();
            if (type == null || names == null) return list;

            for (int i = 0; i < names.Length; i++)
            {
                TimerMember member = FindOne(type, names[i]);
                if (member == null) continue;

                bool duplicate = false;
                for (int k = 0; k < list.Count; k++)
                    if (list[k].Name == member.Name) { duplicate = true; break; }
                if (!duplicate) list.Add(member);
            }
            return list;
        }

        /// <summary>按名字找一个 float 字段或属性(能不能写由 TryWrite 决定)。</summary>
        internal static TimerMember FindOne(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                FieldInfo field = type.GetField(name, InstanceAll);
                if (field != null && field.FieldType == typeof(float)) return TimerMember.ForField(field);

                PropertyInfo property = type.GetProperty(name, InstanceAll);
                if (property != null && property.PropertyType == typeof(float))
                    return TimerMember.ForProperty(property);
            }
            catch { }
            return null;
        }

        /// <summary>所有可读的 float 字段/属性(诊断与兜底自动认领用)。</summary>
        internal static List<TimerMember> FloatMembers(Type type)
        {
            var list = new List<TimerMember>();
            if (type == null) return list;

            try
            {
                FieldInfo[] fields = type.GetFields(InstanceAll);
                for (int i = 0; i < fields.Length; i++)
                    if (fields[i].FieldType == typeof(float)) list.Add(TimerMember.ForField(fields[i]));
            }
            catch { }

            try
            {
                PropertyInfo[] properties = type.GetProperties(InstanceAll);
                for (int i = 0; i < properties.Length; i++)
                {
                    PropertyInfo property = properties[i];
                    if (property.PropertyType != typeof(float)) continue;
                    if (!property.CanRead) continue;
                    list.Add(TimerMember.ForProperty(property));
                }
            }
            catch { }

            return list;
        }

        /// <summary>
        /// 诊断文本: 类型 + 每个 float 成员的当前值。
        /// 名字全对不上时, 这一行就是"这个版本的计时器到底长什么样"的唯一线索, 所以打一次值得。
        /// </summary>
        internal static string Describe(Type type, object instance)
        {
            if (type == null) return "(null)";

            var text = new System.Text.StringBuilder();
            List<TimerMember> members = FloatMembers(type);
            for (int i = 0; i < members.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(members[i].Name);
                float value;
                if (instance != null && members[i].TryRead(instance, out value))
                {
                    text.Append('=').Append(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (!members[i].CanWrite) text.Append("(只读)");
            }
            if (members.Count == 0) text.Append("(没有 float 字段/属性)");
            return text.ToString();
        }
    }

    /// <summary>
    /// 名字全对不上时的兜底: 观察几帧, 认领"每帧都在涨的那个 float 字段"当**时钟**
    /// (游戏实际用的 Unity Timer 里就是 <c>_lastUpdateTime</c>)。
    ///
    /// 只认字段不认属性: 属性可能是 elapsed/duration 这类算出来的值, 写回去会调到别人的 setter。
    /// </summary>
    internal sealed class IncreasingFieldPicker
    {
        /// <summary>需要连续观察的帧数。</summary>
        internal const int RequiredSamples = 6;

        // 名字里带这些词的肯定不是"每帧刷新的时钟", 直接排除。
        private static readonly string[] Rejects =
        {
            "duration", "delay", "interval", "speed", "scale", "min", "max", "percent", "progress",
        };

        private readonly List<TimerMember> _candidates = new List<TimerMember>();
        private readonly List<float> _last = new List<float>();
        private readonly List<bool> _alive = new List<bool>();
        private Type _type;
        private int _samples;

        /// <summary>已喂进去的样本帧数。</summary>
        internal int Samples { get { return _samples; } }

        /// <summary>是否已经观察够帧数却什么都没认领到(调用方据此打一次"放弃"日志)。</summary>
        internal bool GaveUp { get { return _samples >= RequiredSamples && FindAlive() < 0; } }

        /// <summary>换一个计时器类型/新窗口时重置。</summary>
        internal void Reset()
        {
            _type = null;
            _samples = 0;
            _candidates.Clear();
            _last.Clear();
            _alive.Clear();
        }

        /// <summary>喂一帧样本; 认领到就返回那个成员(否则 null)。</summary>
        internal TimerMember Sample(Type type, object instance)
        {
            if (type == null || instance == null) return null;

            if (!ReferenceEquals(type, _type))
            {
                Reset();
                _type = type;
                BuildCandidates(type);
            }

            _samples++;
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (!_alive[i]) continue;

                float value;
                if (!_candidates[i].TryRead(instance, out value))
                {
                    _alive[i] = false;
                    continue;
                }

                if (_samples > 1)
                {
                    // 必须严格变大, 而且不能大得离谱(说明它是个长跑的累计量, 不是这一局的 elapsed)
                    if (!(value > _last[i]) || value > 3600f || value < 0f) _alive[i] = false;
                }
                _last[i] = value;
            }

            if (_samples < RequiredSamples) return null;

            int index = FindAlive();
            return index >= 0 ? _candidates[index] : null;
        }

        private int FindAlive()
        {
            for (int i = 0; i < _candidates.Count; i++)
                if (_alive[i] && _candidates[i].CanWrite) return i;
            return -1;
        }

        private void BuildCandidates(Type type)
        {
            List<TimerMember> members = TimerMemberProbe.FloatMembers(type);
            for (int i = 0; i < members.Count; i++)
            {
                TimerMember member = members[i];
                if (!member.IsField || !member.CanWrite) continue;
                if (IsRejected(member.Name)) continue;
                _candidates.Add(member);
                _last.Add(0f);
                _alive.Add(true);
            }
        }

        private static bool IsRejected(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string lower = name.ToLower();
            for (int i = 0; i < Rejects.Length; i++)
                if (lower.IndexOf(Rejects[i]) >= 0) return true;
            return false;
        }
    }

    /// <summary>
    /// 读 <c>OperationTimer.timerDict</c>(<c>Dictionary&lt;long, Timer&gt;</c>)里的计时器实例。
    ///
    /// 热更程序集里泛型集合的"非泛型接口实现"可能被裁剪, 所以准备两条路:
    /// 先按 <see cref="IDictionary"/> 枚举(最快), 失败再走 <c>Dictionary&lt;,&gt;</c> 自己的
    /// <c>GetEnumerator</c> —— 后者正是游戏自己 <c>foreach</c> 时用的方法, 一定存在。
    /// </summary>
    internal static class TimerDictionaryReader
    {
        /// <summary>把字典里的 value(计时器)收集到 <paramref name="into"/>。</summary>
        /// <param name="path">实际走通的那条路(IDictionary / GetEnumerator), 供日志用。</param>
        /// <param name="error">都没走通时的原因, 供日志用。</param>
        internal static bool TryCollect(object dict, List<object> into, out string path, out string error)
        {
            path = null;
            error = null;
            if (dict == null) return false;

            // ① 非泛型 IDictionary
            try
            {
                IDictionary map = dict as IDictionary;
                if (map != null)
                {
                    IDictionaryEnumerator enumerator = map.GetEnumerator();
                    int guard = 0;
                    while (enumerator.MoveNext())
                    {
                        if (enumerator.Value != null) into.Add(enumerator.Value);
                        if (++guard > 256) break;
                    }
                    path = "IDictionary";
                    return true;
                }
                error = "不是 IDictionary";
            }
            catch (Exception e)
            {
                error = "IDictionary: " + e.GetType().Name;
            }

            // ② Dictionary<,>.GetEnumerator(游戏自己的 foreach 就是它)
            try
            {
                Type dictType = dict.GetType();
                MethodInfo getEnumerator = dictType.GetMethod("GetEnumerator",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                object enumerator = getEnumerator != null ? getEnumerator.Invoke(dict, null) : null;

                if (enumerator != null)
                {
                    Type enumeratorType = enumerator.GetType();
                    MethodInfo moveNext = enumeratorType.GetMethod("MoveNext",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    PropertyInfo current = enumeratorType.GetProperty("Current",
                        BindingFlags.Public | BindingFlags.Instance);

                    if (moveNext != null && current != null)
                    {
                        int guard = 0;
                        while ((bool)moveNext.Invoke(enumerator, null))
                        {
                            object pair = current.GetValue(enumerator, null);
                            if (pair != null)
                            {
                                PropertyInfo valueProperty = pair.GetType().GetProperty("Value",
                                    BindingFlags.Public | BindingFlags.Instance);
                                object value = valueProperty != null ? valueProperty.GetValue(pair, null) : null;
                                if (value != null) into.Add(value);
                            }
                            if (++guard > 256) break;
                        }
                        path = "GetEnumerator";
                        return true;
                    }
                    error = (error ?? "") + " / 缺少 MoveNext/Current";
                }
                else
                {
                    error = (error ?? "") + " / 缺少 GetEnumerator";
                }
            }
            catch (Exception e)
            {
                error = (error ?? "") + " / GetEnumerator: " + e.GetType().Name;
            }

            return false;
        }
    }

    /// <summary>
    /// 按真实时间推进计时器: 游戏这一帧把 elapsed 推进了 delta(游戏秒), 真实只过了 delta/speed,
    /// 所以把字段退回"上一帧的值 + delta/speed"。
    ///
    /// 只后退不前进(desired ≤ observed), 所以永远不会把计时器往终点以外推; 停止推进(暂停)时只对齐
    /// 基准不写字段, 恢复后从原来的位置继续。
    /// </summary>
    internal sealed class TimerCompensator
    {
        /// <summary>推后起点时, 至少给计时器留这么久的"已用时长", 免得剩余时间变负。</summary>
        private const float MinElapsed = 0.05f;

        private sealed class Entry
        {
            /// <summary>我们最后留在字段里的值(= 下一帧算 delta 的基准)。</summary>
            internal float Value;
        }

        private readonly Dictionary<object, Entry> _entries =
            new Dictionary<object, Entry>(TimerReferenceComparer.Instance);

        /// <summary>正在跟踪的计时器数量。</summary>
        internal int Count { get { return _entries.Count; } }

        /// <summary>最近一次 Tick 里"计时器这帧推进了多少"(游戏秒), 供调用方反推实际倍率。</summary>
        internal float LastDelta { get; private set; }

        /// <summary>
        /// elapsed 模式: Timer 把已用时长存成字段时, 直接把字段退回"上一帧的值 + delta/倍率"。
        /// 返回是否真的写回了字段。
        /// </summary>
        /// <param name="timer">计时器实例。</param>
        /// <param name="observed">读到(未修正)的已用时长。</param>
        /// <param name="duration">总时长(未知传 0 或负数)。</param>
        /// <param name="speed">当前引擎倍率。</param>
        /// <param name="member">elapsed 成员。</param>
        internal bool TickElapsed(object timer, float observed, float duration, double speed, TimerMember member)
        {
            if (timer == null || member == null) return false;

            Entry entry;
            if (!_entries.TryGetValue(timer, out entry))
            {
                // 第一次见到: 只建立基准(这一帧之前推进的部分不追溯)
                _entries[timer] = new Entry { Value = observed };
                LastDelta = 0f;
                return false;
            }

            float delta = observed - entry.Value;
            LastDelta = delta > 0f ? delta : 0f;
            if (delta <= 0f)
            {
                // 没推进(计时器暂停)或被重置: 对齐基准, 不写字段
                entry.Value = observed;
                return false;
            }

            if (!(speed > 1.0001))
            {
                // 没在变速: 字段原样就是真实时间
                entry.Value = observed;
                return false;
            }

            float desired = entry.Value + delta / (float)speed;
            if (duration > 0f && desired > duration) desired = duration;
            if (desired < 0f) desired = 0f;
            if (desired >= observed)
            {
                // 没有可退的空间(倍率≈1 / 帧太短): 不写, 免得每帧做无意义的赋值
                entry.Value = observed;
                return false;
            }

            if (!member.TryWrite(timer, desired))
            {
                entry.Value = observed;
                return false;
            }

            entry.Value = desired;
            return true;
        }

        /// <summary>
        /// start 模式(游戏实际用的 Unity Timer 就是这种): 已用时长 = <c>时钟 - 起点</c>, 没有字段可退,
        /// 于是把起点按"这帧时钟多走了多少"往后推, 让剩余时间只按真实时间递减。
        ///
        /// <code>
        ///   delta = 时钟这帧推进(游戏秒)   push = delta - delta / 倍率
        ///   起点 += push                  => 已用时长这帧只涨 delta/倍率
        /// </code>
        ///
        /// 基准取"时钟"而不是真实时间: 计时器暂停(时钟不涨)时 delta=0, 不动起点, 恢复后继续。
        /// </summary>
        /// <param name="timer">计时器实例。</param>
        /// <param name="clockNow">当前时钟(游戏时间)。</param>
        /// <param name="startNow">当前起点。</param>
        /// <param name="speed">当前引擎倍率。</param>
        /// <param name="startMember">起点成员。</param>
        internal bool TickStart(object timer, float clockNow, float startNow, double speed, TimerMember startMember)
        {
            if (timer == null || startMember == null) return false;

            Entry entry;
            if (!_entries.TryGetValue(timer, out entry))
            {
                // 第一次见到: 只记下时钟基准
                _entries[timer] = new Entry { Value = clockNow };
                LastDelta = 0f;
                return false;
            }

            float delta = clockNow - entry.Value;
            entry.Value = clockNow;
            LastDelta = delta > 0f ? delta : 0f;

            if (delta <= 0f) return false;                     // 计时器暂停 / 时钟没动
            if (!(speed > 1.0001)) return false;               // 没在变速: 什么都不用做

            float push = delta - delta / (float)speed;
            if (push <= 0f) return false;

            float newStart = startNow + push;

            // 起点不能越过"现在", 否则已用时长变负、倒计时会反过来涨
            float maxStart = clockNow - MinElapsed;
            if (newStart > maxStart) newStart = maxStart;
            if (newStart <= startNow) return false;

            return startMember.TryWrite(timer, newStart);
        }

        /// <summary>丢掉已经不在场上的计时器; 返回清掉的条数。</summary>
        internal int Prune(IList<object> alive)
        {
            if (_entries.Count == 0) return 0;

            List<object> dead = null;
            foreach (object key in _entries.Keys)
            {
                bool found = false;
                if (alive != null)
                {
                    for (int i = 0; i < alive.Count; i++)
                        if (ReferenceEquals(alive[i], key)) { found = true; break; }
                }
                if (found) continue;
                if (dead == null) dead = new List<object>();
                dead.Add(key);
            }

            if (dead == null) return 0;
            for (int i = 0; i < dead.Count; i++) _entries.Remove(dead[i]);
            return dead.Count;
        }

        /// <summary>清空全部跟踪(战斗结束 / 关掉功能时)。</summary>
        internal void Clear()
        {
            _entries.Clear();
        }
    }
}
