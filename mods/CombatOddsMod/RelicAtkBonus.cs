using System;
using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>
    /// 星币锤 / 手电筒 / 美工刀 的「攻击力加成」计算 —— <b>纯逻辑, 零游戏/SDK 依赖, 可完全离线单测</b>。
    ///
    /// 机制来源: 用游戏自己的 protobuf 解析器解码 Relic.bin + STRRelic.bin 逐条核对描述,
    /// 并用真机结算伤害对拍修正(客户端 DLL 里没有这些公式, 效果由服务器结算):
    ///   · 星币锤      50043 [橙·工资/星光] 获得5层星光; 攻击时若当前星币 &gt; 20, 额外 +当前星币30% 攻击力, 随后消耗6星币。
    ///   · 手电筒-强光 50013 [紫·工资/星光] 攻击怪物时每4层星光 → 自身攻击力+1; 击中怪物后获得1层星光。
    ///   · 美工刀-初级 50010 [紫·治愈]      生命值为满时攻击力+2, 且攻击时额外 +治愈层数 攻击力。
    ///   · 美工刀-锋利 50011 [橙·治愈]      同上, 但固定值为 +4。
    ///
    /// <b>关于美工刀只算治愈层数(重要, 真机对拍修正)</b>:
    ///   描述里的 "+2 / +4" 是美工刀<b>自身</b>的攻击力加成 —— 服务器已经把它算进 HUD 上显示的那个
    ///   攻击力数字里了(和狂暴/逆鳞那类加成一样)。所以本模块对美工刀<b>只输出"治愈层数"那部分</b>,
    ///   否则会在 HUD 上重复计算。(<see cref="KnifeBasicFlat"/>/<see cref="KnifeSharpFlat"/> 仅作
    ///   文档保留, 不参与计算。)
    ///
    /// 数值口径(与用户真机对拍确认):
    ///   · 取整: 星币锤 <b>四舍五入</b>(106→32, 112→34); 手电筒/星光 <b>向下取整</b>(每满 4 层 +1)。
    ///   · 美工刀<b>仅在满血时</b>才触发, 且只算治愈层数。
    /// 描述里的"星光"图标对应底层 SalaryCount(Hero.SalaryNum / buff 10005), 不是星币(Gold)。
    /// </summary>
    public static class RelicAtkBonus
    {
        public const int CoinHammerId = 50043;   // 星币锤
        public const int FlashlightId = 50013;   // 手电筒-强光
        public const int KnifeBasicId = 50010;   // 美工刀-初级
        public const int KnifeSharpId = 50011;   // 美工刀-锋利

        /// <summary>星币锤的触发门槛: "当前持有星币大于20"(严格大于)。</summary>
        public const int CoinHammerGoldThreshold = 20;
        /// <summary>
        /// 星币锤: 额外攻击力 = 当前星币 × 30%, <b>四舍五入</b>。
        ///
        /// 真机对拍(用户实测两局, 结算伤害都精确等于 18+加成):
        ///   星币106 → round(31.8) = 32; 18+32+7+7 = 64 ✅
        ///   星币112 → round(33.6) = 34; 18+34+6+6 = 64 ✅
        ///   若用向下取整则是 31/33 → 63/63, 两次都差 1 ⇒ 是四舍五入。
        ///
        /// 实现用整数运算 (+50 再整除) 而不是 0.30 这个 double —— 100 × 0.30 在 IEEE754 下
        /// 是 29.999999…, 直接取整会得到 29(应为 30)。整数乘除没有这个误差。
        /// </summary>
        public const int CoinHammerGoldPercent = 30;
        /// <summary>手电筒: 每 4 层星光 → 攻击力 +1。</summary>
        public const int FlashlightSalaryPerAtk = 4;
        /// <summary>美工刀-初级满血固定加成。**已含在服务器下发的攻击力里, 不再重复计入**。</summary>
        public const int KnifeBasicFlat = 2;
        /// <summary>美工刀-锋利满血固定加成。**已含在服务器下发的攻击力里, 不再重复计入**。</summary>
        public const int KnifeSharpFlat = 4;

        /// <summary>HUD 显示色: 黄 = 星币锤/手电筒(星光系), 绿 = 美工刀(治愈系)。</summary>
        public enum Tint
        {
            Yellow,
            Green
        }

        /// <summary>一条加成(一个筹码 → 一个 "+N")。</summary>
        public sealed class Bonus
        {
            public int RelicId;
            /// <summary>筹码名(如 "星币锤"), 供可选的带名显示与日志使用。</summary>
            public string Name;
            /// <summary>加成值(恒 &gt; 0; 为 0 的条目不会出现在结果里)。</summary>
            public int Value;
            public Tint Color;
        }

        /// <summary>计算所需的本局实时状态(全部取自客户端已同步的数据)。</summary>
        public struct Input
        {
            public bool HasCoinHammer;
            public bool HasFlashlight;
            public bool HasKnifeBasic;
            public bool HasKnifeSharp;
            /// <summary>当前星币(Hero.Gold)。</summary>
            public int Gold;
            /// <summary>治愈层数(Hero.CureNum, 对应 buff 10004)。</summary>
            public int CureLayers;
            /// <summary>星光层数(Hero.SalaryNum, 对应 buff 10005)。</summary>
            public int SalaryLayers;
            public int Hp;
            public int MaxHp;
        }

        /// <summary>
        /// 生命值为满。MaxHp 未知(&lt;=0)时按"不满足"处理 —— 宁可少显示, 也不要错报美工刀的加成。
        /// </summary>
        public static bool IsFullHp(Input i)
        {
            return i.MaxHp > 0 && i.Hp > 0 && i.Hp >= i.MaxHp;
        }

        /// <summary>
        /// 逐条算出加成, 只输出 Value &gt; 0 的条目。
        /// 顺序固定: 星币锤 → 手电筒 → 美工刀(初级/锋利), 与 HUD 上的显示顺序一致。
        /// </summary>
        public static List<Bonus> Evaluate(Input i)
        {
            var list = new List<Bonus>();

            // 星币锤: 星币严格大于 20 才触发; 加成为 round(星币 × 30%)。整数运算 = +50 再整除(四舍五入)。
            if (i.HasCoinHammer && i.Gold > CoinHammerGoldThreshold)
            {
                int v = (i.Gold * CoinHammerGoldPercent + 50) / 100;
                if (v > 0)
                    list.Add(new Bonus { RelicId = CoinHammerId, Name = "星币锤", Value = v, Color = Tint.Yellow });
            }

            // 手电筒: floor(星光层数 / 4), 与"当前是否命中怪物"无关(是常驻的层数换算)。
            if (i.HasFlashlight && i.SalaryLayers > 0)
            {
                int v = i.SalaryLayers / FlashlightSalaryPerAtk;   // 非负整数除法即向下取整
                if (v > 0)
                    list.Add(new Bonus { RelicId = FlashlightId, Name = "手电筒", Value = v, Color = Tint.Yellow });
            }

            // 美工刀(初级/锋利): 满血才生效, 且**只补"治愈层数"那部分** ——
            // 描述里的 +2/+4 是它自身的攻击力加成, 服务器已经算进 HUD 显示的攻击力里了, 不能重复加。
            // 两个可以同时持有 → 各出一个 +N。
            if (IsFullHp(i))
            {
                int cure = i.CureLayers > 0 ? i.CureLayers : 0;
                if (cure > 0)
                {
                    if (i.HasKnifeBasic)
                        list.Add(new Bonus { RelicId = KnifeBasicId, Name = "美工刀-初级", Value = cure, Color = Tint.Green });
                    if (i.HasKnifeSharp)
                        list.Add(new Bonus { RelicId = KnifeSharpId, Name = "美工刀-锋利", Value = cure, Color = Tint.Green });
                }
            }

            return list;
        }

        /// <summary>加成合计(没有加成时返回 0)。</summary>
        public static int Total(List<Bonus> list)
        {
            if (list == null) return 0;
            int sum = 0;
            for (int k = 0; k < list.Count; k++) sum += list[k].Value;
            return sum;
        }

        /// <summary>是否关心这个筹码 id(供调用方快速筛掉无关 id)。</summary>
        public static bool IsBonusRelic(int relicId)
        {
            return relicId == CoinHammerId || relicId == FlashlightId
                || relicId == KnifeBasicId || relicId == KnifeSharpId;
        }
    }
}
