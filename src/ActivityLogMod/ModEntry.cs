using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK;
using GameLogic;
using party.model;
using Tools;
using UI;

namespace ActivityLogMod
{
    /// <summary>
    /// 实时行为日志 mod —— CesiumLoader SDK 规范示例。
    ///
    /// 作用: 订阅游戏事件 + 每秒轮询, 把对局内的行为实时输出到加载器控制台。
    ///
    /// 本文件演示的 SDK 规范:
    ///   1. 入口: ModEntry.Main() —— 导出 manifest sidecar → 加载配置 → ModBase.Run
    ///   2. 元数据: [ModManifest] 特性(名称/版本/作者/描述)
    ///   3. 配置: SdkConfig.Load/Save 读写 configs/ActivityLogMod.json(公开字段, 支持热重载)
    ///   4. 日志分级: SdkLog.Info/Warn/Error/Debug(环境变量 CESIUM_LOG_LEVEL=Debug 可看调试日志)
    ///   5. 事件: GameEvents.* 订阅 + StartAutoHook()(SDK 内部维持挂钩, 事件驱动)
    ///   6. 轮询: 仅 UI/房间/战斗状态用 ModBase.Run 的 tick(各块独立 try/catch)
    ///   7. 操作查询: GameActions.CanThrowDice 检测"我的回合"(展示操作层 API)
    ///
    /// 配置(configs/ActivityLogMod.json, 全部可选):
    ///   Enabled            = true  总开关(false 时 mod 直接不启动)
    ///   LogUi              = true  界面切换 / 房间状态 / 队友 / 选角(轮询)
    ///   LogBattle          = true  星币 / 摸牌 / 手牌(轮询)
    ///   LogEvents          = true  用牌 / 骰子 / 移动 / 战斗 / 商店 / 遗物(事件)
    ///   LogTurn            = true  轮到我的回合提示(基于 GameActions.CanThrowDice)
    ///   ReloadConfigOnTick = false 每次 tick 重读配置(改配置即时生效, 不用重启游戏)
    /// </summary>
    // 元数据已移到 AssemblyInfo.cs(程序集级声明, 权威位置)
    public static class ModEntry
    {
        private static ActivityLogConfig _cfg = new ActivityLogConfig();

        // ============================== 入口 ==============================

        public static void Main()
        {
            try
            {
                MainSafe();
            }
            catch (Exception ex)
            {
                // 顶层兜底: 完整堆栈写 mod-errors.log(SDK 故障报告), 并重新抛出让加载器看到
                SdkLog.ReportCrash("ActivityLogMod", "ModEntry.Main 顶层异常", ex);
                throw;
            }
        }

        private static void MainSafe()
        {
            SdkLog.Info("ActivityLog", "=== 实时行为日志 v2.1.6 启动 ===");
            SdkManifest.ExportSidecar(); // [ModManifest] → 同名 .json, 供 apt 模组列表读取

            _cfg = SdkConfig.Load<ActivityLogConfig>("ActivityLogMod");
            if (!_cfg.Enabled)
            {
                SdkLog.Warn("ActivityLog", "配置 Enabled=false, mod 已停用(改 configs/ActivityLogMod.json 后重进游戏生效)");
                return;
            }
            SdkLog.Info("ActivityLog",
                $"配置已加载: LogUi={_cfg.LogUi}, LogBattle={_cfg.LogBattle}, LogEvents={_cfg.LogEvents}, LogTurn={_cfg.LogTurn}, 热重载={_cfg.ReloadConfigOnTick}");

            ModBase.Run(init: OnInit, tick: OnTick, tag: "ActivityLog");
        }

        private static void OnInit()
        {
            SdkLog.Info("ActivityLog", "初始化: 订阅游戏事件");
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
            // 事件驱动: SDK 内部自动维持 RPC 挂钩(每 1 秒检查战斗回调), mod 无需轮询
            GameEvents.StartAutoHook();
            SdkLog.Info("ActivityLog", $"事件订阅完成({GameEvents.EventCount} 个事件), 自动挂钩已启动");
        }

        // ============================== 事件处理 ==============================

        private static void OnCardUsed(long playerId, int cardId, int remain)
        {
            if (!_cfg.LogEvents) return;
            SdkLog.Info("ActivityLog", $"[用牌] {Nick(playerId)}{Self(playerId)} 使用了 {Names.Card(cardId)} (剩{remain}张)");
        }

        private static void OnNoCard(long playerId)
        {
            if (!_cfg.LogEvents) return;
            SdkLog.Info("ActivityLog", $"[用牌] {Nick(playerId)} 无牌可出, 跳过");
        }

        private static void OnEffectCardUsed(long playerId, int cardId, int remain)
        {
            if (!_cfg.LogEvents) return;
            SdkLog.Info("ActivityLog", $"[效果牌] {Nick(playerId)}{Self(playerId)} 使用了 {Names.Card(cardId)} (剩{remain}张)");
        }

