using System;
using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>
    /// 一次战斗读数的结构化结果，给别的 mod（比如 PVE 面板）自己排版显示。
    /// 每次重算都换一个新对象（不改旧对象），读的一方拿到引用就是一份完整、不会被改到一半的数据。
    /// </summary>
    public sealed class BattleOddsSnapshot
    {
        /// <summary>递增序号：变了就是有新结果。</summary>
        public long Version;
        public long BattleId;
        public string AttackerName = "";
        public string DefenderName = "";
        /// <summary>自己是攻方 / 守方；两个都 false = 观战别人的战斗。</summary>
        public bool SelfIsAttacker, SelfIsDefender;

        // ---- 防守方视角 ----
        public bool ShowDefender;
        /// <summary>攻方已经投过骰（下面的防/闪概率才是确定值）。没投时只有 WorstKnockProb。</summary>
        public bool AttackerRolled;
        public int DefenderHp;
        public int AttackerFinalAtk;
        /// <summary>选防御 / 选闪避时的被击倒概率（0～1）。显示「存活」就用 1 - 它。</summary>
        public double DefendKnockProb, DodgeKnockProb;
        public bool DefendBetter, DodgeBetter, DodgeSure;
        /// <summary>攻方未投骰时，按最坏攻击算的被击倒概率。</summary>
        public double WorstKnockProb;
        public bool CannotFightBack;

        // ---- 攻击方视角 ----
        public bool ShowAttacker;
        public double KillProb;
        public int TargetHp;
        public int TypicalDamage;

        /// <summary>补充说明（摩西精密技巧、邦妮标记、蓄力一击、目标易伤/减伤/免疫、已计入实际牌点等），纯文本，一条一行。</summary>
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// 「信箱」：战斗胜率助手把每次算好的结果放进来，别的 mod 来取。
    /// 对方 mod 不需要编译期引用本 mod（不用声明硬依赖），按名字反射读 <see cref="Latest"/> 即可；本 mod 没装时对方自然什么都读不到。
    ///
    /// 另外提供一个「我来显示」的约定：外部面板每次读取时调用 <see cref="ClaimDisplay"/>，
    /// 本 mod 在最近 <see cref="ClaimSeconds"/> 秒内收到过声明，就不再画自己 FightWindow 里的读数框，避免同一份信息出现两次；
    /// 外部面板被关掉后不再声明，几秒后自动恢复自己显示。
    /// </summary>
    public static class BattleOddsFeed
    {
        public const double ClaimSeconds = 3.0;
        private static long _version;
        private static long _claimTicks;

        /// <summary>最近一次战斗读数；不在战斗中（或战斗结束）为 null。</summary>
        public static BattleOddsSnapshot Latest { get; private set; }

        /// <summary>外部面板声明「战斗读数由我显示」。需要持续调用（每秒至少一次）。</summary>
        public static void ClaimDisplay() { _claimTicks = DateTime.UtcNow.Ticks; }

        /// <summary>最近有外部面板在显示战斗读数。</summary>
        public static bool ExternallyDisplayed
        {
            get { return (DateTime.UtcNow - new DateTime(_claimTicks, DateTimeKind.Utc)).TotalSeconds < ClaimSeconds; }
        }

        internal static void Publish(BattleOddsSnapshot s)
        {
            s.Version = ++_version;
            Latest = s;
        }

        internal static void Clear() { Latest = null; }
    }
}
