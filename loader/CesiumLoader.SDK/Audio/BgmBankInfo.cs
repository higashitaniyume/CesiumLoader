namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 从游戏配表（`Audio.BGM` / `Audio.Bank` / `Audio.Event` / `Audio.State`）解析出的一条背景音乐记录。
    ///
    /// 游戏的 BGM 播放入口是 `Core.Audio.BGMHelper.TryPlayBGM(bgmId)` →
    /// `AudioManager.PlayBGM(bankId, stateId)` → `BGMModule.PlayBGM` →
    /// `AkSoundEngine.PostEvent(bank.PlayEventId → Audio.Event.EventID)`，
    /// <b>完全不经过</b> <c>FairyGUI.Stage.inst.playSoundCallback</c>。
    /// 因此 SDK 只能按「配表 ID + 银行播放状态」来识别 BGM，本类就是这条链路的静态描述：
    /// <list type="bullet">
    ///   <item><description><see cref="PlayEventConfigId"/> 是 `Audio.Event` 配表 ID，也就是
    ///   Toys 音效目录里 BGM 分类条目的 ID，替换规则按它匹配；</description></item>
    ///   <item><description><see cref="BankId"/> 是 `Audio.Bank` 配表 ID，用它可以向游戏查询
    ///   “这首 BGM 是否正在播放”；</description></item>
    /// </list>
    /// </summary>
    internal sealed class BgmBankInfo
    {
        /// <summary>`Audio.Bank` 配表 ID。</summary>
        public int BankId { get; set; }

        /// <summary>`Audio.Bank.PlayEventId` 指向的 `Audio.Event` 配表 ID（替换规则的匹配键）。</summary>
        public int PlayEventConfigId { get; set; }

        /// <summary>`Audio.Bank.StopEventId` 指向的 `Audio.Event` 配表 ID；未配置时为 0。</summary>
        public int StopEventConfigId { get; set; }

        /// <summary>该 bank 对应的一个 `Audio.BGM` 配表 ID（同一 bank 的多个 id 是同一首曲子的状态变体）。</summary>
        public int BgmId { get; set; }

        /// <summary>`Audio.State` 配表 ID；未指定状态时为 0。</summary>
        public int StateConfigId { get; set; }

        /// <summary>`Audio.State.State` 原文（如 `BGM_Map_Phase.Phase1`）；该 bank 未使用状态时为空。</summary>
        public string StateName { get; set; } = string.Empty;

        /// <summary>Wwise 事件英文名（如 `BGM_Map_School`）。</summary>
        public string EventName { get; set; } = string.Empty;

        /// <summary>Wwise 引擎内部事件数值 ID。</summary>
        public uint WwiseEventId { get; set; }
    }
}
