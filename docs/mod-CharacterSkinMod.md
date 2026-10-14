# CharacterSkinMod 0.2.1

0.2.1 修复自定义图片私有地址遗漏游戏布局字典的缺陷：为私有地址继承原图的完整立绘偏移、HUD 偏移/缩放/翻转。原 PNG 尺寸正确也会受此缺陷影响，例如原 HUD scale=0.6 在旧版因查表未命中退为 1。布局来源优先采用包 `SkinItemId` 对应原皮肤；包为 0 时采用同角色首套皮肤，与 Toys 裁剪画布一致；该槽无布局时依次回退到方案官方基础、目标原皮肤。InGameCharacter 空槽按 Character/SfwCharacter 回退。别名先加入 `Skin.Offsets`，同步已存在的 `heroSkinOffsetDict`；新建人物逻辑会从列表自然建立别名。不会写入原图条目或虚构 `OffsetDict` 的数字 ID，卸载只删除仍归本模组所有的别名。状态文件新增 `LayoutAliases`。

## 多角色皮肤方案

启用方案由 `config.json` 的 `ActiveSchemeId` 指定，方案位于 `AstralParty_ModLoader/character-skin-schemes/<id>/character-scheme.json`。每条规则包含 `Id / HeroId / TargetSkinItemId / OfficialSourceSkinItemId / CustomPackageId`。目标 0 为角色通配；同角色的精确目标优先于通配。允许多角色、多目标及官方与自定义来源组合。自定义包旧 `SkinItemId` 只用于默认创建规则，不限制同角色复用。

官方来源须属于同一角色。所有来源从修改前的配置 Clone 快照读取，避免 A→B、B→C 链式污染；目标 ItemID、Index、文案 ID、语音、视频、特效、特殊 Timeline 与战斗资源配置保留原版。视觉白名单复制立绘/SFW/HUD/头像/准备图/窄幅图/地块标志/升星图、Bust/SfwBust、SkinScale 与 Actions。原皮肤对象保持 identity，分别原地更新 protobuf `StandingPaintingDict` 与 `StandingPaintings` 两份对象，已有 HeroCardLogic/HeroCardData/RoomPlayer 引用继续有效。

**官方人物标准动画实际使用来源 `CharacterAsset.animationController`。** 模组以来源快照的 `GetCharacterLabel()+平台标签` 异步预载官方 CharacterAsset；按地图人物 owner 的 HeroId 与实际目标 ItemID 精确替换该人物 Animator 控制器。保留目标配置 CharacterLabel 和游戏按 heroId 的动画缓存，不修改角色 ID 或全局角色资源缓存，因此同一角色不同目标可以使用不同来源。替换时保存控制器并保持已存在的状态 hash、normalizedTime 及 float/int/bool 参数，继续由游戏 CharacterAnimator 触发事件；卸载时按所有权恢复控制器并保留当前 normalizedTime。纯自定义方案不预载额外官方动画。无 owner 的 UI 优先匹配唯一原图集，其次匹配原控制器；歧义时明确警告并保持原版。

自定义内容在官方视觉基础上覆盖。标准皮肤图片槽始终分配目标独有私有地址，包含 Bust/SfwBust 列表地址，避免不同目标互相覆盖。CharacterMap 与 Name/Nick/Biography/Lines 为角色共用内容，`asset:` 为全局地址；一致的覆盖允许复用，不同覆盖拒绝整份方案。运行时还根据实际图片地址和本地化 ID 检查跨角色共享冲突。没有有效文案 ID、缺包、错误角色归属、无法解码图片或加载官方动画会写入明确失败状态。

配置和自定义缓存事务式应用；部分应用失败会回滚。恢复只修改仍等于本模组 Applied 值的字段、列表、控制器及缓存，保留其他组件后续改动。卸载/失败先解除已替换 GLoader 的自定义引用，恢复原缓存并重新加载原地址，再 Dispose 自定义纹理与释放预载官方资源句柄。

