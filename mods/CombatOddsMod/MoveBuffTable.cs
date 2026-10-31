using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>玩家身上一个仍生效的 Buff(配置 id + 当前层数)。</summary>
    internal struct ActiveBuff
    {
        public int BuffId;
        public int Layers;
    }

    /// <summary>
    /// 会改变移速的 Buff 数值表, 用于在投骰前把"所有已知来源"加进来。
    ///
    /// 数值来自当前国服配表(2026-10-30)：筹码 Buff 取 Relic.bin 的 Params/说明；技能与道具 Buff
    /// 取 STRBuff 文本里明确写出的 `移动速度±N`。少数需要层数或其他 Buff 才能算的, 用
    /// <see cref="SpecialValue"/> 单独处理(糖糖开大就是其中之一：移速 = 她的层数)。
    ///
    /// 仍然只算"客户端看得到"的部分：随机效果(±2 浮动)、按累计值动态变化的被动、以及未来
    /// 版本新增的 Buff, 要么算不出, 要么需要在这里补一行。补不上的部分会由服务器的
    /// <c>MovePointBuffS2C</c> 在移动时校正。
    ///
    /// 纯逻辑(零游戏依赖), 由 CombatOddsMod.Tests 直接编译测试。
    /// </summary>
    internal static class MoveBuffTable
    {
        /// <summary>筹码提供的常驻移速 Buff。</summary>
        internal const int SkateBasicBuffId = 5000401;
        internal const int SkateMidBuffId = 5000501;
        internal const int SkateHighBuffId = 5000601;
        internal const int DreamShipBuffId = 5005401;
        internal const int PerpetualBuffId = 5009001;

        /// <summary>糖糖(302 主播女孩)开大附加的移速 Buff：数值等于她【饮料/层数】Buff 的层数。</summary>
        internal const int SugarMoveBuffId = 3020101;
        internal const int SugarLayerBuffId = 3021101;

        /// <summary>思维飞艇：9 层时 +2 移速。</summary>
        internal const int LayerConditionalBuffId = 10651101;
        /// <summary>能量回收：层数大于 5 时 +1 移速。</summary>
        internal const int EnergyRecycleBuffId = 5008401;
        /// <summary>鸳鸯玉佩：拥有【鸳鸯连理】时 +2 移速。</summary>
        internal const int JadeTokenBuffId = 5006801;
        internal const int JadePairBuffId = 10470101;

        /// <summary>固定数值的移速 Buff。</summary>
        private static readonly Dictionary<int, int> Flat = new Dictionary<int, int>
        {
            // 筹码
            { SkateBasicBuffId, 1 },
            { SkateMidBuffId, 2 },
            { SkateHighBuffId, 4 },
            { DreamShipBuffId, 2 },
            { PerpetualBuffId, 2 },

            // Buff 文本里写明数值的加减速
            { 1090101, -2 },   // 标记/减速类
            { 1120101, -3 },
            { 1120301, -3 },
            { 1261203, -9 },
            { 1281102, 2 },    // 下次移动速度+2
            { 1290101, -2 },   // 下次移动速度-2
            { 1290201, -2 },
            { 3031102, 3 },    // 改变生活
            { 3050101, 2 },
            { 3051203, 2 },    // 身轻体快
            { 3051302, 2 },    // 挚友祝福
            { 3081106, 2 },
            { 6015102, -2 },
            { 6015702, 2 },
            { 7000101, 2 },
            { 10430101, 2 },   // 疾驰
            { 10671302, 2 },   // 真凶
            { 10711103, -1 },
            { 2002501, -4 },
            { 2101501, 2 },
            { 5007102, 2 },
            { 5008802, 2 },    // 跃迁引擎的"下次移动+2"
            { 201102, 2 },
            { 210102, 2 },
            { 10031101, 1 },   // 角色被动：自由选择移动方向，移速+1
            { 1170101, 3 },    // 主动技能后：防御-3、移速+3，持续 1 轮
            { 1170102, 3 },
        };

        internal static bool IsSpecial(int buffId)
        {
            return buffId == SugarMoveBuffId
                || buffId == LayerConditionalBuffId
                || buffId == EnergyRecycleBuffId
                || buffId == JadeTokenBuffId;
        }

        internal static bool TryGetFlat(int buffId, out int value)
        {
            return Flat.TryGetValue(buffId, out value);
        }

        /// <summary>需要层数或其他 Buff 才能算的移速。</summary>
        internal static int SpecialValue(int buffId, int ownLayers, IList<ActiveBuff> buffs)
        {
            switch (buffId)
            {
                case SugarMoveBuffId:
                    return LayersOf(buffs, SugarLayerBuffId);
                case LayerConditionalBuffId:
                    return ownLayers >= 9 ? 2 : 0;
                case EnergyRecycleBuffId:
                    return ownLayers > 5 ? 1 : 0;
                case JadeTokenBuffId:
                    return LayersOf(buffs, JadePairBuffId) > 0 ? 2 : 0;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// 把身上所有已知移速 Buff 加起来。服务器教过的数值(<paramref name="learned"/>)优先,
        /// 其次查表, 最后才是需要上下文的特殊规则。
        /// </summary>
        internal static int Sum(IList<ActiveBuff> buffs, IDictionary<int, int> learned)
        {
            if (buffs == null || buffs.Count == 0) return 0;

            int sum = 0;
            for (int i = 0; i < buffs.Count; i++)
            {
                int buffId = buffs[i].BuffId;

                if (IsSpecial(buffId))
                {
                    sum += SpecialValue(buffId, buffs[i].Layers, buffs);
                    continue;
                }

                int value;
                if (learned != null && learned.TryGetValue(buffId, out value))
                {
                    sum += value;
                    continue;
                }

                if (TryGetFlat(buffId, out value)) sum += value;
            }
            return sum;
        }

        private static int LayersOf(IList<ActiveBuff> buffs, int buffId)
        {
            if (buffs == null) return 0;
            for (int i = 0; i < buffs.Count; i++)
            {
                if (buffs[i].BuffId == buffId)
                {
                    int layers = buffs[i].Layers;
                    return layers > 0 ? layers : 0;
                }
            }
            return 0;
        }
    }
}
