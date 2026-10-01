using System;
using System.Collections.Generic;
using Core;
using Core.Net;
using GameLogic;
using party.model;
using party.protocol;
using Tools;

namespace CesiumLoader.SDK
{
    /// <summary>
    /// 游戏操作能力: 让 mod 能像玩家一样向服务器发送 C2S 指令(投骰子/移动/用牌等)。
    /// 内部复刻游戏 UI 的真实发送路径(MonoSingletonProvider&lt;NetManager&gt;.inst.RPC.xxxCall),
    /// 与玩家手动操作走同一条链路, 服务器按正常逻辑处理。
    ///
    /// 安全: 所有方法带空保护, 不在战斗/房间时静默失败(返回 false), 绝不抛异常。
    /// 注意: 操作会真实影响对局, 请只在你的 mod 确实需要时调用。
    /// </summary>
    public static class GameActions
    {
        // ============================== 基础 ==============================

        private static NetManager Net => MonoSingletonProvider<NetManager>.inst;

        private static GameLogicManager Logic => SimpleSingletonProvider<GameLogicManager>.inst;

        /// <summary>当前待响应的操作序列号(服务器下发的最近一个 Action 的 Sn)。不在回合/无操作时为 0。</summary>
        public static long CurrentSn
        {
            get
            {
                try { return Logic?.action?.throwDiceSn ?? 0; }
                catch { return 0; }
            }
        }

        /// <summary>是否能投骰子(轮到自己的行动回合)。</summary>
        public static bool CanThrowDice
        {
            get
            {
                try { return Logic?.action != null && Logic.action.throwDiceSn > 0; }
                catch { return false; }
            }
        }

        private static ActionInfo MakeInfo(long sn)
        {
            return new ActionInfo
            {
                Sn = sn,
                UseTime = OperationTimer.GetExtraTime()
            };
        }

        private static bool Ready(out long sn, long? overrideSn)
        {
            sn = 0;
            try
            {
                // 权限门控: 向服务器发送操作是敏感能力, 默认关闭。
                // mod 需在 [ModManifest(Permissions=ModPermission.GameActions)] 声明,
                // 或用 mods\{name}.permissions.json 显式授予, 否则所有操作静默失败。
                if (!Permissions.Require(ModPermission.GameActions, "GameActions"))
                    return false;

                var net = Net;
                var logic = Logic;
                if (net?.RPC == null || logic?.action == null) return false;
                sn = overrideSn ?? logic.action.throwDiceSn;
                if (sn <= 0) return false;
                logic.battle?.RecordFinishSn(sn);
                // 与游戏自己的 Request*C2S 一致: 取消这个 sn 的操作倒计时。
                // 不取消的话, 客户端挂着的超时回调到点会**再发一条结论相反**的请求
                // (5047 超时→不打, 5035 超时→不出牌, 5039 超时→不闪避), 覆盖我们刚发出的决定。
                try { OperationTimer.CancelOperatTimer(sn); } catch { }
                return true;
            }
            catch { return false; }
        }

        // ============================== 投骰子 ==============================

