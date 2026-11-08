using System;
using System.Collections.Generic;
using System.Text;

namespace ShopRelicPeekMod
{
    public sealed class RelicHistoryEntry
    {
        public List<int> Ids { get; set; }
        public DateTime Time { get; set; } = DateTime.MinValue;
    }

    public sealed class PlayerRelicState
    {
        public long PlayerId { get; set; }
        public int Slot { get; set; } = -1;
        public string Nick { get; set; }
        public string HeroName { get; set; }
        public bool IsSelecting { get; set; }
        public long LastSn { get; set; }
        public List<int> CurrentCandidates { get; } = new List<int>();
        public DateTime CurrentCandidatesTime { get; set; } = DateTime.MinValue;
        public List<RelicHistoryEntry> History { get; } = new List<RelicHistoryEntry>();
        public int SelectedRelicId { get; set; }
        public DateTime SelectedTime { get; set; } = DateTime.MinValue;

        public void OnCandidates(long sn, IReadOnlyList<int> relicIds)
        {
            OnCandidates(sn, relicIds, DateTime.UtcNow);
        }

        public void OnCandidates(long sn, IReadOnlyList<int> relicIds, DateTime now)
        {
            if (relicIds == null || relicIds.Count == 0) return;
            if (sn != 0 && sn == LastSn) return;

            if (IsSelecting && CurrentCandidates.Count > 0)
            {
                // Preserving previous candidate set in history on reroll
                History.Add(new RelicHistoryEntry
                {
                    Ids = new List<int>(CurrentCandidates),
                    Time = CurrentCandidatesTime
                });
            }

            CurrentCandidates.Clear();
            for (int i = 0; i < relicIds.Count; i++)
            {
                CurrentCandidates.Add(relicIds[i]);
            }
            CurrentCandidatesTime = now;
            SelectedRelicId = 0;
            SelectedTime = DateTime.MinValue;
            LastSn = sn;
            IsSelecting = true;
        }

        public void OnSelected(int relicId, DateTime now)
        {
            SelectedRelicId = relicId;
            SelectedTime = now;
            IsSelecting = false;
        }

        public bool IsExpired(DateTime now, int durationSeconds)
        {
            if (IsSelecting) return false;
            if (CurrentCandidates.Count == 0 && SelectedRelicId == 0) return true;
            if (SelectedTime == DateTime.MinValue) return true;
            return (now - SelectedTime).TotalSeconds > durationSeconds;
        }

        public void Reset()
        {
            History.Clear();
            CurrentCandidates.Clear();
            CurrentCandidatesTime = DateTime.MinValue;
            IsSelecting = false;
            SelectedRelicId = 0;
            SelectedTime = DateTime.MinValue;
            LastSn = 0;
        }
    }

    public sealed class PlayerShopState
    {
        public long PlayerId { get; set; }
        public int Slot { get; set; } = -1;
        public string Nick { get; set; }
        public string HeroName { get; set; }
        public string ShopType { get; set; } = "商店";
        public List<int> Cards { get; } = new List<int>();
        public List<bool> Alreadys { get; } = new List<bool>();
        public long LastSn { get; set; }
        public bool IsInShop { get; set; }
        public DateTime EnterTime { get; set; } = DateTime.MinValue;
        public DateTime LastSeenTime { get; set; } = DateTime.MinValue;
        public DateTime ExitTime { get; set; } = DateTime.MinValue;

        /// <summary>在店状态的保底过期秒数：关店包偶发缺失时，避免面板永久残留。</summary>
        public static int StaleThresholdSeconds(int durationSeconds)
        {
            return Math.Max(60, durationSeconds * 3);
        }

        public void OnShopEnter(string shopType, IReadOnlyList<int> cards, IReadOnlyList<bool> alreadys)
        {
            OnShopEnter(0, shopType, cards, alreadys, DateTime.UtcNow);
        }

