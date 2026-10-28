using CesiumLoader.SDK.Configuration;

namespace FreeCameraMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class FreeCameraConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            bool changed = false;
            changed |= config.Describe("autoFollow", D(
                "保持游戏自动跟随镜头；改为 false 才启用锁定视角。",
                "保持遊戲自動跟隨鏡頭；改為 false 才啟用鎖定視角。",
                "Keep the game's automatic camera follow; set false to lock the view.",
                "ゲームの自動カメラ追従を維持します。false にすると視点を固定します。"));
            changed |= config.Describe("zoomStep", D(
                "每格滚轮移动的距离（米），默认 20。",
                "每格滾輪移動的距離（公尺），預設 20。",
                "Distance moved per wheel step in meters (default 20).",
                "ホイール 1 目盛りあたりの移動距離（メートル、既定 20）。"));
            changed |= config.Describe("zoomInMax", D(
                "沿视线向前（拉近）的最大距离（米），默认 120。",
                "沿視線向前（拉近）的最大距離（公尺），預設 120。",
                "Maximum forward (zoom-in) distance along the view direction in meters (default 120).",
                "視線方向へ近づける最大距離（メートル、既定 120）。"));
            changed |= config.Describe("zoomOutMax", D(
                "沿视线向后（拉远）的最大距离（米），默认 1500。",
                "沿視線向後（拉遠）的最大距離（公尺），預設 1500。",
                "Maximum backward (zoom-out) distance along the view direction in meters (default 1500).",
                "視線方向へ遠ざけられる最大距離（メートル、既定 1500）。"));
            changed |= config.Describe("invertWheel", D(
                "反转滚轮方向；默认向前滚为拉近。",
                "反轉滾輪方向；預設向前滾為拉近。",
                "Invert the wheel direction; by default scrolling forward zooms in.",
                "ホイール方向を反転します（既定は前方向でズームイン）。"));
            changed |= config.Describe("smooth", D(
                "平滑过渡；关闭后每格立刻到位。",
                "平滑過渡；關閉後每格立刻到位。",
                "Enable smooth transitions; when off, each wheel step snaps immediately.",
                "スムーズな移動を有効にします。オフでは 1 目盛りごとに即座に移動します。"));
            changed |= config.Describe("smoothTime", D(
                "平滑时间常数（秒），默认 0.12。",
                "平滑時間常數（秒），預設 0.12。",
                "Smoothing time constant in seconds (default 0.12).",
                "スムーズ移動の時定数（秒、既定 0.12）。"));
            changed |= config.Describe("ensureFarClip", D(
                "拉远时按需抬高 farClip，避免远处棋盘被裁剪。",
                "拉遠時視需要提高 farClip，避免遠處棋盤被裁剪。",
                "Raise farClip when zooming out so the distant board is not clipped.",
                "ズームアウト時に farClip を必要に応じて広げ、遠くの盤面が切れないようにします。"));
            changed |= config.Describe("requireReadyCamera", D(
                "菜单/加载过渡期不响应滚轮。",
                "選單/載入過渡期不回應滾輪。",
                "Ignore the wheel while a menu or loading transition camera is active.",
                "メニューや読み込み中の遷移カメラではホイール操作を無視します。"));
            changed |= config.Describe("maxReadyCameraHeight", D(
                "判定菜单/过渡相机的高度阈值（米），默认 500。",
                "判定選單/過渡相機的高度閾值（公尺），預設 500。",
                "Height threshold for detecting menu/transition cameras in meters (default 500).",
                "メニュー/遷移カメラ判定の高さしきい値（メートル、既定 500）。"));
            changed |= config.Describe("notify", D(
                "显示滚轮缩放/恢复的屏幕提示。",
                "顯示滾輪縮放/還原的螢幕提示。",
                "Show on-screen notices when zooming or restoring the camera.",
                "ズームや視点復元時に画面へ通知を表示します。"));
            changed |= config.Describe("restoreKey", D(
                "恢复原视角的按键，默认 F1。",
                "還原原視角的按鍵，預設 F1。",
                "Key that restores the original view (default F1).",
                "元の視点に戻すキー（既定 F1）。"));
            changed |= config.Describe("resetKey", D(
                "第二个恢复键，默认 F2；None 表示不用。",
                "第二個還原鍵，預設 F2；None 表示不用。",
                "Second restore key (default F2); None disables it.",
                "2 つ目の視点復元キー（既定 F2）。None で無効になります。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
