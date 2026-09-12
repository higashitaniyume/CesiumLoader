using System;
using System.Collections.Generic;
using CesiumLoader.SDK;
using GameLogic;
using party.model;
using Tools;
using UI;

namespace ActivityLogMod
{
    /// <summary>
    /// 行为日志 mod: 基于 CesiumLoader.SDK, 订阅游戏事件 + 每秒轮询,
    /// 把玩家在游戏里的主要行为输出到加载器控制台。
    ///
    /// 观测内容:
    ///   - 界面切换 / 房间状态 / 队友信息 / 自己选角(轮询)
    ///   - 星币 / 自己摸牌 / 手牌(轮询)
    ///   - 用牌 / 效果牌 / 技能 / 跟牌 / 骰子 / 移动 / 战斗 / 商店 / 筹码 / 遗物 / 选卡(事件)
    /// </summary>
    public static class ModEntry
    {
        public static void Main()
        {
            SdkLog.Write("ActivityLog", "=== ActivityLogMod.Main 被调用 ===");
            ModBase.Run(init: OnInit, tick: OnTick, tag: "ActivityLog");
        }

        private static void OnInit()
        {
            SdkLog.Write("ActivityLog", "初始化: 订阅游戏事件");
            GameEvents.CardUsed += OnCardUsed;
            GameEvents.NoCard += OnNoCard;
            GameEvents.EffectCardUsed += OnEffectCardUsed;
            GameEvents.SkillUsed += OnSkillUsed;
            GameEvents.QuickCardUsed += OnQuickCardUsed;
            GameEvents.DiceResult += OnDiceResult;
            GameEvents.Move += OnMove;
            GameEvents.BattleUpdate += OnBattleUpdate;
            GameEvents.BattleDice += OnBattleDice;
            GameEvents.RewardCardSelected += OnRewardCardSelected;
            GameEvents.ShopCandidates += OnShopCandidates;
            GameEvents.RelicCandidates += OnRelicCandidates;
            GameEvents.RelicSelected += OnRelicSelected;
            GameEvents.RelicsSynced += OnRelicsSynced;
            GameEvents.HandChanged += OnHandChanged;
            SdkLog.Write("ActivityLog", "事件订阅完成");
        }

        // ---------- 事件处理 ----------

        private static void OnCardUsed(long playerId, int cardId, int remain)
        {
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[用牌] {who}{self} 使用了: {Names.Card(cardId)} (剩余{remain}张)");
        }

        private static void OnNoCard(long playerId)
        {
            string who = Nick(playerId);
            SdkLog.Write("ActivityLog", $"[用牌] {who} 无牌可出, 跳过出牌");
        }

        private static void OnEffectCardUsed(long playerId, int cardId, int remain)
        {
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[效果牌] {who}{self} 使用了: {Names.Card(cardId)} (剩余{remain}张)");
        }

        private static void OnSkillUsed(long playerId, int skillId)
        {
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[技能] {who}{self} 释放了技能: {Names.Skill(skillId)}");
        }

        private static void OnQuickCardUsed(long playerId, int cardId, int originalCardId)
        {
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            string orig = (originalCardId != 0) ? $" (跟{Names.Card(originalCardId)})" : "";
            SdkLog.Write("ActivityLog", $"[跟牌] {who}{self} 使用了: {Names.Card(cardId)}{orig}");
        }

        private static void OnDiceResult(long playerId, int point, int maxPoint)
        {
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            string bonus = (maxPoint != point && maxPoint > 0) ? $" (上限{maxPoint})" : "";
            SdkLog.Write("ActivityLog", $"[骰子] {who}{self} 掷出了: {point}点{bonus}");
        }

        private static void OnMove(long playerId, int stepCount, bool end)
        {
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[移动] {who}{self} 移动了 {stepCount} 格{(end ? " (到达)" : "")}");
        }

