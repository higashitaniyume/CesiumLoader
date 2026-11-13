using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Gameplay;

namespace PvePanelMod
{
    /// <summary>右栏一行记录：这个玩家最近一次的商店或筹码。</summary>
    internal sealed class RightEntry
    {
        public bool IsShop;
        public int[] Ids = new int[0];          // 卡牌 id 或筹码 id
        public bool[] Bought = new bool[0];     // 商店：已买（与 Ids 对齐）
        public int PickId;                      // 筹码：最终选了哪个（0 = 还没选）
        public int Rerolls;                     // 筹码：刷新了几次
        public bool Done;                       // 已离店 / 已选定 → 变灰，但一直留着
        public long Sn;                         // 服务器会重复广播，按序号去重
        public DateTime StartedUtc;             // 用来让「刚更新」的那一行闪一下
        public DateTime TouchedUtc;             // 最后一次有动静的时间（兜底超时用）

        public RightEntry Clone()
        {
            var e = (RightEntry)MemberwiseClone();
            e.Ids = (int[])Ids.Clone();
            e.Bought = (bool[])Bought.Clone();
            return e;
        }
    }

    /// <summary>
    /// 每个玩家只留最近一次的商店或筹码（设计决定 A）：新的盖掉旧的；结束后变灰但不消失，直到这个人下一次进店或选筹码。
    /// 事件可能不在主线程来，所以读写都加锁；界面每次取一份拷贝。
    /// 兜底：离店消息可能丢，太久没动静就当已结束（商店 45 秒、筹码 120 秒）。
    /// </summary>
    internal sealed class RightState : IDisposable
    {
        private const double ShopStaleSeconds = 45, RelicStaleSeconds = 120;
        private readonly object _lock = new object();
        private readonly Dictionary<long, RightEntry> _byPlayer = new Dictionary<long, RightEntry>();
        private string _matchKey;

        public RightState()
        {
            GameEvents.ShopOffered += OnShopOffered;
            GameEvents.ShopSettled += OnShopSettled;
            GameEvents.RelicOffered += OnRelicOffered;
            GameEvents.RelicSelected += OnRelicSelected;
            GameEvents.StartAutoHook();
        }

        /// <summary>换局时清空（RoomId + StartTime 变了就是新的一局）。</summary>
        public void CheckMatch()
        {
            Players.MatchInfo m;
            if (!Players.TryGetMatch(out m)) return;
            string key = m.RoomId + "|" + m.StartTime;
            lock (_lock)
            {
                if (key == _matchKey) return;
                _matchKey = key;
                _byPlayer.Clear();
            }
        }

        public Dictionary<long, RightEntry> Snapshot()
        {
            var now = DateTime.UtcNow;
            var copy = new Dictionary<long, RightEntry>();
            lock (_lock)
            {
                foreach (var kv in _byPlayer)
                {
                    var e = kv.Value;
                    if (!e.Done && (now - e.TouchedUtc).TotalSeconds > (e.IsShop ? ShopStaleSeconds : RelicStaleSeconds)) e.Done = true;
                    copy[kv.Key] = e.Clone();
                }
            }
            return copy;
        }

        private void OnShopOffered(ShopOffer o)
        {
            if (o == null || !o.IsPve || o.CardIds == null) return;
            lock (_lock)
            {
                RightEntry e;
                bool same = _byPlayer.TryGetValue(o.PlayerId, out e) && e.IsShop && !e.Done;
                if (same && o.Sn != 0 && o.Sn == e.Sn) return;
                if (!same) e = NewEntry(o.PlayerId, true);
                e.Sn = o.Sn;
                e.Ids = (int[])o.CardIds.Clone();
                e.Bought = o.AlreadyBought != null && o.AlreadyBought.Length == o.CardIds.Length ? (bool[])o.AlreadyBought.Clone() : new bool[o.CardIds.Length];
                e.TouchedUtc = DateTime.UtcNow;
                if (o.IsClose) e.Done = true;
            }
        }

        private void OnShopSettled(ShopSettlement s)
        {
            if (s == null || !s.IsPve) return;
            lock (_lock)
            {
                RightEntry e;
                if (!_byPlayer.TryGetValue(s.PlayerId, out e) || !e.IsShop) return;
                if (s.Already != null && s.Already.Length == e.Ids.Length) e.Bought = (bool[])s.Already.Clone();
                e.TouchedUtc = DateTime.UtcNow;
                if (s.IsClose) e.Done = true;
            }
        }

        private void OnRelicOffered(RelicOffer o)
        {
            if (o == null || o.RelicIds == null || o.RelicIds.Length == 0) return;
            lock (_lock)
            {
                RightEntry e;
                bool selecting = _byPlayer.TryGetValue(o.PlayerId, out e) && !e.IsShop && !e.Done;
                if (selecting && o.Sn != 0 && o.Sn == e.Sn) return;
                if (selecting && o.IsReroll) e.Rerolls++;
                else if (!selecting) e = NewEntry(o.PlayerId, false);
                e.Sn = o.Sn;
                e.Ids = (int[])o.RelicIds.Clone();
                e.PickId = 0;
                e.TouchedUtc = DateTime.UtcNow;
            }
        }

        private void OnRelicSelected(long playerId, int relicId)
        {
            lock (_lock)
            {
                RightEntry e;
                if (!_byPlayer.TryGetValue(playerId, out e) || e.IsShop) return;
                e.PickId = relicId;
                e.Done = true;
                e.TouchedUtc = DateTime.UtcNow;
            }
        }

        private RightEntry NewEntry(long playerId, bool shop)
        {
            var now = DateTime.UtcNow;
            var e = new RightEntry { IsShop = shop, StartedUtc = now, TouchedUtc = now };
            _byPlayer[playerId] = e;
            return e;
        }

        public void Dispose()
        {
            GameEvents.ShopOffered -= OnShopOffered;
            GameEvents.ShopSettled -= OnShopSettled;
            GameEvents.RelicOffered -= OnRelicOffered;
            GameEvents.RelicSelected -= OnRelicSelected;
        }
    }
}
