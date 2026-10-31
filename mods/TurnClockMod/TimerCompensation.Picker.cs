using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TurnClockMod
{

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
}