        private static void OnBattleUpdate(Battle b)
        {
            if (b.Attacker == null || b.Defender == null) return;
            string atk = Names.BattleRole(b.Attacker);
            string def = Names.BattleRole(b.Defender);
            string end = b.IsEnd ? " [已结束]" : "";
            string self = Players.IsSelf(b.Attacker.PlayerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[战斗] {atk}{self} vs {def} 攻{b.Attacker.Point}(ATK{b.Attacker.Atk}) vs 防{b.Defender.Point}(DEF{b.Defender.Def}){end}");
        }

        private static void OnBattleDice(long playerId, int val)
        {
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[战斗骰] {who}{self} 掷出攻击: {val}点");
        }

        private static void OnRewardCardSelected(long playerId, int cardId)
        {
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[选卡] {who}{self} 选择了奖励卡: {Names.Card(cardId)}");
        }

        private static void OnShopCandidates(long playerId, IReadOnlyList<int> cardIds)
        {
            var names = new List<string>();
            foreach (int cid in cardIds) if (cid > 0) names.Add(Names.Card(cid));
            if (names.Count == 0) return;
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[商店] {who}{self} 的待选卡: " + string.Join(" | ", names));
        }

        private static void OnRelicCandidates(long playerId, IReadOnlyList<int> relicIds)
        {
            var names = new List<string>();
            foreach (int rid in relicIds) if (rid > 0) names.Add(Names.Relic(rid));
            if (names.Count == 0) return;
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[筹码] {who}{self} 的候选遗物: " + string.Join(" | ", names));
        }

        private static void OnRelicSelected(long playerId, int relicId)
        {
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[筹码] {who}{self} 选择了: {Names.Relic(relicId)}");
        }

        private static void OnRelicsSynced(long playerId, IReadOnlyList<int> relicIds)
        {
            var names = new List<string>();
            foreach (int rid in relicIds) if (rid > 0) names.Add(Names.Relic(rid));
            if (names.Count == 0) return;
            string who = Nick(playerId);
            string self = Players.IsSelf(playerId) ? " (我)" : "";
            SdkLog.Write("ActivityLog", $"[遗物] {who}{self} 持有: " + string.Join(" | ", names));
        }

        private static void OnHandChanged(long playerId, IReadOnlyList<CardInfo> cards)
        {
            bool self = Players.IsSelf(playerId);
            if (self)
            {
                var names = new List<string>();
                foreach (var c in cards)
                    if (c != null && c.CardId > 0) names.Add(Names.Card(c.CardId));
                SdkLog.Write("ActivityLog", $"[手牌] 我的手牌: " + (names.Count > 0 ? string.Join(" | ", names) : "(空)"));
            }
            else
            {
                // 队友手牌被服务器掩码(负数), 只报数量
                bool allNegative = true;
                foreach (var c in cards)
                    if (c != null && c.CardId > 0) { allNegative = false; break; }
                if (!allNegative)
                {
                    var names = new List<string>();
                    foreach (var c in cards)
                        if (c != null && c.CardId > 0) names.Add(Names.Card(c.CardId));
                    SdkLog.Write("ActivityLog", $"[手牌] {Nick(playerId)} 的手牌: " + string.Join(" | ", names));
                }
                else
                {
                    SdkLog.Write("ActivityLog", $"[手牌] {Nick(playerId)} 手牌变化: {cards.Count}张 [服务器掩码]");
                }
            }
        }

        // ---------- 轮询(tick) ----------

        private static string _lastPanelName;
        private static string _lastRoomState;
        private static string _lastPlayers;
        private static string _lastSelfHero;
        private static string _lastChips;
        private static readonly List<int> _lastMyCards = new List<int>();

        private static void OnTick()
        {
            GameEvents.EnsureHooked(); // 游戏每场战斗重置回调, 每秒重新挂钩
            PollUiAndRoom();
            PollBattle();
        }

        private static void PollUiAndRoom()
        {
            try
            {
                // 界面
                var ui = SimpleSingletonProvider<UIManager>.inst;
                if (ui != null)
                {
                    var panel = ui.currentPanel;
                    string pn = panel != null ? UIPanelChinese(panel) : null;
                    if (pn != null && pn != _lastPanelName)
                    {
                        _lastPanelName = pn;
                        SdkLog.Write("ActivityLog", $"[界面] 打开: {pn}");
                    }
                }
                else if (_lastPanelName != null)
                {
                    _lastPanelName = null;
                }
            }
            catch { }

            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var room = gm?.room?.curRoomInfo;
                if (room != null)
                {
                    // 房间状态
                    string state = StateChinese(room.State);
                    string map = MapChinese(room.MapId);
                    string rs = $"{state} 地图: {map} 玩家数: {room.Players?.Count ?? 0}";
                    if (rs != _lastRoomState)
                    {
                        _lastRoomState = rs;
                        SdkLog.Write("ActivityLog", $"[房间] 状态: {rs}");
                    }
                    // 队友
                    string players = DumpPlayers(room);
                    if (players != null && players != _lastPlayers)
                    {
                        _lastPlayers = players;
                        SdkLog.Write("ActivityLog", $"[队友] {players}");
                    }
                    // 自己选角
                    string hero = SelfHeroName();
                    if (hero != null && hero != _lastSelfHero)
                    {
                        _lastSelfHero = hero;
                        SdkLog.Write("ActivityLog", $"[角色] 我选择了: {hero}");
                    }
                }
                else if (_lastRoomState != null)
                {
                    _lastRoomState = null;
                    _lastPlayers = null;
                    _lastSelfHero = null;
                }
            }
            catch { }
        }

        private static void PollBattle()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var battle = gm?.battle;
                if (battle?.PlayerDatas == null || battle.PlayerDatas.Count == 0) return;