`status.json` 提供 `State`（Disabled/Pending/Loading/Applied/Failed/Unloaded）、`Error`、方案/规则/目标数量、图片/精灵/控制器实际应用计数及 WarningMessages。首次应用等待所需角色、皮肤和文案配置加载完成。方案与文件修改需要完全退出并重启游戏，不支持本轮热更。旧 ActivePackages 在没有 ActiveSchemeId 时仍可读取，整份旧配置也接受相同基本验证。Toys 服务负责迁移与旧配置备份。

本地角色皮肤模组，Toys 的皮肤页提供并列的卡牌皮肤与角色皮肤 Tab。

## 已实现

- 按 HeroId 与原皮肤 ItemID 启用。SkinItemId=0 覆盖该角色所有原皮肤。
- FairyGUI 图片缓存和可见 GLoader 的资源替换：立绘、SFW、头像、HUD、准备图、窄幅图、半身图、地块标志。CharacterMap 为角色共用地址，影响该角色的倒计时和小地图头像。
- Name/Nick/Biography/Lines 角色共用文案及皮肤长短描述。没有原文案 ID 时拒绝应用并写入失败状态。
- PNG/JPG 动作帧序列、逐帧时长、FPS、循环、自定义 pivot/PPU/缩放，原精灵名 SpriteMappings。
- 整段 `Animations` 优先于 `SpriteMappings`：非循环动作帧较少时播完后停在自定义末帧，循环动作重复自定义序列；非循环动作帧较多时，若游戏控制器提前切换动作，会继续播放当前自定义序列直到结束，再接管此时游戏正在显示的动作。可能延后或跳过期间的短动作，不改变游戏事件时序。部分 SpriteMappings 在同动作已有成功映射后，缺失帧沿用最近一张自定义图片；未匹配的过渡动作保留上一张自定义精灵；首次没有可用精灵时采用自定义 Idle 首帧，包没有 Idle 且没有历史自定义帧时仍保留原图。额外映射图片只有被原动画引用才显示，长序列需使用 Animations。建议用 FPS/逐帧时长使总时长接近原动作。本次新增行为通过离线测试；2026-10-14 用户完成实机验证并确认渲染正常。
- 0.1.2 新增 UseOriginalSpriteLayout：仅对 SpriteMappings 生效，使用被替换原帧的归一化 pivot 与 PPU，仍应用 Layout.Scale。供保持原画布尺寸的逐帧皮肤模板使用；映射跟随原动画时序，独立动作序列继续使用布局设置。
- 已制作蓝海晴默认皮肤 100114001 示例 ZIP：17 个标准图片槽位加升星插图、23 个原动作、905 张精灵帧。通过 Toys 导入/导出和 SDK CesiumJson 清单读取校验，原尺寸、锚点和文件来源记录在 source-metadata.json。示例包未自动启用，游戏中的映射布局仍待下一次启动验证。
- 保留人物 Animator 组件、CharacterAnimator 事件流程与 Transform；有官方来源时替换实际控制器并保持状态与 normalizedTime。OnUpdate 恢复原精灵，OnLateUpdate 应用自制精灵；独立自定义动作仍由精灵名推断与 Time.time 计时，连续同动作重触发和暂停需真机核对。
- 每帧 LateUpdate 发现并接管地图与战斗人物。BattleActor 按其 playerData 的 HeroId/ItemID 匹配，替换 actorRenderer 本体 SpriteRenderer，保留 Timeline、位移、特效和游戏事件。此补丁 Release 构建通过、角色皮肤离线测试 39 项全部通过；2026-10-14 用户完成实机验证并确认渲染正常。地图人物按 owner 与目标皮肤匹配；复用人物时更新规则及包。无 owner 的 UI 优先原图集匹配，再按原控制器匹配，不明或歧义时保持原版并警告。
- Toys 新建、ZIP 导入导出、删除（移入 .deleted）、启用停用、多角色并行、图片选择、动画导入预览、文案及清单编辑。

## 新建时从游戏导出所选官方皮肤模板

