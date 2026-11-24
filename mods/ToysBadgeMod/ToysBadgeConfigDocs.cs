using CesiumLoader.SDK.Configuration;

namespace ToysBadgeMod
{
    internal static class ToysBadgeConfigDocs
    {
        public static void Apply(ModConfig cfg)
        {
            if (cfg == null) return;

            cfg.Describe("Enabled", ConfigDoc.Of(
                "总开关：关闭后不连接服务、不显示图标。",
                "總開關：關閉後不連線服務、不顯示圖示。",
                "Master switch: stops connecting to the service and hides all badges.",
                "全体スイッチ：オフにするとサービスへ接続せず、アイコンも表示しません。"));

            cfg.Describe("ServiceUrl", ConfigDoc.Of(
                "房间服务地址，默认 https://connect.hiynet.com ；清空则完全不连接、不发起任何请求。",
                "房間服務位址，預設 https://connect.hiynet.com ；清空則完全不連線、不發出任何請求。",
                "Room service URL (defaults to https://connect.hiynet.com). Clear it to stay fully disconnected.",
                "ルームサービスの URL（既定は https://connect.hiynet.com）。空にすると一切接続しません。"));

            cfg.Describe("RefreshSeconds", ConfigDoc.Of(
                "局内刷新与保活间隔（秒），范围 15–55，默认 25。",
                "局內重新整理與保活間隔（秒），範圍 15–55，預設 25。",
                "In-match refresh and keepalive interval in seconds (15–55, default 25).",
                "対局中の更新およびキープアライブ間隔（秒、15～55、既定 25）。"));

            cfg.Describe("TimeoutSeconds", ConfigDoc.Of(
                "单次请求超时秒数，范围 5–60，默认 15。",
                "單次請求逾時秒數，範圍 5–60，預設 15。",
                "Per-request timeout in seconds (5–60, default 15).",
                "1 回のリクエストのタイムアウト秒数（5～60、既定 15）。"));

            cfg.Describe("IconSize", ConfigDoc.Of(
                "圆形工具箱图标直径（像素），范围 14–64，默认 26。",
                "圓形工具箱圖示直徑（像素），範圍 14–64，預設 26。",
                "Circular badge icon diameter in UI pixels (14–64, default 26).",
                "丸型ツールボックスアイコンの直径（UI ピクセル、14～64、既定 26）。"));

            cfg.Describe("Gap", ConfigDoc.Of(
                "图标右边缘与玩家头像左边缘之间的间距，范围 -32–48，默认 4。",
                "圖示右邊緣與玩家頭像左邊緣之間的間距，範圍 -32–48，預設 4。",
                "Horizontal gap between the badge right edge and avatar left edge (-32–48, default 4).",
                "アイコン右端とアバター左端の間隔（-32～48、既定 4）。"));

            cfg.Describe("OffsetX", ConfigDoc.Of(
                "图标水平微调偏移，范围 -200–200，默认 0。",
                "圖示水平微調偏移，範圍 -200–200，預設 0。",
                "Horizontal fine-tuning offset in UI pixels (-200–200, default 0).",
                "アイコンの水平微調整オフセット（-200～200、既定 0）。"));

            cfg.Describe("OffsetY", ConfigDoc.Of(
                "图标垂直微调偏移，范围 -200–200，默认 0。",
                "圖示垂直微調偏移，範圍 -200–200，預設 0。",
                "Vertical fine-tuning offset in UI pixels (-200–200, default 0).",
                "アイコンの垂直微調整オフセット（-200～200、既定 0）。"));

            cfg.Describe("ShowSelfBadge", ConfigDoc.Of(
                "是否在自己的头像左侧也显示图标，默认开启。",
                "是否在自己的頭像左側也顯示圖示，預設開啟。",
                "Also show the circular badge next to your own avatar (default on).",
                "自分のアバターの左側にもアイコンを表示します（既定: オン）。"));

            cfg.Describe("ShowOfflineMembers", ConfigDoc.Of(
                "是否继续为已判定离线的成员保留显示图标，默认关闭。",
                "是否繼續為已判定離線的成員保留顯示圖示，預設關閉。",
                "Keep showing badges for members marked offline (default off).",
                "オフライン判定されたメンバーのアイコンも表示し続けます（既定: オフ）。"));

            cfg.Describe("ShareChatClientId", ConfigDoc.Of(
                "当同时启用 InMatchChatMod 时复用它的 ClientId，避免同一局内出现重复成员名额。默认开启。",
                "當同時啟用 InMatchChatMod 時複用它的 ClientId，避免同一局內出現重複成員名額。預設開啟。",
                "Reuse InMatchChatMod's ClientId when it is active so the same player does not take two slots. Enabled by default.",
                "InMatchChatMod が有効な場合はその ClientId を共有し、同一対局内で重複メンバーになるのを防ぎます。既定は有効。"));

            cfg.Describe("IconPath", ConfigDoc.Of(
                "可选自定义 PNG 图标路径；留空使用内置圆形 Astral Party Toys 图标。",
                "可選自訂 PNG 圖示路徑；留空使用內建圓形 Astral Party Toys 圖示。",
                "Optional custom PNG icon path; leave empty to use the built-in circular Astral Party Toys icon.",
                "任意のカスタム PNG アイコンのパス。空欄の場合は内蔵の丸型 Astral Party Toys アイコンを使用します。"));

            cfg.Describe("ClientId", ConfigDoc.Of(
                "本机安装标识，留空会自动生成；用于同局重连复用成员身份，请勿手改。",
                "本機安裝識別，留空會自動產生；用於同局重連沿用成員身分，請勿手動修改。",
                "Per-install identifier; leave empty to auto-generate. Keeps member identity across reconnects. Do not edit by hand.",
                "この環境の識別子。空欄なら自動生成されます。再接続時に同じメンバーとして扱うための値なので、手動で変更しないでください。"));
        }
    }
}
