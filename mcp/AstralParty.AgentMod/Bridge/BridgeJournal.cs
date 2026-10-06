using System;
using System.Collections.Generic;
using System.Text;
using AstralParty.Agent;
using CesiumLoader.SDK.Configuration;

namespace AstralParty.AgentMod.Bridge
{
    /// <summary>
    /// 事件流 / 原始动作流的收集与落盘。
    ///
    /// 线程模型(重要): 事件回调可能在**网络线程**触发, 而写盘只允许在主线程 tick 里做,
    /// 所以回调只把"已经序列化好的 JSON 行"塞进加锁队列, 由 <see cref="Flush"/> 统一写出。
    /// </summary>
    internal sealed class BridgeJournal
    {
        private readonly object _lock = new object();
        private readonly Queue<string> _eventLines = new Queue<string>();
        private readonly Queue<string> _actionLines = new Queue<string>();
        private readonly List<AgentActionRecord> _recentActions = new List<AgentActionRecord>();
        private readonly int _recentLimit;

        private long _eventsLogged;
        private long _actionsLogged;
        private long _droppedEvents;

        public BridgeJournal(int recentLimit)
        {
            _recentLimit = recentLimit < 1 ? 1 : recentLimit;
        }

        public long EventsLogged { get { lock (_lock) { return _eventsLogged; } } }
        public long ActionsLogged { get { lock (_lock) { return _actionsLogged; } } }
        public long DroppedEvents { get { lock (_lock) { return _droppedEvents; } } }

        /// <summary>记一条事件(任何线程可调)。extra 里的键值会平铺进 JSON 对象。</summary>
        public void AddEvent(string type, long playerId, IDictionary<string, object> extra)
        {
            try
            {
                var dict = new Dictionary<string, object>(8);
                dict["AtMs"] = AgentBridgeLayout.NowMs();
                dict["Type"] = type;
                if (playerId != 0) dict["PlayerId"] = playerId;
                if (extra != null)
                {
                    foreach (var kv in extra) dict[kv.Key] = kv.Value;
                }

                string line = CesiumJson.Serialize(dict);
                lock (_lock)
                {
                    // 队列水位保护: 外部没人读时也不能吃光内存(理论上每 tick 都会清空)
                    if (_eventLines.Count > 20000) { _droppedEvents++; return; }
                    _eventLines.Enqueue(line);
                    _eventsLogged++;
                }
            }
            catch { }
        }

        /// <summary>记一条原始动作(任何线程可调)。</summary>
        public void AddAction(AgentActionRecord record)
        {
            if (record == null) return;
            try
            {
                var dict = new Dictionary<string, object>(8);
                dict["AtMs"] = record.AtMs;
                dict["Id"] = record.Id;
                dict["Sn"] = record.Sn;
                dict["PlayerId"] = record.PlayerId;
                if (record.IsSelf) dict["IsSelf"] = true;
                dict["Len"] = record.Len;
                if (!string.IsNullOrEmpty(record.Decoded)) dict["Decoded"] = record.Decoded;
                string line = CesiumJson.Serialize(dict);

                lock (_lock)
                {
                    if (_actionLines.Count > 40000) return;
                    _actionLines.Enqueue(line);
                    _actionsLogged++;

                    _recentActions.Add(record);
                    if (_recentActions.Count > _recentLimit)
                        _recentActions.RemoveRange(0, _recentActions.Count - _recentLimit);
                }
            }
            catch { }
        }

        /// <summary>最近动作的快照(拷贝, 供 state.json 用)。</summary>
        public List<AgentActionRecord> RecentSnapshot()
        {
            lock (_lock) { return new List<AgentActionRecord>(_recentActions); }
        }

        /// <summary>把队列里的行批量追加到两个 jsonl(主线程调用)。返回写出的行数。</summary>
        public int Flush(string root, long rotateBytes)
        {
            int written = 0;
            written += FlushQueue(_eventLines, AgentBridgeLayout.EventsPath(root), rotateBytes);
            written += FlushQueue(_actionLines, AgentBridgeLayout.ActionsPath(root), rotateBytes);
            return written;
        }

        private int FlushQueue(Queue<string> queue, string path, long rotateBytes)
        {
            string block = null;
            int count = 0;
            lock (_lock)
            {
                if (queue.Count == 0) return 0;
                var sb = new StringBuilder(queue.Count * 96);
                while (queue.Count > 0) { sb.Append(queue.Dequeue()).Append('\n'); count++; }
                block = sb.ToString();
            }

            AgentBridgeLayout.RotateIfLarge(path, rotateBytes);
            if (!AgentBridgeLayout.AppendText(path, block))
            {
                // 写失败就把行放回队列头部(下一 tick 重试), 不丢事件
                lock (_lock)
                {
                    var lines = block.Split('\n');
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        if (lines[i].Length > 0) queue.Enqueue(lines[i]);
                    }
                }
                return 0;
            }
            return count;
        }
    }
}