        public void OnShopEnter(long sn, string shopType, IReadOnlyList<int> cards, IReadOnlyList<bool> alreadys, DateTime now)
        {
            if (cards == null || cards.Count == 0) return;
            if (sn != 0 && sn == LastSn) return; // 同 Sn 重复广播直接丢弃，避免覆盖关店后的离店状态
            ShopType = string.IsNullOrEmpty(shopType) ? "商店" : shopType;
            Cards.Clear();
            for (int i = 0; i < cards.Count; i++) Cards.Add(cards[i]);
            Alreadys.Clear();
            if (alreadys != null)
            {
                for (int i = 0; i < alreadys.Count; i++) Alreadys.Add(alreadys[i]);
            }
            IsInShop = true;
            EnterTime = now;
            LastSeenTime = now;
            LastSn = sn;
            ExitTime = DateTime.MaxValue;
        }

        /// <summary>商人等“瞬时快照”（没有在店停留语义）：记录后直接进入离店倒计时。</summary>
        public void OnShopSnapshot(string shopType, IReadOnlyList<int> cards, IReadOnlyList<bool> alreadys, DateTime now)
        {
            if (cards == null || cards.Count == 0) return;
            ShopType = string.IsNullOrEmpty(shopType) ? "商店" : shopType;
            Cards.Clear();
            for (int i = 0; i < cards.Count; i++) Cards.Add(cards[i]);
            Alreadys.Clear();
            if (alreadys != null)
            {
                for (int i = 0; i < alreadys.Count; i++) Alreadys.Add(alreadys[i]);
            }
            IsInShop = false;
            EnterTime = now;
            LastSeenTime = now;
            ExitTime = now;
        }

        public void UpdateAlreadys(IReadOnlyList<bool> alreadys)
        {
            UpdateAlreadys(alreadys, DateTime.UtcNow);
        }

        public void UpdateAlreadys(IReadOnlyList<bool> alreadys, DateTime now)
        {
            if (alreadys == null) return;
            Alreadys.Clear();
            for (int i = 0; i < alreadys.Count; i++) Alreadys.Add(alreadys[i]);
            LastSeenTime = now;
        }

        public void OnShopClose(DateTime now)
        {
            // 无论当前是否标记在店都落 ExitTime（关店包可能先于进店包到达或被去重丢掉）
            IsInShop = false;
            ExitTime = now;
            if (LastSeenTime == DateTime.MinValue) LastSeenTime = now;
        }

        public bool IsExpired(DateTime now, int durationSeconds)
        {
            if (Cards.Count == 0) return true;
            if (IsInShop)
            {
                // 在店保底：长时间无任何商店包（关店遗失 / 逻辑卡住）则视为过期
                DateTime mark = LastSeenTime != DateTime.MinValue ? LastSeenTime : EnterTime;
                if (mark != DateTime.MinValue && (now - mark).TotalSeconds > StaleThresholdSeconds(durationSeconds))
                    return true;
                return false;
            }
            if (ExitTime == DateTime.MinValue || ExitTime == DateTime.MaxValue) return true;
            return (now - ExitTime).TotalSeconds > durationSeconds;
        }

        public void Reset()
        {
            Cards.Clear();
            Alreadys.Clear();
            IsInShop = false;
            LastSn = 0;
            EnterTime = DateTime.MinValue;
            LastSeenTime = DateTime.MinValue;
            ExitTime = DateTime.MinValue;
        }
    }

    public struct RelicItemInfo
    {
        public string Name;
        public int Quality;
        public RelicItemInfo(string name, int quality)
        {
            Name = name;
            Quality = quality;
        }
    }

    public sealed class ShopRelicTracker
    {
        private readonly object _lock = new object();
        private readonly Dictionary<long, PlayerRelicState> _relics = new Dictionary<long, PlayerRelicState>();
        private readonly Dictionary<long, PlayerShopState> _shops = new Dictionary<long, PlayerShopState>();

        public Func<int, string> CardNameResolver { get; set; }
        public Func<int, RelicItemInfo> RelicInfoResolver { get; set; }
        public Func<long, string> NickResolver { get; set; }
        public Func<long, string> CharacterResolver { get; set; }
        public Func<long, int> SlotResolver { get; set; }

        public static readonly string[] SeatNames = { "一楼", "二楼", "三楼", "四楼" };
        public static readonly string[] SeatColors = { "#FF4646", "#94FF46", "#4386F4", "#FFB346" };