        private static void OnSkillUsed(long playerId, int skillId)
        {
            if (!_cfg.LogEvents) return;
            SdkLog.Info("ActivityLog", $"[技能] {Nick(playerId)}{Self(playerId)} 释放了 {Names.Skill(skillId)}");
        }

        private static void OnQuickCardUsed(long playerId, int cardId, int originalCardId)
        {
            if (!_cfg.LogEvents) return;
            string orig = originalCardId != 0 ? $" (跟{Names.Card(originalCardId)})" : "";
            SdkLog.Info("ActivityLog", $"[跟牌] {Nick(playerId)}{Self(playerId)} 使用了 {Names.Card(cardId)}{orig}");
        }

        private static void OnDiceResult(long playerId, int point, int maxPoint)
        {
            if (!_cfg.LogEvents) return;
            string bonus = maxPoint > 0 && maxPoint != point ? $" (上限{maxPoint})" : "";
            SdkLog.Info("ActivityLog", $"[骰子] {Nick(playerId)}{Self(playerId)} 掷出 {point} 点{bonus}");
        }

        private static void OnMove(long playerId, int stepCount, bool end)
        {
            if (!_cfg.LogEvents) return;
            SdkLog.Info("ActivityLog", $"[移动] {Nick(playerId)}{Self(playerId)} 移动 {stepCount} 格{(end ? " (到达)" : "")}");
        }

        private static void OnBattleUpdate(Battle b)
        {
            if (!_cfg.LogEvents) return;
            if (b.Attacker == null || b.Defender == null) return;
            string end = b.IsEnd ? " [已结束]" : "";
            SdkLog.Info("ActivityLog",
                $"[战斗] {Names.BattleRole(b.Attacker)}{Self(b.Attacker.PlayerId)} vs {Names.BattleRole(b.Defender)} " +
                $"攻{b.Attacker.Point}(ATK{b.Attacker.Atk}) vs 防{b.Defender.Point}(DEF{b.Defender.Def}){end}");
        }

        private static void OnBattleDice(long playerId, int val)
        {
            if (!_cfg.LogEvents) return;
            SdkLog.Info("ActivityLog", $"[战斗骰] {Nick(playerId)}{Self(playerId)} 掷出攻击 {val} 点");
        }

        private static void OnRewardCardSelected(long playerId, int cardId)
        {
            if (!_cfg.LogEvents) return;
            SdkLog.Info("ActivityLog", $"[选卡] {Nick(playerId)}{Self(playerId)} 选择了奖励卡 {Names.Card(cardId)}");
        }

        private static void OnShopCandidates(long playerId, IReadOnlyList<int> cardIds)
        {
            if (!_cfg.LogEvents) return;
            var names = CollectNames(cardIds, Names.Card);
            if (names.Count == 0) return;
            SdkLog.Info("ActivityLog", $"[商店] {Nick(playerId)}{Self(playerId)} 待选卡: {string.Join(" | ", names)}");
        }

        private static void OnRelicCandidates(long playerId, IReadOnlyList<int> relicIds)
        {
            if (!_cfg.LogEvents) return;
            var names = CollectNames(relicIds, Names.Relic);
            if (names.Count == 0) return;
            SdkLog.Info("ActivityLog", $"[筹码] {Nick(playerId)}{Self(playerId)} 候选遗物: {string.Join(" | ", names)}");
        }

        private static void OnRelicSelected(long playerId, int relicId)
        {
            if (!_cfg.LogEvents) return;
            SdkLog.Info("ActivityLog", $"[筹码] {Nick(playerId)}{Self(playerId)} 选择了 {Names.Relic(relicId)}");
        }

        private static void OnRelicsSynced(long playerId, IReadOnlyList<int> relicIds)
        {
            if (!_cfg.LogEvents) return;
            var names = CollectNames(relicIds, Names.Relic);
            if (names.Count == 0) return;
            SdkLog.Info("ActivityLog", $"[遗物] {Nick(playerId)}{Self(playerId)} 持有: {string.Join(" | ", names)}");
        }

        private static void OnHandChanged(long playerId, IReadOnlyList<CardInfo> cards)
        {
            if (!_cfg.LogEvents) return;
            bool self = Players.IsSelf(playerId);
            if (self)
            {
                var names = CollectNames(cards, c => Names.Card(c.CardId));
                SdkLog.Info("ActivityLog", $"[手牌] 我的手牌: {(names.Count > 0 ? string.Join(" | ", names) : "(空)")}");
                return;
            }
            // 队友手牌被服务器掩码(负数): 有真实卡才列名字, 否则只报数量
            var visible = CollectNames(cards, c => Names.Card(c.CardId));
            SdkLog.Info("ActivityLog",
                visible.Count > 0
                    ? $"[手牌] {Nick(playerId)} 的手牌: {string.Join(" | ", visible)}"
                    : $"[手牌] {Nick(playerId)} 手牌变化: {cards.Count} 张 [服务器掩码]");
        }

        // ============================== 轮询(tick) ==============================

        private static bool _wasMyTurn;

