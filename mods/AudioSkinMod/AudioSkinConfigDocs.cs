using CesiumLoader.SDK.Configuration;

namespace AudioSkinMod
{
    public static class AudioSkinConfigDocs
    {
        public static void Apply(ModConfig config)
        {
            if (config == null) return;

            config.Describe("enabled", ConfigDoc.Of(
                "是否启用音效方案替换功能。",
                "是否啟用音效方案替換功能。",
                "Whether sound skin and audio replacement is enabled.",
                "サウンドスキンの置き換え機能を有効にするかどうか。"
            ));

            config.Describe("activeScheme", ConfigDoc.Of(
                "当前生效的音效方案目录标识（对应 audio-schemes/<标识>）。",
                "當前生效的音效方案目錄標識（對應 audio-schemes/<標識>）。",
                "Active audio scheme ID (corresponds to audio-schemes/<id>).",
                "現在有効なサウンドスキーム識別子（audio-schemes/<ID> に対応）。"
            ));

            config.Describe("sfxVolume", ConfigDoc.Of(
                "音效通道全局倍率（0.0 ~ 2.0）。",
                "音效通道全域倍率（0.0 ~ 2.0）。",
                "SFX audio channel volume multiplier (0.0 ~ 2.0).",
                "効果音チャンネルの全体音量倍率（0.0 〜 2.0）。"
            ));

            config.Describe("bgmVolume", ConfigDoc.Of(
                "背景音乐通道全局倍率（0.0 ~ 2.0）。",
                "背景音樂通道全域倍率（0.0 ~ 2.0）。",
                "BGM audio channel volume multiplier (0.0 ~ 2.0).",
                "BGMチャンネルの全体音量倍率（0.0 〜 2.0）。"
            ));

            config.Describe("voiceVolume", ConfigDoc.Of(
                "角色语音通道全局倍率（0.0 ~ 2.0）。",
                "角色語音通道全域倍率（0.0 ~ 2.0）。",
                "Voice audio channel volume multiplier (0.0 ~ 2.0).",
                "ボイスチャンネルの全体音量倍率（0.0 〜 2.0）。"
            ));

            config.Describe("logEvents", ConfigDoc.Of(
                "是否在控制台打印游戏触发的声音事件日志（用于调试定位事件 ID）。",
                "是否在控制台列印遊戲觸發的聲音事件日誌（用於調試定位事件 ID）。",
                "Log all audio events to console for diagnostic and event lookup.",
                "ゲーム内で発生したサウンドイベントログをコンソールに出力するかどうか。"
            ));
        }
    }
}
