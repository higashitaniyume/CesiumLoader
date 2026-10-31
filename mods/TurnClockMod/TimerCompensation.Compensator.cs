using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TurnClockMod
{

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