        private static void OnTick()
        {
            if (_cfg.ReloadConfigOnTick)
            {
                var fresh = SdkConfig.Load<ActivityLogConfig>("ActivityLogMod");
                if (fresh != null) _cfg = fresh;
            }

            // 事件挂钩由 GameEvents.StartAutoHook() 内部维持, 这里只做周期状态轮询
            if (_cfg.LogTurn) PollTurn();
            if (_cfg.LogUi) PollUiAndRoom();
            if (_cfg.LogBattle) PollBattle();
        }

        /// <summary>轮到我的回合提示(基于 GameActions.CanThrowDice, 展示操作层查询 API)。</summary>
        private static void PollTurn()
        {
            try
            {
                bool myTurn = GameActions.CanThrowDice;
                if (myTurn && !_wasMyTurn)
                    SdkLog.Info("ActivityLog", "[回合] ★ 轮到我了, 可以行动");
                else if (!myTurn && _wasMyTurn)
                    SdkLog.Info("ActivityLog", "[回合] 我的行动结束");
                _wasMyTurn = myTurn;
            }
            catch { }
        }

        private static string _lastPanelName;
        private static string _lastRoomState;
        private static string _lastPlayers;
        private static string _lastSelfHero;
        private static string _lastChips;
        private static readonly List<int> _lastMyCards = new List<int>();

        private static void PollUiAndRoom()
        {
            try
            {
                var ui = SimpleSingletonProvider<UIManager>.inst;
                if (ui != null)
                {
                    var panel = ui.currentPanel;
                    string pn = panel != null ? UIPanelChinese(panel) : null;
                    if (pn != null && pn != _lastPanelName)
                    {
                        _lastPanelName = pn;
                        SdkLog.Info("ActivityLog", $"[界面] 打开: {pn}");
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
                    string state = StateChinese(room.State);
                    string map = MapChinese(room.MapId);
                    string rs = $"{state} 地图: {map} 玩家数: {room.Players?.Count ?? 0}";
                    if (rs != _lastRoomState)
                    {
                        _lastRoomState = rs;
                        SdkLog.Info("ActivityLog", $"[房间] {rs}");
                    }

                    string players = DumpPlayers(room);
                    if (players != null && players != _lastPlayers)
                    {
                        _lastPlayers = players;
                        SdkLog.Info("ActivityLog", $"[队友] {players}");
                    }

                    string hero = SelfHeroName();
                    if (hero != null && hero != _lastSelfHero)
                    {
                        _lastSelfHero = hero;
                        SdkLog.Info("ActivityLog", $"[角色] 我选择了: {hero}");
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
                    SdkLog.Info("ActivityLog", $"[星币] {chipKey}");
                }

                // 自己摸牌 / 手牌内容变化
                var myCards = new List<int>();
                foreach (var hc in Players.MyHandCards())
                    if (hc != null && hc.CardId > 0) myCards.Add(hc.CardId);

                if (myCards.Count != _lastMyCards.Count)
                {
                    if (myCards.Count > _lastMyCards.Count && _lastMyCards.Count > 0)
                    {
                        foreach (int cid in myCards)
                            if (!_lastMyCards.Contains(cid))
                                SdkLog.Info("ActivityLog", $"[摸牌] 我摸到了 {Names.Card(cid)}");
                    }
                    else if (myCards.Count < _lastMyCards.Count && _lastMyCards.Count > 0)
                    {
                        foreach (int cid in _lastMyCards)
                            if (!myCards.Contains(cid))
                                SdkLog.Info("ActivityLog", $"[手牌] 我打出了 {Names.Card(cid)}");
                    }
                    _lastMyCards.Clear();
                    _lastMyCards.AddRange(myCards);
                }
                else
                {
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

        // ============================== 辅助 ==============================

        private static string Self(long playerId) => Players.IsSelf(playerId) ? " (我)" : "";

        private static string Nick(long playerId)
        {
            string n = Players.Nick(playerId);
            return string.IsNullOrEmpty(n) ? "P" + playerId : n;
        }

        private static List<string> CollectNames(IReadOnlyList<int> ids, Func<int, string> resolve)
        {
            var names = new List<string>();
            foreach (int id in ids)
                if (id > 0) names.Add(resolve(id));
            return names;
        }

        private static List<string> CollectNames(IReadOnlyList<CardInfo> cards, Func<CardInfo, string> resolve)
        {
            var names = new List<string>();
            foreach (var c in cards)
                if (c != null && c.CardId > 0) names.Add(resolve(c));
            return names;
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
                    parts.Add($"槽{p.NodeId}:{nick}{(p.IsBot ? "[AI]" : "")}={HeroName(p)}");
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

    /// <summary>实时行为日志 mod 配置(公开字段, SdkConfig 读写; 热重载开启后改配置即时生效)。</summary>
    public class ActivityLogConfig
    {
        public bool Enabled = true;
        public bool LogUi = true;
        public bool LogBattle = true;
        public bool LogEvents = true;
        public bool LogTurn = true;
        public bool ReloadConfigOnTick = false;
    }
}
