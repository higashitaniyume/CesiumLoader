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
                "显示滚轮缩放/方向键旋转/恢复的屏幕提示。",
                "顯示滾輪縮放/方向鍵旋轉/還原的螢幕提示。",
                "Show on-screen notices when zooming, rotating or restoring the camera.",
                "ズーム・回転・視点復元時に画面へ通知を表示します。"));
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
            changed |= config.Describe("rotateLeftKey", D(
                "向左侧旋转地图的按键，默认 LeftArrow；None 表示关掉这个方向。",
                "向左側旋轉地圖的按鍵，預設 LeftArrow；None 表示關閉這個方向。",
                "Key that rotates the map to the left (default LeftArrow); None disables this direction.",
                "地図を左へ回転するキー（既定 LeftArrow）。None でこの方向を無効にします。"));
            changed |= config.Describe("rotateRightKey", D(
                "向右侧旋转地图的按键，默认 RightArrow；None 表示关掉这个方向。",
                "向右側旋轉地圖的按鍵，預設 RightArrow；None 表示關閉這個方向。",
                "Key that rotates the map to the right (default RightArrow); None disables this direction.",
                "地図を右へ回転するキー（既定 RightArrow）。None でこの方向を無効にします。"));
            changed |= config.Describe("rotateSpeed", D(
                "按住旋转键时的速度（度/秒），默认 90；0 表示只能点按。",
                "按住旋轉鍵時的速度（度/秒），預設 90；0 表示只能點按。",
                "Rotation speed while a rotate key is held, in degrees per second (default 90); 0 keeps tap-only rotation.",
                "回転キーを押し続けたときの速度（度/秒、既定 90）。0 ではタップ回転のみになります。"));
            changed |= config.Describe("rotateStep", D(
                "点按一次旋转键转过的角度（度），默认 15；0 表示关掉点按步进。",
                "點按一次旋轉鍵轉過的角度（度），預設 15；0 表示關閉點按步進。",
                "Angle rotated by one tap of a rotate key in degrees (default 15); 0 disables tap rotation.",
                "回転キーを 1 回押したときの角度（度、既定 15）。0 でタップ回転を無効にします。"));
            changed |= config.Describe("invertRotate", D(
                "反转左右旋转方向，默认 false：→ 让视点向右转；改成 true 则 → 让地图向右转。",
                "反轉左右旋轉方向，預設 false：→ 讓視點向右轉；改為 true 則 → 讓地圖向右轉。",
                "Invert the left/right rotation direction (default false: → turns the view right; true: → turns the map right).",
                "左右の回転方向を反転します（既定 false：→ で視点が右へ回る。true：→ で地図が右へ回る）。"));
            changed |= config.Describe("blockPanWhileRotate", D(
                "旋转时抵掉游戏自己的左右方向键平移，默认 true；改成 false 则旋转的同时地图也会被推走。",
                "旋轉時抵掉遊戲自己的左右方向鍵平移，預設 true；改為 false 則旋轉的同時地圖也會被推走。",
                "Cancel the game's own left/right arrow pan while rotating (default true); false lets the map slide while rotating.",
                "回転中にゲーム自身の左右キー移動を打ち消します（既定 true）。false では回転と同時に地図も動きます。"));
            return changed;
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
