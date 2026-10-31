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
}
