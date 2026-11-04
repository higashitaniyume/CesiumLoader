using CesiumLoader.SDK.Configuration;

namespace HeroAutoSelectMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class HeroAutoSelectConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("Enabled", D(
                "总开关：关闭后不再自动选择角色，重新开启即可恢复。",
                "總開關：關閉後不再自動選擇角色。",
                "Master switch: disables automatic character selection.",
                "全体スイッチ：オフにするとキャラクターの自動選択を行いません。"));
            changed |= config.Describe("HeroId", D(
                "要自动选择的角色 ID，默认 102（芬妮「古怪神探」）。",
                "要自動選擇的角色 ID，預設 102（芬妮「古怪神探」）。",
                "Character ID to select automatically (default 102, Fanny / Eccentric Detective).",
                "自動選択するキャラクター ID（既定 102、ファニー「奇想探偵」）。"));
            changed |= config.Describe("AutoConfirm", D(
                "选好角色后是否自动点「确定」。关闭时需要手动确认。",
                "選好角色後是否自動點「確定」。關閉時需要手動確認。",
                "Automatically confirm the character after selecting. Turn off to confirm manually.",
                "キャラクター選択後に自動で「確定」するか。オフの場合は手動で確定します。"));
            changed |= config.Describe("DelayMilliseconds", D(
                "进入选人阶段后等待多少毫秒再选择，范围 0–10000，默认 500。",
                "進入選人階段後等待多少毫秒再選擇，範圍 0–10000，預設 500。",
                "Delay before selecting after the character screen opens, in milliseconds (0–10000, default 500).",
                "選人画面が開いてから選択するまでの待ち時間（ミリ秒、0～10000、既定 500）。"));
            changed |= config.Describe("ConfirmDelayMilliseconds", D(
                "发出选择后等待多少毫秒再确认，范围 0–10000，默认 500。",
                "發出選擇後等待多少毫秒再確認，範圍 0–10000，預設 500。",
                "Delay before confirming after the selection is sent, in milliseconds (0–10000, default 500).",
                "選択を送信してから確定するまでの待ち時間（ミリ秒、0～10000、既定 500）。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