        public static string GetSeatName(int slot)
        {
            if (slot >= 0 && slot < SeatNames.Length) return SeatNames[slot];
            return "玩家";
        }

        public static string GetSeatColor(int slot)
        {
            if (slot >= 0 && slot < SeatColors.Length) return SeatColors[slot];
            return "#E0E0E0";
        }

        public void RecordRelicCandidates(long playerId, long sn, IReadOnlyList<int> relics)
        {
            RecordRelicCandidates(playerId, sn, relics, DateTime.UtcNow);
        }

        public void RecordRelicCandidates(long playerId, long sn, IReadOnlyList<int> relics, DateTime now)
        {
            if (playerId == 0 || relics == null || relics.Count == 0) return;
            lock (_lock)
            {
                if (!_relics.TryGetValue(playerId, out var state))
                {
                    state = new PlayerRelicState { PlayerId = playerId };
                    _relics[playerId] = state;
                }
                if (state.Slot < 0 && SlotResolver != null) state.Slot = SlotResolver(playerId);
                if (string.IsNullOrEmpty(state.Nick) && NickResolver != null) state.Nick = NickResolver(playerId);
                if (string.IsNullOrEmpty(state.HeroName) && CharacterResolver != null) state.HeroName = CharacterResolver(playerId);
                state.OnCandidates(sn, relics, now);
            }
        }

        public void RecordRelicSelected(long playerId, int relicId, DateTime now)
        {
            if (playerId == 0) return;
            lock (_lock)
            {
                if (_relics.TryGetValue(playerId, out var state))
                {
                    state.OnSelected(relicId, now);
                }
                else
                {
                    state = new PlayerRelicState { PlayerId = playerId };
                    if (SlotResolver != null) state.Slot = SlotResolver(playerId);
                    if (NickResolver != null) state.Nick = NickResolver(playerId);
                    if (CharacterResolver != null) state.HeroName = CharacterResolver(playerId);
                    state.OnSelected(relicId, now);
                    _relics[playerId] = state;
                }
            }
        }

        public void RecordShopEnter(long playerId, string shopType, IReadOnlyList<int> cards, IReadOnlyList<bool> alreadys)
        {
            RecordShopEnter(0, playerId, shopType, cards, alreadys, DateTime.UtcNow);
        }

        public void RecordShopEnter(long playerId, string shopType, IReadOnlyList<int> cards, IReadOnlyList<bool> alreadys, DateTime now)
        {
            RecordShopEnter(0, playerId, shopType, cards, alreadys, now);
        }

        public void RecordShopEnter(long sn, long playerId, string shopType, IReadOnlyList<int> cards, IReadOnlyList<bool> alreadys, DateTime now)
        {
            if (playerId == 0 || cards == null || cards.Count == 0) return;
            lock (_lock)
            {
                if (!_shops.TryGetValue(playerId, out var state))
                {
                    state = new PlayerShopState { PlayerId = playerId };
                    _shops[playerId] = state;
                }
                if (state.Slot < 0 && SlotResolver != null) state.Slot = SlotResolver(playerId);
                if (string.IsNullOrEmpty(state.Nick) && NickResolver != null) state.Nick = NickResolver(playerId);
                if (string.IsNullOrEmpty(state.HeroName) && CharacterResolver != null) state.HeroName = CharacterResolver(playerId);
                state.OnShopEnter(sn, shopType, cards, alreadys, now);
            }
        }

        public void RecordShopSnapshot(long playerId, string shopType, IReadOnlyList<int> cards, IReadOnlyList<bool> alreadys, DateTime now)
        {
            if (playerId == 0 || cards == null || cards.Count == 0) return;
            lock (_lock)
            {
                if (!_shops.TryGetValue(playerId, out var state))
                {
                    state = new PlayerShopState { PlayerId = playerId };
                    _shops[playerId] = state;
                }
                if (state.Slot < 0 && SlotResolver != null) state.Slot = SlotResolver(playerId);
                if (string.IsNullOrEmpty(state.Nick) && NickResolver != null) state.Nick = NickResolver(playerId);
                if (string.IsNullOrEmpty(state.HeroName) && CharacterResolver != null) state.HeroName = CharacterResolver(playerId);
                state.OnShopSnapshot(shopType, cards, alreadys, now);
            }
        }