        /// <summary>投骰子(普通回合)。isNoOper=true 表示无牌可出直接跳过, isMoveNow=true 表示投完立即移动。返回是否成功发出。</summary>
        public static bool ThrowDice(bool isNoOper = false, bool isMoveNow = false, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                Net!.RPC.ThrowDiceC2S.ThrowDiceC2SCall(new ThrowDiceC2S
                {
                    Info = MakeInfo(targetSn),
                    DevPoint = GMConfig.dev_MovePoint,
                    IsNoOper = isNoOper,
                    IsMoveNow = isMoveNow
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>战斗攻击骰子(战斗中的攻击判定)。返回是否成功发出。</summary>
        public static bool BattleThrowDice(long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                Net!.RPC.BattleThrowDiceC2S.BattleThrowDiceC2SCall(new BattleThrowDiceC2S
                {
                    Info = MakeInfo(targetSn),
                    DevPoint = GMConfig.dev_AttackerPoint
                });
                return true;
            }
            catch { return false; }
        }

        // ============================== 战斗询问 / 闪避 ==============================

        /// <summary>
        /// 应答"要不要打这一场"(服务器动作 5047 = <c>FightLogic.AskFight</c>):
        /// <paramref name="isBattle"/>=true 接受进入战斗, false 不接。返回是否成功发出。
        /// </summary>
        /// <remarks>
        /// 复刻 <c>FightLogic.RequestAskBattleC2S</c>。**注意不要在这种窗口里乱抢答**:
        /// 反编译 <c>AskFight</c> 证实, 当服务器下发的 <c>AskBattleC2S.FightBack == true</c> 时,
        /// 客户端自己会立刻以 <c>IsBattle=true</c> 自动应答(真人玩家根本没有选择机会), 桥接不该再发一条。
        /// </remarks>
        public static bool AskBattle(bool isBattle, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                Net!.RPC.AskBattleC2S.AskBattleC2SCall(new AskBattleC2S
                {
                    Info = MakeInfo(targetSn),
                    IsBattle = isBattle
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 战斗内闪避选择(服务器动作 5039 = <c>FightLogic.ReadyFightChoice</c>):
        /// <paramref name="dodge"/>=true 闪避, false 硬吃。返回是否成功发出。
        /// </summary>
        /// <remarks>复刻 <c>FightLogic.RequestBattleChoiceC2S</c>(<c>DevPoint</c> 用 <c>GMConfig.dev_DefenderPoint</c>)。</remarks>
        public static bool BattleChoice(bool dodge, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                Net!.RPC.BattleChoiceC2S.BattleChoiceC2SCall(new BattleChoiceC2S
                {
                    Info = MakeInfo(targetSn),
                    DevPoint = GMConfig.dev_DefenderPoint,
                    Dodge = dodge
                });
                return true;
            }
            catch { return false; }
        }

        // ============================== 移动 ==============================

        /// <summary>移动到目标地块(targetLandId = 目标地块 ID, 即方向箭头指向的格)。返回是否成功发出。</summary>
        public static bool Move(int targetLandId, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                Net!.RPC.MoveC2S.MoveC2SCall(new MoveC2S
                {
                    Info = MakeInfo(targetSn),
                    Direction = targetLandId
                });
                return true;
            }
            catch { return false; }
        }

        // ============================== 用牌 ==============================

        /// <summary>
        /// 战斗中使用卡牌(<paramref name="cardUid"/> = **手牌的唯一实例号 Guid**, 不是卡牌配置 CardId;
        /// 传 0 = 这一轮不出牌)。返回是否成功发出。
        /// </summary>
        /// <remarks>
        /// 依据(反编译 <c>UI.FightWindow.DragEndEvent</c>): 客户端出牌发的是
        /// <c>RequestBattleUseCardC2S(useBattleCardSn, item.CardData.Guid)</c>, 而
        /// <c>RequestBattleUseCardC2S</c> 把它填进 <c>BattleUseCardC2S.CardUid</c>;
        /// 超时/点"结束出牌"按钮时发的也是同一个字段, 值为 0。
        /// 反证: <c>GameEvents.cs</c> 里要把 S2C 的 <c>CardId</c> 用 <c>Players.ResolveCardGuid</c>
        /// 反查回真实 CardId —— 说明这条链路上的"CardId"字段装的其实是 Guid。
        /// </remarks>
        public static bool UseCard(int cardUid, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                Net!.RPC.BattleUseCardC2S.BattleUseCardC2SCall(new BattleUseCardC2S
                {
                    Info = MakeInfo(targetSn),
                    CardUid = cardUid
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>使用棋盘效果牌(cardId = 卡牌 CardId, targetIds = 目标玩家, landIds = 目标地块, chooseEffectIndex = 选择的效果项)。返回是否成功发出。</summary>
        public static bool UseEffectCard(int cardId, IEnumerable<long> targetIds = null, IEnumerable<int> landIds = null, int chooseEffectIndex = 0, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                var req = new UseEffectCardC2S
                {
                    Info = MakeInfo(targetSn),
                    CardId = cardId,
                    UseSelectCardIndex = chooseEffectIndex,
                    DevPoint = GMConfig.dev_MovePoint
                };
                if (targetIds != null) req.TargetIds.AddRange(targetIds);
                if (landIds != null) req.TargetNodeIds.AddRange(landIds);
                Net!.RPC.UseEffectCardC2S.UseEffectCardC2SCall(req);
                return true;
            }
            catch { return false; }
        }

        /// <summary>跟牌/快速卡(cardId = 快速卡 CardId, targetId = 被跟的玩家 id)。返回是否成功发出。</summary>
        public static bool UseQuickCard(int cardId, long targetId, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                Net!.RPC.UseQuickCardC2S.UseQuickCardC2SCall(new UseQuickCardC2S
                {
                    Info = MakeInfo(targetSn),
                    CardId = cardId,
                    TargetId = targetId
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>弃牌(cardIds = 要弃的卡牌 CardId 列表)。返回是否成功发出。</summary>
        public static bool AbandonCards(IEnumerable<int> cardIds, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                var req = new AbandonCardC2S { Info = MakeInfo(targetSn) };
                req.CardUniqueIds.AddRange(cardIds);
                Net!.RPC.AbandonCardC2S.AbandonCardC2SCall(req);
                return true;
            }
            catch { return false; }
        }

        /// <summary>弃一张牌(cardId)。返回是否成功发出。</summary>
        public static bool AbandonCard(int cardId, long? sn = null)
            => AbandonCards(new[] { cardId }, sn);

        // ============================== 选择/结算 ==============================

        /// <summary>选择奖励卡(cardIds = 服务器下发的候选列表, selectedIndex = 选中的下标)。返回是否成功发出。</summary>
        public static bool SelectRewardCard(IEnumerable<int> cardIds, int selectedIndex, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                var req = new SelectRewardCardC2S { Info = MakeInfo(targetSn) };
                req.CardIds.AddRange(cardIds);
                req.Idx = selectedIndex;
                Net!.RPC.SelectRewardCardC2S.SelectRewardCardC2SCall(req);
                return true;
            }
            catch { return false; }
        }

        /// <summary>选择遗物(筹码格, relicIds = 服务器下发的候选, selectedIndex = 选中的下标)。返回是否成功发出。</summary>
        public static bool SelectRelic(IEnumerable<int> relicIds, int selectedIndex = 0, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                var req = new SelectRelicC2S { Info = MakeInfo(targetSn) };
                req.Relics.AddRange(relicIds);
                req.Idx = selectedIndex;
                Net!.RPC.SelectRelicC2S.SelectRelicC2SCall(req);
                return true;
            }
            catch { return false; }
        }

        /// <summary>单张遗物便捷重载: 直接按遗物 id 选择。返回是否成功发出。</summary>
        public static bool SelectRelic(int relicId, long? sn = null)
            => SelectRelic(new[] { relicId }, 0, sn);

        /// <summary>重摇筹码候选(等价于客户端 RelicLogic.RequestResetRelic)。返回是否成功发出。</summary>
        /// <remarks>
        /// 与 <see cref="SelectRelic(IEnumerable{int}, int, long?)"/> 是**同一条消息**, 唯一区别是
        /// <c>IsReroll=true</c>。反编译 <c>RelicLogic.RequestResetRelic</c> 证实: 它只填 Info 与 IsReroll,
        /// **不填** <c>Relics</c>/<c>Idx</c>(保持默认值)。
        ///
        /// 语义注意: 重摇**不会**结束这个窗口 —— 服务器回 <c>SelectRelicS2C{IsReroll=true}</c> 后
        /// 会另推一组新的 5211 候选(新 Sn), 由那一组决定后续。所以别在重摇后把窗口当成已完成。
        /// </remarks>
        public static bool RerollRelic(long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                Net!.RPC.SelectRelicC2S.SelectRelicC2SCall(new SelectRelicC2S
                {
                    Info = MakeInfo(targetSn),
                    IsReroll = true
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 事件选择(棋盘事件弹窗, cmd 5317): <paramref name="events"/> = 服务器给的候选事件 id 列表(必须原样回传),
        /// <paramref name="selectedIndex"/> = 选中下标。返回是否成功发出。
        /// </summary>
        /// <remarks>
        /// 反编译 <c>UI.LandEventWindow.ShowSkill10202</c> 证实: 客户端是把**服务器那条消息原样**改两个字段后发回
        /// (<c>Idx</c>=选中下标, <c>Info.Sn</c>=action.Sn), <c>Events</c> 列表保持不变 —— 所以这里也必须把
        /// <paramref name="events"/> 带回去, 不能只发一个下标。
        /// 该窗口的超时回调会把 <c>Idx</c> 兜成 0(界面上 selectedIndex 是 -1), 即"不选就默认第一项"。
        /// </remarks>
        public static bool SelectEvent(IEnumerable<int> events, int selectedIndex, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                var req = new SelectEventC2S { Info = MakeInfo(targetSn), Idx = selectedIndex };
                if (events != null) req.Events.AddRange(events);
                Net!.RPC.SelectEventC2S.SelectEventC2SCall(req);
                return true;
            }
            catch { return false; }
        }
    }
}
