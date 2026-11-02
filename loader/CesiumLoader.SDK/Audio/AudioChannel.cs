namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 音频分类通道。用于分类管理音量与独立播放。
    /// </summary>
    public enum AudioChannel
    {
        /// <summary>主通道（影响所有声音）。</summary>
        Master = 0,

        /// <summary>背景音乐通道。</summary>
        Bgm = 1,

        /// <summary>音效 / UI 界面声音通道。</summary>
        Sfx = 2,

        /// <summary>角色语音通道。</summary>
        Voice = 3,

        /// <summary>自定义独立通道。</summary>
        Custom = 4
    }
}
