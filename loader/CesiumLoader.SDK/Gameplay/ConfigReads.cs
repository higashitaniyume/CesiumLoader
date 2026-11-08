using System;
using Tools;
using UI;

namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 游戏配置表读取: 技能参数与筹码品质。
    /// 所有方法都做空保护, 查不到返回安全值, 绝不抛异常。
    /// </summary>
    public static class ConfigReads
    {
        /// <summary>PVE 商店天赋免费特殊价来源的技能 id(见游戏 <c>UILandCardShop.UpdatePVECardShopData</c>)。</summary>
        public const int TalentFreeSkillId = 10112;

        /// <summary>技能参数读取: <paramref name="index"/> 指定 <c>Params</c> 下标, 负数表示从末位倒数(如 -1 = 末位)。
        /// 越界或查不到返回 -1。</summary>
        /// <remarks>参数缺失返回 -1, 调用方据此判定特殊价未知, 不要当 0 用。</remarks>
        public static int SkillParam(int skillId, int index)
        {
            try
            {
                if (skillId <= 0) return -1;
                var skill = StaticConfigure.Skill;
                if (skill?.InfoDict == null) return -1;
                if (!skill.InfoDict.TryGetValue(skillId, out var info)) return -1;
                var ps = info?.Params;
                if (ps == null || ps.Count == 0) return -1;
                int idx = index < 0 ? ps.Count + index : index;
                if (idx < 0 || idx >= ps.Count) return -1;
                return ps[idx];
            }
            catch { return -1; }
        }

        /// <summary>筹码品质: 0=无 1=蓝 2=紫 3=橙。查不到返回 0。</summary>
        public static int RelicQuality(int relicId)
        {
            try
            {
                if (relicId <= 0) return 0;
                var cfg = relicId.GetRelicInfoConfigure();
                return cfg != null ? (int)cfg.RelicQualityType : 0;
            }
            catch { return 0; }
        }
    }
}
