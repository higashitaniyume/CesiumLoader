using System;
using System.Collections.Generic;
using System.Text;

namespace OverlayFeedMod
{
    public sealed class PlayerRelicState
    {
        public long PlayerId { get; set; }
        public int Slot { get; set; } = -1;
        public string Nick { get; set; }
        public bool IsSelecting { get; set; }
        public long LastSn { get; set; }
        public List<int> CurrentCandidates { get; } = new List<int>();
        public List<List<int>> History { get; } = new List<List<int>>();
        public int SelectedRelicId { get; set; }
        public DateTime SelectedTime { get; set; } = DateTime.MinValue;

        public void OnCandidates(long sn, IReadOnlyList<int> relicIds)
        {
            if (relicIds == null || relicIds.Count == 0) return;
            if (sn != 0 && sn == LastSn) return;

            if (IsSelecting && CurrentCandidates.Count > 0)
            {
                // Preserving previous candidate set in history on reroll
                History.Add(new List<int>(CurrentCandidates));
            }

            CurrentCandidates.Clear();
            for (int i = 0; i < relicIds.Count; i++)
            {
                CurrentCandidates.Add(relicIds[i]);
            }
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
        public string ShopType { get; set; } = "商店";
        public List<int> Cards { get; } = new List<int>();
        public List<bool> Alreadys { get; } = new List<bool>();
        public bool IsInShop { get; set; }
        public DateTime ExitTime { get; set; } = DateTime.MinValue;

        public void OnShopEnter(string shopType, IReadOnlyList<int> cards, IReadOnlyList<bool> alreadys)
        {
            ShopType = string.IsNullOrEmpty(shopType) ? "商店" : shopType;
            Cards.Clear();
            if (cards != null)
            {
                for (int i = 0; i < cards.Count; i++) Cards.Add(cards[i]);
            }
            Alreadys.Clear();
            if (alreadys != null)
            {
                for (int i = 0; i < alreadys.Count; i++) Alreadys.Add(alreadys[i]);
            }
            IsInShop = true;
            ExitTime = DateTime.MaxValue;
        }

        public void UpdateAlreadys(IReadOnlyList<bool> alreadys)
        {
            if (alreadys == null) return;
            Alreadys.Clear();
            for (int i = 0; i < alreadys.Count; i++) Alreadys.Add(alreadys[i]);
        }

        public void OnShopClose(DateTime now)
        {
            if (IsInShop)
            {
                IsInShop = false;
                ExitTime = now;
            }
        }

        public bool IsExpired(DateTime now, int durationSeconds)
        {
            if (IsInShop) return false;
            if (Cards.Count == 0) return true;
            if (ExitTime == DateTime.MinValue || ExitTime == DateTime.MaxValue) return true;
            return (now - ExitTime).TotalSeconds > durationSeconds;
        }

        public void Reset()
        {
            Cards.Clear();
            Alreadys.Clear();
            IsInShop = false;
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
                state.OnCandidates(sn, relics);
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
                    state.OnSelected(relicId, now);
                    _relics[playerId] = state;
                }
            }
        }

