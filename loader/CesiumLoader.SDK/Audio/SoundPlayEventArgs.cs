using System;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 声音播放前拦截/替换事件的参数。
    /// </summary>
    public sealed class SoundPlayEventArgs : EventArgs
    {
        /// <summary>游戏 Audio.Event 配表中的配置 ID（如 1001）。</summary>
        public int EventConfigId { get; }

        /// <summary>Wwise 声音事件英文名称（如 "Play_UI_Click"、"Voice_Hero_1001_Atk"）。找不到配表时为空。</summary>
        public string EventName { get; }

        /// <summary>Wwise 引擎内部事件数值 ID。</summary>
        public uint WwiseEventId { get; }

        /// <summary>
        /// 是否取消/静音游戏原版的 Wwise 音频事件。
        /// 若设置为 true 且未指定 CustomClip，则该声音被静音；
        /// 若设置为 true 且指定了 CustomClip，则播放自定义音频代替。
        /// </summary>
        public bool Cancel { get; set; }

        /// <summary>
        /// 重定向目标游戏音频配置 ID。
        /// 若赋值，原版声音会被替换为播放另一个游戏内置声音。
        /// </summary>
        public int? RedirectConfigId { get; set; }

        /// <summary>
        /// 自定义替换音频片段句柄（可以是 AudioClip 或由 AudioService 加载的对象句柄）。
        /// 若非空，则会自动触发播放。
        /// </summary>
        public object CustomClip { get; set; }

        /// <summary>自定义替换音频的音量缩放倍率（默认 1.0）。</summary>
        public float CustomVolume { get; set; } = 1.0f;

        /// <summary>自定义替换音频的播放通道（默认 Sfx）。</summary>
        public AudioChannel Channel { get; set; } = AudioChannel.Sfx;

        public SoundPlayEventArgs(int eventConfigId, string eventName, uint wwiseEventId)
        {
            EventConfigId = eventConfigId;
            EventName = eventName ?? string.Empty;
            WwiseEventId = wwiseEventId;
        }

        public override string ToString()
        {
            return string.Format("SoundEvent[Id={0}, Name={1}, WwiseId={2}, Cancel={3}]", EventConfigId, EventName, WwiseEventId, Cancel);
        }
    }
}
