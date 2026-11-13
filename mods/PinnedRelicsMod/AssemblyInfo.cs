using System.Reflection;
using CesiumLoader.SDK.Manifests;

[assembly: AssemblyTitle("PinnedRelicsMod")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: ModManifest("关键筹码置顶", "1.2.0", "Aperfish",
    "持有指定筹码后，在左侧对应玩家的星币下方横排显示筹码图标。",
    Permissions = ModPermission.ReadGameState | ModPermission.UI, SdkVersion = "2.8.6")]