新建角色皮肤勾选「从游戏导出官方皮肤素材」，在「导出哪套官方皮肤」中选择该人物已有的官方皮肤，可修改热更新缓存目录。默认使用当前用户的 AppData/LocalLow/feimo/AstralParty_CN/com.unity.addressables，同时检索当前游戏的 *_Data/StreamingAssets/aa。导出来源与方案替换目标独立，可用皮肤 B 的素材替换原皮肤 A；所选资源须已下载到本地。来源不存在或属于其他人物时明确报错，不退回默认皮肤。不勾选则创建空白自定义包。

导出使用内置 C# 提取器（AssetsTools.NET 3.0.5、AssetsTools.NET.Texture 3.0.2），无需 Python 或外部解包程序。通过 .NET 读取序列化对象、解码纹理并按原 Sprite 顶点/UV 网格还原透明 PNG。提取仅访问本地文件，不联网。按缓存 catalog 的地址依赖定位 bundle，优先热更新缓存；读取 GameData_CN 中的实际 Character/Skin 配置（缺少时使用 Toys 配置），再导出图片、精灵和动画剪辑。资源未下载时说明缺失或失败，动画/图片完全未定位则不创建空模板。语音、视频与特效保留原版，Texts 留空。

提交后立即加入后台队列并关闭新建窗口，可继续添加多个任务；按加入顺序逐个执行，避免解包内存峰值叠加。右下角常驻任务面板显示阶段、百分比与进度条，切换页面或当前游戏不隐藏；等待任务可取消排队，运行任务可终止，状态保持到手动关闭消息。取消等待当前资源操作完成后检查令牌并清理半成品，不发布模板；失败或取消后继续下一任务。队列仅在当前 Toys 进程内有效，不跨重启持久化。成功后选中新包，不自动启用；失败清理半成品。source-metadata.json 记录 catalog、读取的 bundle、缺失资源与逐帧尺寸/锚点。实际通过创建服务提取蓝海晴：18 个图片入口、23 个动作、905 帧、缺失 0，来源同时包含安装目录 bundle 和 Addressables 缓存 __data。其他角色需依其本地已下载资源验证。

## 导出当前动作为 GIF

二维精灵动画工具栏「导出当前动作 GIF」打开保存对话框，使用 Magick.NET-Q8-AnyCPU 15.0.0 编码，加入同一后台队列，支持进度和取消。导出当前动作帧顺序、逐帧 DurationsMs（无逐帧时长时按界面 FPS）、界面循环设置。保留透明背景，每帧清除背景避免上一帧残影；不同尺寸统一为最大画布并按底部居中对齐。GIF 为 256 色及单层透明、时长单位 10 ms，使用累计时长取整减少 30 FPS 漂移。写入临时文件，成功后原子替换选定目标，失败或取消保留原文件。单帧解码/量化/最终编码期间取消会在该操作结束后生效。

## Toys 动画逐帧操作

导入或保存后保留正在编辑的动作和帧；新帧数量变少时仅将当前帧限制在有效范围，不跳回 Idle。动作切换时重置为第一帧，空动作的 FPS/循环使用默认值。

每帧提供透明背景缩略图，点击选中并显示大图，可用上一帧、下一帧或滑块跳转。替换选中帧先选择 PNG/JPG，再按原帧画布裁剪或等比补边，默认不裁剪，输出透明 PNG；保留帧数、顺序、FPS、循环与逐帧时长。映射包会同步 SpriteMappings；同一原精灵有多次引用时一起更新以保持预览与游戏一致，独立动画只更新选中位置。

导入帧序列替换整个动作并移除该动作旧映射，使用独立帧序列。恢复此动作移除动作帧列表和该动作映射；其他动作不受影响。旧图保留在包目录中，避免覆盖源素材。真实 WebView 验证导入单帧后动作仍为 Walk、刷新后保留选中帧、缩略图点击、前后帧、滑块和单次播放终点；无 JS 错误。

## 当前限制与验证状态

