using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("角色半透明", "1.0.0", "CesiumLoader",
    "按 H 使游戏中的角色半透明化（默认 30% 不透明度），方便观察地块与重叠单位；支持长按与点击切换。",
    Permissions = ModPermission.ReadGameState | ModPermission.Input | ModPermission.UI,
    SdkVersion = "2.8.6")]