        public void RecordShopClose(long playerId, DateTime now)
        {
            if (playerId == 0) return;
            lock (_lock)
            {
                if (_shops.TryGetValue(playerId, out var state))
                {
                    state.OnShopClose(now);
                }
            }
        }

        public void RecordShopAlreadys(long playerId, IReadOnlyList<bool> alreadys)
        {
            RecordShopAlreadys(playerId, alreadys, DateTime.UtcNow);
        }

        public void RecordShopAlreadys(long playerId, IReadOnlyList<bool> alreadys, DateTime now)
        {
            if (playerId == 0 || alreadys == null) return;
            lock (_lock)
            {
                if (_shops.TryGetValue(playerId, out var state))
                {
                    state.UpdateAlreadys(alreadys, now);
                }
            }
        }

        /// <summary>
        /// 相对时间格式化（与显示时长过期逻辑同源，统一使用 DateTime.UtcNow）：
        /// 不满 1 秒显示“刚刚”，否则显示“Xs前”（纯秒数，不进分钟）。
        /// </summary>
        public static string FormatAge(DateTime time, DateTime now)
        {
            if (time == DateTime.MinValue) return string.Empty;
            double seconds = (now - time).TotalSeconds;
            if (seconds < 1.0) return "刚刚";
            return ((int)seconds) + "秒前";
        }

        public void OnRoundChanged()
        {
            OnRoundChanged(DateTime.UtcNow);
        }

