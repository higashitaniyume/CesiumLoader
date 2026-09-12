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
                var net = Net;
                var logic = Logic;
                if (net?.RPC == null || logic?.action == null) return false;
                sn = overrideSn ?? logic.action.throwDiceSn;
                if (sn <= 0) return false;
                logic.battle?.RecordFinishSn(sn);
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

        /// <summary>战斗中使用卡牌(cardId = 手牌里卡牌的 CardId)。返回是否成功发出。</summary>
        public static bool UseCard(int cardId, long? sn = null)
        {
            try
            {
                if (!Ready(out long targetSn, sn)) return false;
                Net!.RPC.BattleUseCardC2S.BattleUseCardC2SCall(new BattleUseCardC2S
                {
                    Info = MakeInfo(targetSn),
                    CardUid = cardId
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
    }
}
