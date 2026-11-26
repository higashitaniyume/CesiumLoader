using CesiumLoader.SDK.Configuration;

namespace HubBridgeMod
{
    internal static class HubBridgeConfigDocs
    {
        public static void Apply(ModConfig cfg)
        {
            if (cfg == null) return;

            cfg.Describe("Enabled", ConfigDoc.Of(
                "总开关：关闭后不再连接 AstralParty.Toys 组队大厅本地桥接服务。",
                "總開關：關閉後不再連線 AstralParty.Toys 組隊大廳本地橋接服務。",
                "Master switch: disables connecting to the local AstralParty.Toys team hub bridge.",
                "全体スイッチ：オフにすると AstralParty.Toys チームハブのローカルブリッジに接続しません。"));

            cfg.Describe("PollIntervalMs", ConfigDoc.Of(
                "心跳与指令轮询间隔（毫秒），范围 100–2000，默认 250。",
                "心跳與指令輪詢間隔（毫秒），範圍 100–2000，預設 250。",
                "Heartbeat and command polling interval in milliseconds (100–2000, default 250).",
                "ハートビートおよびコマンドポーリング間隔（ミリ秒、100～2000、既定 250）。"));

            cfg.Describe("RequestTimeoutSeconds", ConfigDoc.Of(
                "本地桥接 HTTP 请求超时时间（秒），范围 2–30，默认 5。",
                "本地橋接 HTTP 請求逾時時間（秒），範圍 2–30，預設 5。",
                "Local bridge HTTP request timeout in seconds (2–30, default 5).",
                "ローカルブリッジ HTTP リクエストのタイムアウト秒数（2～30、既定 5）。"));

            cfg.Describe("EndpointPath", ConfigDoc.Of(
                "可选自定义 endpoint.json 路径；留空时自动读取加载器目录下的 hub-bridge/endpoint.json。",
                "可選自訂 endpoint.json 路徑；留空時自動讀取載入器目錄下的 hub-bridge/endpoint.json。",
                "Optional custom path to endpoint.json; leave empty to use hub-bridge/endpoint.json under the loader root.",
                "任意の endpoint.json パス；空の場合はローダーディレクトリの hub-bridge/endpoint.json を使用します。"));
        }
    }
}
