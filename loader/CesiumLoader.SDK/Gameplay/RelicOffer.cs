namespace CesiumLoader.SDK.Gameplay
{
    /// <summary>
    /// 一次筹码三选一候选的完整内容(<see cref="GameEvents.RelicOffered"/>)。
    ///
    /// 来源是 <c>PredictActionS2C</c> 里的 5211 动作, 解 <c>SelectRelicC2S</c>。
    /// </summary>
    public sealed class RelicOffer
    {
        /// <summary>候选玩家。</summary>
        public long PlayerId;

        /// <summary>动作序号(同一批候选会被重复广播, 按它去重)。</summary>
        public long Sn;

        /// <summary>本次给出的候选筹码 id 列表(通常 3 个)。</summary>
        public int[] RelicIds;

        /// <summary>
        /// 这条候选是否由"刷新"产生。注意它与结果消息
        /// <see cref="GameEvents.RelicResult"/> 的 <c>isReroll</c> 是两回事, 不要混用。
        /// </summary>
        public bool IsReroll;

        /// <summary>选中下标(候选阶段通常没有意义, 保留原始字段)。</summary>
        public int Index;

        /// <summary>筹码等级(原始字段)。</summary>
        public int Lv;

        /// <summary>升星等级: 大于 0 表示这次候选是升星。</summary>
        public int SupLv;

        public override string ToString()
        {
            return "筹码候选(pid=" + PlayerId + ", sn=" + Sn +
                   ", 候选=" + (RelicIds == null ? 0 : RelicIds.Length) +
                   ", 刷新=" + IsReroll + ")";
        }
    }
}