                // 星币
                var chips = new List<string>();
                foreach (var pd in battle.PlayerDatas)
                {
                    if (pd?.player == null || pd.player.Id <= 0) continue;
                    string nick = Players.SafeNick(pd);
                    if (nick == null) continue;
                    int gold = 0;
                    try { gold = pd.Property?.gold?.Value ?? 0; } catch { }
                    chips.Add($"{nick}:{gold}");
                }
                string chipKey = string.Join(" ", chips);
                if (chipKey != _lastChips)
                {
                    _lastChips = chipKey;
                    SdkLog.Write("ActivityLog", "[星币] " + chipKey);
                }

                // 自己摸牌(手牌内容变化)
                var myCards = new List<int>();
                foreach (var hc in Players.MyHandCards())
                    if (hc != null && hc.CardId > 0) myCards.Add(hc.CardId);
                if (myCards.Count != _lastMyCards.Count)
                {
                    if (myCards.Count > _lastMyCards.Count && _lastMyCards.Count > 0)
                    {
                        // 摸到新牌: 找出新增的
                        foreach (int cid in myCards)
                            if (!_lastMyCards.Contains(cid))
                                SdkLog.Write("ActivityLog", $"[摸牌] 我摸到了: {Names.Card(cid)}");
                    }
                    _lastMyCards.Clear();
                    _lastMyCards.AddRange(myCards);
                }
                else
                {
                    // 数量没变但内容变了(替换)
                    bool changed = false;
                    for (int i = 0; i < myCards.Count; i++)
                        if (myCards[i] != _lastMyCards[i]) { changed = true; break; }
                    if (changed)
                    {
                        _lastMyCards.Clear();
                        _lastMyCards.AddRange(myCards);
                    }
                }
            }
            catch { }
        }

        // ---------- 辅助 ----------

        private static string Nick(long playerId)
        {
            string n = Players.Nick(playerId);
            return string.IsNullOrEmpty(n) ? "P" + playerId : n;
        }

        private static string SelfHeroName()
        {
            try
            {
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                var me = gm?.room?.curRoomInfo?.GetSelfInfo();
                if (me?.characterConfig != null)
                    return CharacterHandle.GetCharacterName(me.characterConfig.Id, me.characterConfig.CharacterType);
                if (me?.serverPlayer?.Hero != null)
                    return CharacterHandle.GetCharacterName(me.serverPlayer.Hero.HeroId);
            }
            catch { }
            return null;
        }

        private static string DumpPlayers(RoomInfo room)
        {
            try
            {
                if (room.Players == null || room.Players.Count == 0) return null;
                var parts = new List<string>();
                foreach (var p in room.Players)
                {
                    if (p == null) continue;
                    string nick;
                    try { nick = p.GetNick(); } catch { nick = "?"; }
                    string hero = HeroName(p);
                    parts.Add($"槽{p.NodeId}:{nick}{(p.IsBot ? "[AI]" : "")}={hero}");
                }
                return string.Join(" | ", parts);
            }
            catch { return null; }
        }

        private static string HeroName(RoomPlayer p)
        {
            try
            {
                if (p.characterConfig != null)
                    return CharacterHandle.GetCharacterName(p.characterConfig.Id, p.characterConfig.CharacterType);
                if (p.serverPlayer?.Hero != null)
                    return CharacterHandle.GetCharacterName(p.serverPlayer.Hero.HeroId);
            }
            catch { }
            return "未选";
        }

        private static string UIPanelChinese(object panel)
        {
            if (panel == null) return null;
            string n = panel.ToString();
            switch (n)
            {
                case "HandCard": return "手牌";
                case "LandEvent": return "地格事件";
                case "Shop": return "商店";
                case "Relic": return "筹码格";
                case "Fight": return "战斗";
                case "ChooseRoundCard": return "回合选卡";
                case "BottomMenu": return "主界面";
                case "RoomList": return "比赛入口";
                case "Room": return "房间";
                case "Match": return "匹配";
                case "Hero": return "角色";
                case "HeroSkin": return "皮肤";
                case "Setting": return "设置";
                default: return n;
            }
        }

        private static string StateChinese(Room.Types.State s)
        {
            switch (s)
            {
                case Room.Types.State.Wait: return "等待中";
                case Room.Types.State.ChoiceHero: return "选择角色";
                case Room.Types.State.ChoiceSkin: return "选择皮肤";
                case Room.Types.State.Ready1: return "准备中";
                case Room.Types.State.Running: return "游戏中";
                default: return s.ToString();
            }
        }

        private static string MapChinese(int mapId)
        {
            switch (mapId)
            {
                case 1: return "新手村";
                case 2: return "荒岛";
                case 3: return "冰岛";
                case 4: return "西部小镇";
                case 5: return "鬼屋";
                case 6: return "机械城";
                case 7: return "糖果岛";
                case 8: return "武侠岛";
                case 9: return "模式9";
                default: return "地图" + mapId;
            }
        }
    }
}
