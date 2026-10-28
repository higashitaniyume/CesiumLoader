using CesiumLoader.SDK.Configuration;

namespace CameraProbeMod
{
    /// <summary>config.json 里各字段的多语言说明。</summary>
    internal static class CameraProbeConfigDocs
    {
        internal static bool Apply(ModConfig config)
        {
            if (config == null) return false;
            return config.Describe("probeKey", D(
                "输出相机探测信息的按键，默认 F9。",
                "輸出相機探測資訊的按鍵，預設 F9。",
                "Key that prints camera probe information (default F9).",
                "カメラ情報を出力するキー（既定 F9）。"));
        }

        private static ConfigDoc D(string zhHans, string zhHant, string en, string ja)
            => ConfigDoc.Of(zhHans, zhHant, en, ja);
    }
}
