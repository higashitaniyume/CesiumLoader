using System;
using System.Collections.Generic;
using AstralParty.Agent;

namespace AstralParty.AgentMod.Bridge
{
    internal sealed partial class PendingTracker
    {
        /// <summary>取当前窗口的候选 id(仅当窗口类型匹配)。agent 只报 index 时, mod 需要拿回完整候选再发请求。</summary>
        public bool TryGetWindowIds(string kind, out int[] ids)
        {
            ids = null;
            lock (_lock)
            {
                if (_window == null || _window.Kind != kind) return false;
                if (_window.Ids == null || _window.Ids.Length == 0) return false;
                ids = (int[])_window.Ids.Clone();
                return true;
            }
        }

        /// <summary>5039 闪避窗口: 取"这一击能不能闪避"。当前没有该窗口时返回 false(调用方应自己判窗口存在)。</summary>
        public bool TryGetNoDodge(out bool noDodge)
        {
            noDodge = false;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.FightChoice) return false;
                noDodge = _window.NoDodge;
                return true;
            }
        }

        /// <summary>5213 追击窗口: 取本地算出的候选怪物 playerId。用于"agent 只能追候选里的怪"这条校验。</summary>
        public bool TryGetMonsterIds(out long[] ids)
        {
            ids = null;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.PursueMonster) return false;
                if (_window.MonsterIds == null) return false;
                ids = (long[])_window.MonsterIds.Clone();
                return true;
            }
        }

        /// <summary>5067 控制移动卡: 取可选点数上限。没有该窗口时返回 false。</summary>
        public bool TryGetMaxPoint(out int maxPoint)
        {
            maxPoint = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.SelectPoint) return false;
                maxPoint = _window.MaxPoint;
                return true;
            }
        }

        /// <summary>5323 商人买卡: 取价格(星币)。没有该窗口时返回 false。</summary>
        public bool TryGetVendorPrice(out int price)
        {
            price = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.VendorCard) return false;
                price = _window.VendorPrice;
                return true;
            }
        }

        /// <summary>5063 炮台选目标: 取"最多能选几个 + 候选英雄"。用于"agent 只能选候选里的英雄、数量不超上限"的校验。</summary>
        public bool TryGetBatteryTargets(out long[] ids, out int targetNum)
        {
            ids = null;
            targetNum = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.BatteryTarget) return false;
                targetNum = _window.TargetNum;
                ids = _window.TargetIds == null ? null : (long[])_window.TargetIds.Clone();
                return true;
            }
        }

        /// <summary>5081 赌场押注: 取 offer 的 IsExec(要原样回传) / 按钮可不可点 / 本注星币。</summary>
        public bool TryGetGambleGuess(out bool isExec, out bool canAct, out int betGold)
        {
            isExec = false;
            canAct = false;
            betGold = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.GambleGuess) return false;
                isExec = _window.EnableJoin;
                canAct = _window.CanAct;
                betGold = _window.BetGold;
                return true;
            }
        }

        /// <summary>5083 赌场掷骰: 取"按钮可不可点"。没有该窗口时返回 false。</summary>
        public bool TryGetGambleDice(out bool canAct)
        {
            canAct = false;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.GambleDice) return false;
                canAct = _window.CanAct;
                return true;
            }
        }

        /// <summary>5041 抽奖: 取"可选号码 + 要选几个"。用于"号码必须在候选里、个数必须等于 Num"的校验。</summary>
        public bool TryGetLottery(out int[] candidates, out int chooseNum)
        {
            candidates = null;
            chooseNum = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.LotteryPick) return false;
                chooseNum = _window.TargetNum;
                candidates = _window.Ids == null ? null : (int[])_window.Ids.Clone();
                return true;
            }
        }

        /// <summary>5033 追击地块: 取候选敌方英雄 playerId。用于"agent 只能追候选里的人"这条校验。</summary>
        public bool TryGetPursuePlayers(out long[] playerIds)
        {
            playerIds = null;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.PursuePlayer) return false;
                if (_window.TargetIds == null) return false;
                playerIds = (long[])_window.TargetIds.Clone();
                return true;
            }
        }

        /// <summary>
        /// 5309 助力投票: 取三个槽位(下标 0=右, 1=左, 2=中; <c>0</c> = 本赛季/本图没有这一路)。
        /// 用槽位而不是"只给非空", 是因为两张地图的票数不同(82013 两路, 82015 三路),
        /// agent 需要知道"左/右/中"分别对应哪个 id。
        /// </summary>
        public bool TryGetAssistVote(out int[] slots)
        {
            slots = null;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.AssistVote) return false;
                if (_window.Ids == null || _window.Ids.Length < 3) return false;
                slots = (int[])_window.Ids.Clone();
                return true;
            }
        }

        /// <summary>取当前窗口的 sn(仅当窗口类型匹配)。各项操作的 Info.Sn 必须用对应窗口的 sn。</summary>
        public bool TryGetWindowSn(string kind, out long sn)
        {
            sn = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != kind || _window.Sn == 0) return false;
                sn = _window.Sn;
                return true;
            }
        }

        /// <summary>
        /// 取当前商店窗口的类型(决定发 ShopBuyC2S 还是 PVEShopBuyC2S: 两者消息类不同)。
        /// </summary>
        public bool TryGetShopWindow(out bool pveShop, out int slotCount)
        {
            pveShop = false;
            slotCount = 0;
            lock (_lock)
            {
                if (_window == null || _window.Kind != AgentPendingKind.Shop) return false;
                pveShop = _window.PveShop;
                slotCount = _window.Ids == null ? 0 : _window.Ids.Length;
                return true;
            }
        }

    }
}
