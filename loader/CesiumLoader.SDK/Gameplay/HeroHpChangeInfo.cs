namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 一次完整血量变化(<see cref="GameEvents.HeroHpChangedFull"/>)。
    /// 与旧事件 <see cref="GameEvents.HeroHpChanged"/> 并行广播, 旧事件保持原语义不变。
    /// </summary>
    public sealed class HeroHpChangeInfo
    {
        /// <summary>受害者。</summary>
        public long PlayerId;

        /// <summary>施加者(0 或环境)。</summary>
        public long Killer;

        /// <summary>变化前 HP。</summary>
        public int OriHp;

        /// <summary>报告伤害量(可为负; 缺字段时 0)。</summary>
        public int ChangeHp;

        /// <summary>实际变化量(含溢出方向), 与 <see cref="ChangeHp"/> 可能不同。</summary>
        public int RealChangeHp;

        /// <summary>变化后 HP。<b>0 表示未提供, 不是血量为 0</b>, 不要据此判死亡。</summary>
        public int RealHp;

        /// <summary>上限。</summary>
        public int MaxHp;

        public override string ToString()
        {
            return "血量(pid=" + PlayerId + ", killer=" + Killer +
                   ", 变化" + ChangeHp + "/" + RealChangeHp +
                   ", 血" + OriHp + "->" + RealHp + "/" + MaxHp + ")";
        }
    }
}
