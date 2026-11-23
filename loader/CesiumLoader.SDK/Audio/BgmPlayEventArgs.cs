using System;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 背景音乐（BGM）播放事件参数。
    ///
    /// <para>
    /// 游戏的 BGM 播放入口 <c>Core.Audio.BGMHelper.TryPlayBGM</c> → <c>AudioManager.PlayBGM</c> →
    /// <c>BGMModule.PlayBGM</c> → <c>AkSoundEngine.PostEvent</c> 是一条原生调用链，不经过
    /// <c>FairyGUI.Stage.inst.playSoundCallback</c>，因此 SDK 无法像音效那样替换一个委托字段来挂钩。
    /// SDK 改为按固定间隔读取游戏自己的 BGM 播放状态（<c>AudioManager.IsBGMPlaying(bankId)</c>），
    /// 在识别到「换了一首 BGM」时派发本事件。
    /// </para>
    /// <para>
    /// 与 <see cref="SoundPlayEventArgs"/> 的区别：BGM 是循环、独占、需要停止/切换的，
    /// 因此 <see cref="CustomClip"/> 不会被 <c>PlayOneShot</c> 播放，而是走
    /// <see cref="AudioService.SetBgmOverride(object, bool, float, float)"/>（同时静音原版 Wwise BGM）。
    /// </para>
    /// </summary>
    public sealed class BgmPlayEventArgs : EventArgs
    {
        /// <summary>
        /// 该 BGM 在游戏 `Audio.Event` 配表中的播放事件配置 ID
        /// （与音效目录里 BGM 分类条目的 ID 相同，例如 10011 = BGM_Login）。
        /// </summary>
        public int EventConfigId { get; }

        /// <summary>Wwise 事件英文名（如 "BGM_Login"）。找不到配表时为空。</summary>
        public string EventName { get; }

        /// <summary>Wwise 引擎内部事件数值 ID。</summary>
        public uint WwiseEventId { get; }

        /// <summary>游戏 `Audio.BGM` 配表 ID（同一 bank 的状态变体共享一首曲子）。</summary>
        public int BgmId { get; }

        /// <summary>游戏 `Audio.Bank` 配表 ID。</summary>
        public int BankId { get; }

        /// <summary>游戏 `Audio.State` 配表 ID；该 bank 未使用状态时为 0。</summary>
        public int StateConfigId { get; }

        /// <summary>
        /// 该 bank 的一个 `Audio.State.State` 原文（如 "BGM_Map_Phase.Phase2"）；无状态时为空。
        /// 同一 bank 的多个阶段各自是一首曲子的状态变体，这里给出的是配表里的代表值，仅用于日志辨识。
        /// </summary>
        public string StateName { get; }

        /// <summary>
        /// 是否抑制游戏原版 BGM。
        /// 与 <see cref="SoundPlayEventArgs.Cancel"/> 语义一致：为 true 且未指定
        /// <see cref="CustomClip"/> 时只静音原版音乐，指定了自定义音频时用自定义音乐覆盖。
        /// </summary>
        public bool Cancel { get; set; }

        /// <summary>自定义替换音频片段句柄；非空时以循环 BGM 覆盖原版音乐。</summary>
        public object CustomClip { get; set; }

        /// <summary>自定义 BGM 的音量缩放倍率（默认 1.0，仍会乘上游戏内 BGM 音量设置）。</summary>
        public float CustomVolume { get; set; } = 1.0f;

        /// <summary>自定义 BGM 是否循环播放（默认 true；原版 BGM 都是循环的）。</summary>
        public bool Loop { get; set; } = true;

        /// <summary>
        /// 构造 BGM 播放事件参数（一般由 SDK 内部使用，测试与自定义派发场景也可直接构造）。
        /// </summary>
        public BgmPlayEventArgs(int eventConfigId, string eventName, uint wwiseEventId,
            int bgmId = 0, int bankId = 0, int stateConfigId = 0, string stateName = null)
        {
            EventConfigId = eventConfigId;
            EventName = eventName ?? string.Empty;
            WwiseEventId = wwiseEventId;
            BgmId = bgmId;
            BankId = bankId;
            StateConfigId = stateConfigId;
            StateName = stateName ?? string.Empty;
        }

        public override string ToString()
        {
            return string.Format("BgmEvent[Id={0}, Name={1}, Bank={2}, State={3}, Cancel={4}]",
                EventConfigId, EventName, BankId, StateName, Cancel);
        }
    }
}