0.2.1 Release 构建通过；角色方案与布局别名离线测试 30 项、SDK 测试 339 项全部通过。CesiumLoader 2.4.7 的 SDK 和模组 DLL/PDB 来自同次构建。本轮未部署游戏、未验证真机渲染或原生 Animator 反射调用。以下是历史版本验证记录：Toys 测试 250 通过、1 跳过；真实 WebView 验证深色页面、弹窗、消息和窄窗口无横向溢出，并检查矩形裁剪预览与透明补边命令。14:53:16 真机启动日志确认 0.1.0 加载并成功读取 lanhaiq 包，进入蓝海晴对局，没有角色模组警告；探针运行在替换前，尚不足以证明最终渲染结果。0.1.1 已部署 DLL/PDB/JSON，新增 status.json 的图片/精灵实际应用计数，用于下一次启动核对 Sprite 创建和赋值。

Toys 图片选择支持按原资源尺寸进行固定比例矩形裁剪，或不裁剪、等比缩放并透明补边，输出透明 PNG。尺寸目录由本地原素材提取（1676 个资源），默认蓝海晴立绘 1120×1740、CharacterMap 200×200。SkinItemId=0 使用该角色首套原皮肤的槽位尺寸；指定原皮肤使用对应尺寸。未知资源使用界面明确标注的受限预设尺寸。既有图片不会自动改写，需要重新选择保存才规范化。

媒体、语音、视频、特效、战斗背景加载尚未实现。Media 字段可保存携带文件，运行时明确警告并保留原版。GIF 转换、图集导入、HUD 布局字段的应用尚未实现；HUDScale/HUDOffset 字段仅预留，不应当成可用功能。特殊 Timeline 的动作名称与短时状态需要进一步覆盖。不是完整资源替换的最终版本。

所有设置在完全退出并重启游戏后生效。尚未启用包时不修改角色。

## 路径

- 包：游戏目录/AstralParty_ModLoader/character-skins/<id>/character-skin.json
- 方案：游戏目录/AstralParty_ModLoader/character-skin-schemes/<id>/character-scheme.json
- 配置：游戏目录/AstralParty_ModLoader/mods/CharacterSkinMod/config.json
- 状态：游戏目录/AstralParty_ModLoader/mods/CharacterSkinMod/status.json
- 包内 Id 使用 A-Z/a-z/0-9/_/-，最多 80 字符。
- 所有相对路径限制在包目录；PNG/JPG 图片；单包总帧数最多 10000，每动作最多 2000。

```json
{
  "SchemaVersion": 1,
  "Id": "my-blue-skin",
  "Name": "我的蓝海晴",
  "HeroId": 114,
  "SkinItemId": 100114001,
  "Images": {"Character": "images/portrait.png", "CharacterMap": "images/pixel.png"},
  "Texts": {},
  "Animations": {"Idle": {"Frames": ["animations/idle/000.png", "animations/idle/001.png"], "DurationsMs": [], "Fps": 12, "Loop": true}},
  "SpriteMappings": {},
  "Layout": {"PivotX": 0.5, "PivotY": 0.1, "PixelsPerUnit": 100, "Scale": 1},
  "Media": {}
}
```

启用配置：
```json
{"Enabled": true, "ActiveSchemeId": "my-party", "ActivePackages": []}
```

方案示例：
```json
{
  "SchemaVersion": 1,
  "Id": "my-party",
  "Name": "我的派对方案",
  "Author": "",
  "Description": "",
  "Rules": [
    { "Id": "blue", "HeroId": 114, "TargetSkinItemId": 0, "OfficialSourceSkinItemId": 100114001, "CustomPackageId": "my-blue-skin" },
    { "Id": "fenny", "HeroId": 102, "TargetSkinItemId": 0, "OfficialSourceSkinItemId": 100102001, "CustomPackageId": "" }
  ]
}
```

Toys 会将清单写为 PascalCase，与 SDK CesiumJson 的区分大小写字段读取一致。导入第三方包须保持该结构；目录缺失或清单无效会拒绝整份启用方案并写入状态错误。