        public void OnRoundChanged(DateTime now)
        {
            lock (_lock)
            {
                foreach (var s in _shops.Values)
                {
                    // 新回合开始时商店阶段已结束：在店状态强制离店（走离店倒计时），不永久保留
                    if (s.IsInShop) s.OnShopClose(now);
                    else s.Reset();
                }
                foreach (var r in _relics.Values)
                {
                    if (!r.IsSelecting) r.Reset();
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _relics.Clear();
                _shops.Clear();
            }
        }

        public bool HasActiveShop(DateTime now, int shopDurationSeconds)
        {
            lock (_lock)
            {
                foreach (var s in _shops.Values)
                {
                    if (!s.IsExpired(now, shopDurationSeconds) && s.Cards.Count > 0) return true;
                }
                return false;
            }
        }

        public bool HasActiveRelic(DateTime now, int relicDurationSeconds)
        {
            lock (_lock)
            {
                foreach (var r in _relics.Values)
                {
                    if (!r.IsExpired(now, relicDurationSeconds)) return true;
                }
                return false;
            }
        }

        public string BuildShopOverlayText(ShopRelicPeekConfig config, DateTime now)
        {
            if (config == null || !config.Enabled || !config.ShowShopCards) return null;

            List<PlayerShopState> activeShops;
            lock (_lock)
            {
                activeShops = new List<PlayerShopState>();
                foreach (var pair in _shops)
                {
                    if (!pair.Value.IsExpired(now, config.ShopDisplayDurationSeconds) && pair.Value.Cards.Count > 0)
                    {
                        var s = pair.Value;
                        var clone = new PlayerShopState
                        {
                            PlayerId = s.PlayerId,
                            Slot = s.Slot,
                            Nick = s.Nick,
                            HeroName = s.HeroName,
                            ShopType = s.ShopType,
                            IsInShop = s.IsInShop,
                            EnterTime = s.EnterTime,
                            LastSeenTime = s.LastSeenTime,
                        };
                        clone.Cards.AddRange(s.Cards);
                        clone.Alreadys.AddRange(s.Alreadys);
                        activeShops.Add(clone);
                    }
                }
            }

            // Resolvers touch game config (GetLocal) — do them outside the lock.
            foreach (var s in activeShops)
            {
                if (s.Slot < 0 && SlotResolver != null) s.Slot = SlotResolver(s.PlayerId);
                if (string.IsNullOrEmpty(s.Nick) && NickResolver != null) s.Nick = NickResolver(s.PlayerId);
                if (string.IsNullOrEmpty(s.Nick)) s.Nick = "P" + s.PlayerId;
                if (string.IsNullOrEmpty(s.HeroName) && CharacterResolver != null) s.HeroName = CharacterResolver(s.PlayerId);
            }

            if (activeShops.Count == 0) return null;

            activeShops.Sort((a, b) =>
            {
                int sa = a.Slot >= 0 ? a.Slot : 999;
                int sb = b.Slot >= 0 ? b.Slot : 999;
                if (sa != sb) return sa.CompareTo(sb);
                return a.PlayerId.CompareTo(b.PlayerId);
            });

            var sb = new StringBuilder();
            for (int i = 0; i < activeShops.Count; i++)
            {
                if (i > 0) sb.Append("\n\n");
                AppendShopPlayerSection(sb, activeShops[i], config, now);
            }
            return sb.ToString();
        }

        public string BuildRelicOverlayText(ShopRelicPeekConfig config, DateTime now)
        {
            if (config == null || !config.Enabled || !config.ShowRelicCandidates) return null;

            List<PlayerRelicState> activeRelics;
            lock (_lock)
            {
                activeRelics = new List<PlayerRelicState>();
                foreach (var pair in _relics)
                {
                    if (!pair.Value.IsExpired(now, config.RelicDisplayDurationSeconds))
                    {
                        var r = pair.Value;
                        var clone = new PlayerRelicState
                        {
                            PlayerId = r.PlayerId,
                            Slot = r.Slot,
                            Nick = r.Nick,
                            HeroName = r.HeroName,
                            IsSelecting = r.IsSelecting,
                            LastSn = r.LastSn,
                            CurrentCandidatesTime = r.CurrentCandidatesTime,
                            SelectedRelicId = r.SelectedRelicId,
                            SelectedTime = r.SelectedTime,
                        };
                        clone.CurrentCandidates.AddRange(r.CurrentCandidates);
                        foreach (var h in r.History) clone.History.Add(new RelicHistoryEntry { Ids = new List<int>(h.Ids), Time = h.Time });
                        activeRelics.Add(clone);
                    }
                }
            }

            foreach (var r in activeRelics)
            {
                if (r.Slot < 0 && SlotResolver != null) r.Slot = SlotResolver(r.PlayerId);
                if (string.IsNullOrEmpty(r.Nick) && NickResolver != null) r.Nick = NickResolver(r.PlayerId);
                if (string.IsNullOrEmpty(r.Nick)) r.Nick = "P" + r.PlayerId;
                if (string.IsNullOrEmpty(r.HeroName) && CharacterResolver != null) r.HeroName = CharacterResolver(r.PlayerId);
            }

            if (activeRelics.Count == 0) return null;

            activeRelics.Sort((a, b) =>
            {
                int sa = a.Slot >= 0 ? a.Slot : 999;
                int sb = b.Slot >= 0 ? b.Slot : 999;
                if (sa != sb) return sa.CompareTo(sb);
                return a.PlayerId.CompareTo(b.PlayerId);
            });

            var sb = new StringBuilder();
            for (int i = 0; i < activeRelics.Count; i++)
            {
                if (i > 0) sb.Append("\n\n");
                AppendRelicPlayerSection(sb, activeRelics[i], config, now);
            }
            return sb.ToString();
        }

        private void AppendShopPlayerSection(StringBuilder sb, PlayerShopState shop, ShopRelicPeekConfig config, DateTime now)
        {
            string seatColor = GetSeatColor(shop.Slot);
            string seatName = GetSeatName(shop.Slot);

            if (config.ShowFloorTag)
            {
                sb.Append("[color=").Append(seatColor).Append("][b]【").Append(seatName).Append("】").Append(shop.Nick).Append("[/b][/color]");
            }
            else
            {
                sb.Append("[color=").Append(seatColor).Append("][b]").Append(shop.Nick).Append("[/b][/color]");
            }

            if (config.ShowCharacterName && !string.IsNullOrEmpty(shop.HeroName))
            {
                sb.Append(" [color=#FFD86B]（").Append(shop.HeroName).Append("）[/color]");
            }

            if (config.ShowShopAge)
            {
                string age = FormatAge(shop.IsInShop ? shop.LastSeenTime : shop.EnterTime, now);
                if (!string.IsNullOrEmpty(age))
                {
                    sb.Append(" [color=#9AA7B8]· ").Append(age).Append("[/color]");
                }
            }

            sb.Append("\n  [color=#FFD24A]").Append(shop.ShopType).Append("遇牌:[/color] ");
            AppendCardList(sb, shop);
        }

        private void AppendRelicPlayerSection(StringBuilder sb, PlayerRelicState relic, ShopRelicPeekConfig config, DateTime now)
        {
            string seatColor = GetSeatColor(relic.Slot);
            string seatName = GetSeatName(relic.Slot);

            if (config.ShowFloorTag)
            {
                sb.Append("[color=").Append(seatColor).Append("][b]【").Append(seatName).Append("】").Append(relic.Nick).Append("[/b][/color]");
            }
            else
            {
                sb.Append("[color=").Append(seatColor).Append("][b]").Append(relic.Nick).Append("[/b][/color]");
            }

            if (config.ShowCharacterName && !string.IsNullOrEmpty(relic.HeroName))
            {
                sb.Append(" [color=#FFD86B]（").Append(relic.HeroName).Append("）[/color]");
            }

            if (config.ShowRelicAge)
            {
                DateTime headerTime = relic.IsSelecting
                    ? relic.CurrentCandidatesTime
                    : (relic.SelectedTime != DateTime.MinValue ? relic.SelectedTime : relic.CurrentCandidatesTime);
                string age = FormatAge(headerTime, now);
                if (!string.IsNullOrEmpty(age))
                {
                    sb.Append(" [color=#9AA7B8]· ").Append(age).Append("[/color]");
                }
            }

            if (relic.IsSelecting)
            {
                // In progress of selecting
                if (relic.History.Count > 0)
                {
                    sb.Append("\n  [color=#AAAAAA]筹码候选历史:[/color]");
                    for (int h = 0; h < relic.History.Count; h++)
                    {
                        var entry = relic.History[h];
                        sb.Append("\n    [color=#888888]第 ").Append(h + 1).Append(" 次");
                        if (config.ShowRelicAge)
                        {
                            string histAge = FormatAge(entry.Time, now);
                            if (!string.IsNullOrEmpty(histAge))
                            {
                                sb.Append(" (").Append(histAge).Append(")");
                            }
                        }
                        sb.Append(":[/color] ");
                        AppendRelicList(sb, entry.Ids, config.ColorRelicByQuality, selectedId: 0);
                    }
                    sb.Append("\n    [color=#FFFFFF][b]当前候选");
                    if (config.ShowRelicAge)
                    {
                        string curAge = FormatAge(relic.CurrentCandidatesTime, now);
                        if (!string.IsNullOrEmpty(curAge))
                        {
                            sb.Append(" (").Append(curAge).Append(")");
                        }
                    }
                    sb.Append(":[/b][/color] ");
                    AppendRelicList(sb, relic.CurrentCandidates, config.ColorRelicByQuality, selectedId: 0);
                }
                else
                {
                    sb.Append("\n  [color=#FFFFFF][b]候选筹码");
                    if (config.ShowRelicAge)
                    {
                        string curAge = FormatAge(relic.CurrentCandidatesTime, now);
                        if (!string.IsNullOrEmpty(curAge))
                        {
                            sb.Append(" (").Append(curAge).Append(")");
                        }
                    }
                    sb.Append(":[/b][/color] ");
                    AppendRelicList(sb, relic.CurrentCandidates, config.ColorRelicByQuality, selectedId: 0);
                }
            }
            else
            {
                // Selection finished, showing results during display duration
                if (relic.History.Count > 0)
                {
                    sb.Append("\n  [color=#AAAAAA]筹码候选历史:[/color]");
                    for (int h = 0; h < relic.History.Count; h++)
                    {
                        var entry = relic.History[h];
                        sb.Append("\n    [color=#888888]第 ").Append(h + 1).Append(" 次");
                        if (config.ShowRelicAge)
                        {
                            string histAge = FormatAge(entry.Time, now);
                            if (!string.IsNullOrEmpty(histAge))
                            {
                                sb.Append(" (").Append(histAge).Append(")");
                            }
                        }
                        sb.Append(":[/color] ");
                        AppendRelicList(sb, entry.Ids, config.ColorRelicByQuality, selectedId: 0);
                    }
                    if (relic.CurrentCandidates.Count > 0)
                    {
                        sb.Append("\n    [color=#CCCCCC]最终轮候选");
                        if (config.ShowRelicAge)
                        {
                            string curAge = FormatAge(relic.CurrentCandidatesTime, now);
                            if (!string.IsNullOrEmpty(curAge))
                            {
                                sb.Append(" (").Append(curAge).Append(")");
                            }
                        }
                        sb.Append(":[/color] ");
                        AppendRelicList(sb, relic.CurrentCandidates, config.ColorRelicByQuality, relic.SelectedRelicId);
                    }
                }
                else if (relic.CurrentCandidates.Count > 0)
                {
                    sb.Append("\n  [color=#CCCCCC]候选筹码");
                    if (config.ShowRelicAge)
                    {
                        string curAge = FormatAge(relic.CurrentCandidatesTime, now);
                        if (!string.IsNullOrEmpty(curAge))
                        {
                            sb.Append(" (").Append(curAge).Append(")");
                        }
                    }
                    sb.Append(":[/color] ");
                    AppendRelicList(sb, relic.CurrentCandidates, config.ColorRelicByQuality, relic.SelectedRelicId);
                }

                if (relic.SelectedRelicId > 0)
                {
                    var selectedInfo = RelicInfoResolver != null
                        ? RelicInfoResolver(relic.SelectedRelicId)
                        : new RelicItemInfo("筹码" + relic.SelectedRelicId, 0);

                    string qColor = config.ColorRelicByQuality ? QualityColor(selectedInfo.Quality) : "#FFFFFF";
                    sb.Append("\n  [color=#FFD24A][b]✔ 最终选定:[/b][/color] [color=").Append(qColor).Append("][b]")
                      .Append(selectedInfo.Name).Append("[/b][/color]");
                }
            }
        }

        private void AppendCardList(StringBuilder sb, PlayerShopState shop)
        {
            for (int i = 0; i < shop.Cards.Count; i++)
            {
                if (i > 0) sb.Append("、");
                int cardId = shop.Cards[i];
                string name = CardNameResolver != null ? CardNameResolver(cardId) : ("卡牌" + cardId);
                bool sold = i < shop.Alreadys.Count && shop.Alreadys[i];

                if (sold)
                {
                    sb.Append("[color=#888888]").Append(name).Append("(已售)[/color]");
                }
                else
                {
                    sb.Append("[color=#E6F0FF]").Append(name).Append("[/color]");
                }
            }
        }

        private void AppendRelicList(StringBuilder sb, IReadOnlyList<int> relics, bool colorByQuality, int selectedId)
        {
            for (int i = 0; i < relics.Count; i++)
            {
                if (i > 0) sb.Append("、");
                int relicId = relics[i];
                var info = RelicInfoResolver != null ? RelicInfoResolver(relicId) : new RelicItemInfo("筹码" + relicId, 0);

                bool isSelected = selectedId > 0 && relicId == selectedId;
                string baseColor = colorByQuality ? QualityColor(info.Quality) : "#FFFFFF";

                if (isSelected)
                {
                    sb.Append("[color=").Append(baseColor).Append("][b]★").Append(info.Name).Append("(已选)[/b][/color]");
                }
                else
                {
                    sb.Append("[color=").Append(baseColor).Append("]").Append(info.Name).Append("[/color]");
                }
            }
        }

        public static string QualityColor(int quality)
        {
            switch (quality)
            {
                case 1: return "#5AA9FF"; // 普通 Blue
                case 2: return "#D26DFF"; // 稀有 Purple
                case 3: return "#FFAA00"; // 传说 Orange
                default: return "#E0E0E0";
            }
        }
    }
}
