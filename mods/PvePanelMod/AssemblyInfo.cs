using System.Runtime.CompilerServices;
using CesiumLoader.SDK.Manifests;

[assembly: ModManifest("PVE 面板", "0.1.0", "hajimi", "PVE 对局信息面板：头像、血量、星币、手牌数、星级、技能冷却、buff，右栏波次与商店筹码，手牌抽屉。只读，不发送对局操作。打开 HandService 后会把本局观战码发给配置里的第三方观战地址。", Permissions = ModPermission.ReadGameState, SdkVersion = "3.7.0")]
// 桌面单测直接引用编译产物（这样 BCL 兼容性检查才看得到真正发布的 DLL），需要能读 internal 的纯逻辑类。
[assembly: InternalsVisibleTo("PvePanelMod.Tests")]