        public void RecordShopEnter(long playerId, string shopType, IReadOnlyList<int> cards, IReadOnlyList<bool> alreadys)
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
                state.OnShopEnter(shopType, cards, alreadys);
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
            if (playerId == 0 || alreadys == null) return;
            lock (_lock)
            {
                if (_shops.TryGetValue(playerId, out var state))
                {
                    state.UpdateAlreadys(alreadys);
                }
            }
        }

        public void OnRoundChanged()
        {
            lock (_lock)
            {
                foreach (var s in _shops.Values)
                {
                    if (!s.IsInShop) s.Reset();
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

        public string BuildShopOverlayText(OverlayFeedConfig config, DateTime now)
        {
            if (config == null || !config.Enabled || !config.ShowShopCards) return null;

            lock (_lock)
            {
                var activeShops = new List<PlayerShopState>();
                foreach (var pair in _shops)
                {
                    if (!pair.Value.IsExpired(now, config.ShopDisplayDurationSeconds) && pair.Value.Cards.Count > 0)
                    {
                        var s = pair.Value;
                        if (s.Slot < 0 && SlotResolver != null) s.Slot = SlotResolver(s.PlayerId);
                        if (string.IsNullOrEmpty(s.Nick) && NickResolver != null) s.Nick = NickResolver(s.PlayerId);
                        if (string.IsNullOrEmpty(s.Nick)) s.Nick = "P" + s.PlayerId;
                        activeShops.Add(s);
                    }
                }

                if (activeShops.Count == 0) return null;

                // Sort: 0 (一楼), 1 (二楼), 2 (三楼), 3 (四楼), -1 (未知)
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
                    AppendShopPlayerSection(sb, activeShops[i], config);
                }
                return sb.ToString();
            }
        }

        public string BuildRelicOverlayText(OverlayFeedConfig config, DateTime now)
        {
            if (config == null || !config.Enabled || !config.ShowRelicCandidates) return null;

            lock (_lock)
            {
                var activeRelics = new List<PlayerRelicState>();
                foreach (var pair in _relics)
                {
                    if (!pair.Value.IsExpired(now, config.RelicDisplayDurationSeconds))
                    {
                        var r = pair.Value;
                        if (r.Slot < 0 && SlotResolver != null) r.Slot = SlotResolver(r.PlayerId);
                        if (string.IsNullOrEmpty(r.Nick) && NickResolver != null) r.Nick = NickResolver(r.PlayerId);
                        if (string.IsNullOrEmpty(r.Nick)) r.Nick = "P" + r.PlayerId;
                        activeRelics.Add(r);
                    }
                }

                if (activeRelics.Count == 0) return null;

                // Sort: 0 (一楼), 1 (二楼), 2 (三楼), 3 (四楼), -1 (未知)
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
                    AppendRelicPlayerSection(sb, activeRelics[i], config);
                }
                return sb.ToString();
            }
        }

        private void AppendShopPlayerSection(StringBuilder sb, PlayerShopState shop, OverlayFeedConfig config)
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

            sb.Append("\n  [color=#FFD24A]").Append(shop.ShopType).Append("遇牌:[/color] ");
            AppendCardList(sb, shop);
        }

        private void AppendRelicPlayerSection(StringBuilder sb, PlayerRelicState relic, OverlayFeedConfig config)
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

            if (relic.IsSelecting)
            {
                // In progress of selecting
                if (relic.History.Count > 0)
                {
                    sb.Append("\n  [color=#AAAAAA]筹码候选历史:[/color]");
                    for (int h = 0; h < relic.History.Count; h++)
                    {
                        sb.Append("\n    [color=#888888]第 ").Append(h + 1).Append(" 次:[/color] ");
                        AppendRelicList(sb, relic.History[h], config.ColorRelicByQuality, selectedId: 0);
                    }
                    sb.Append("\n    [color=#FFFFFF][b]当前候选:[/b][/color] ");
                    AppendRelicList(sb, relic.CurrentCandidates, config.ColorRelicByQuality, selectedId: 0);
                }
                else
                {
                    sb.Append("\n  [color=#FFFFFF][b]候选筹码:[/b][/color] ");
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
                        sb.Append("\n    [color=#888888]第 ").Append(h + 1).Append(" 次:[/color] ");
                        AppendRelicList(sb, relic.History[h], config.ColorRelicByQuality, selectedId: 0);
                    }
                    if (relic.CurrentCandidates.Count > 0)
                    {
                        sb.Append("\n    [color=#CCCCCC]最终轮候选:[/color] ");
                        AppendRelicList(sb, relic.CurrentCandidates, config.ColorRelicByQuality, relic.SelectedRelicId);
                    }
                }
                else if (relic.CurrentCandidates.Count > 0)
                {
                    sb.Append("\n  [color=#CCCCCC]候选筹码:[/color] ");
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

