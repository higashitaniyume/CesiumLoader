# 角色皮肤（CharacterSkinMod）

本地角色皮肤模组：用自定义图片、文案与二维精灵动画替换官方角色的立绘、头像、HUD、地块标志和人物动作。支持多角色、多目标、官方来源与自制包混合。Toys 的皮肤页提供并列的卡牌皮肤与角色皮肤两个 Tab。

## 启用与生效

配置位于游戏目录 `AstralParty_ModLoader/mods/CharacterSkinMod/config.json`：

```json
{"Enabled": true, "ActiveSchemeId": "my-party", "ActivePackages": []}
```

- `ActiveSchemeId` 指定启用哪份多角色方案；留空时按旧版 `ActivePackages` 里的包逐个按包自身 `HeroId`/`SkinItemId` 启用。
- 所有设置需要**完全退出并重启游戏**后生效，不支持本轮热更。
- 未启用皮肤包时不修改任何角色。

## 多角色皮肤方案

方案位于 `AstralParty_ModLoader/character-skin-schemes/<id>/character-scheme.json`，每条规则包含 `Id / HeroId / TargetSkinItemId / OfficialSourceSkinItemId / CustomPackageId`：

- `TargetSkinItemId` 为 0 表示该角色所有原皮肤；同角色的精确目标优先于通配。
- `OfficialSourceSkinItemId` 指定从该角色的另一套官方皮肤取视觉与动画；须与目标同角色。
- `CustomPackageId` 指向 `character-skins/<id>`，可留空表示只换官方来源。
- 所有来源都从修改前的配置快照读取，A→B、B→C 这类链式规则不会互相污染。
- 目标的 ItemID、Index、文案 ID、语音、视频、特效、特殊 Timeline 与战斗资源配置保持原版；复制的是立绘/SFW/HUD/头像/准备图/窄幅图/地块标志/升星图、Bust/SfwBust、SkinScale 与 Actions。
- 原皮肤对象保持原 identity，`StandingPaintingDict` 与 `StandingPaintings` 两份对象都原地更新，已存在的 HeroCardLogic / HeroCardData / RoomPlayer 引用继续有效，同一角色不同目标可以使用不同来源。

## 图片与文案替换

- 图片槽：`Character`、`SfwCharacter`、`InGameCharacter`、`InGameCharacterSFW`、`ProfilePhoto`、`RolePhoto`、`SfwRolePhoto`、`CharacterReady`、`SfwCharacterReady`、`CharacterThin`、`SfwCharacterThin`、`LandIcon`、`Bust0/1`、`SfwBust0/1`；`asset:<资源名>` 可直接替换任意全局图片地址。
- `CharacterMap` 为角色共用地址，会同时影响该角色的操作倒计时头像和小地图头像。
- 文案槽：`Name`、`Nick`、`Biography`、`Lines`（角色共用）与 `SkinLong`、`SkinShort`（皮肤描述），写入五种语言。没有有效文案 ID 时拒绝应用并写入失败状态。
- 每个目标的标准图片槽使用目标独有的私有地址，包含 Bust/SfwBust 列表地址，不同目标互不覆盖；私有地址继承原图的立绘偏移、HUD 偏移/缩放/翻转。
- `CharacterMap` 与角色共用文案、`asset:` 全局地址在多个角色间一致时允许复用，不一致时拒绝整份方案；运行时还会按实际图片地址与本地化 ID 复查跨角色冲突。
- FairyGUI 图片缓存与可见 GLoader 都按新地址重新取图，未使用官方来源时不会改动原资源。

## 二维精灵动画

清单有两条数据路径，可以同时存在：

- `SpriteMappings`：原精灵名 → 自定义帧，**逐帧映射**。原精灵有映射时按游戏当前显示的帧替换，帧序、动作切换、房间速度、回放倍速、走路速度倍率、时停暂停全部跟随游戏自身动画，模组不另开时钟。
- `Animations`：整段动作帧序列，带 `Fps`、`Loop` 与可选逐帧 `DurationsMs`。只在该动作没有逐帧映射时使用（Toys 导入帧序列替换整个动作时会移除该动作旧映射）。序列按游戏当前动画速率推进：非循环动作播完停在末帧，循环动作重复播放，游戏暂停/时停时同样冻结，同一动作被游戏重播时从头开始。

找不到内容的回退顺序：

1. 当前原帧有映射 → 用该帧；
2. 动作带变体后缀（`Idle-02`、`Talent-03`、`Fight-attack2` 等）→ 按同一帧号取基础动作（`Idle`、`Talent`、`Fight-attack`）的自定义帧；
3. 否则播基础动作的整段自定义序列；
4. 否则播自定义 `Idle` 序列；
5. 否则沿用最近一张自定义帧，并在 `status.json` 的警告里列出该动作名。

位置锚点：`UseOriginalSpriteLayout` 为真时，每张自定义帧使用**它替换的那张原帧**的归一化 pivot 与 PPU，帧信息取自包内 `source-metadata.json`／`legacy-animation-source.json`；`replacements/<guid>.png` 这类文件名不可识别的帧退回它所替换原精灵名的记录，用户导入的独立序列继续使用包 `Layout`。`UseOriginalSpriteLayout` 为假的包整体使用 `Layout`。

制作建议：自定义帧画布与原帧一致（Toys 的裁剪/补边已按原尺寸输出），用 FPS 或逐帧时长让总时长接近原动作；原动画引用不到的长序列用 `Animations`。

人物接管范围：每帧 LateUpdate 发现并接管地图人物、战斗人物与无 owner 的 UI 像素动画。战斗人物按 `playerData` 的 HeroId/ItemID 匹配并只替换本体 SpriteRenderer，保留 Timeline、位移、特效与游戏事件；UI 人物优先匹配唯一原图集，其次匹配原控制器，不明或歧义时保持原版并写入警告。

## 官方来源动画

官方人物的标准动画实际使用来源 `CharacterAsset.animationController`。模组按来源 `GetCharacterLabel()+平台标签` 异步预载官方 CharacterAsset，再按地图人物 owner 的 HeroId 与实际目标 ItemID 精确替换该人物的 Animator 控制器；保留目标配置的 CharacterLabel 与游戏按 heroId 的动画缓存，不修改角色 ID 或全局资源缓存。替换时保存控制器并保持已有状态 hash、normalizedTime 与 float/int/bool 参数，继续由游戏 CharacterAnimator 触发事件；卸载时按所有权恢复控制器并保留当前 normalizedTime。纯自定义方案不预载额外官方动画。

## 应用、失败与恢复

配置与自定义缓存事务式应用：部分应用失败会整份回滚并写入失败状态。恢复只修改仍等于本模组 Applied 值的字段、列表、控制器与缓存，保留其它组件之后的改动；卸载/失败先解除已替换 GLoader 的自定义引用、恢复原缓存并重新加载原地址，再销毁自定义纹理与释放预载官方资源。

## 状态文件

`AstralParty_ModLoader/mods/CharacterSkinMod/status.json`：

- `State`：`Disabled` / `Pending` / `Loading` / `Applied` / `Failed` / `Unloaded`；`Error` 为失败原因。
- 方案/规则/目标/包/图片/布局别名/角色/精灵数量，以及图片、精灵、控制器的实际应用计数。
- `WarningMessages`：缺少自定义内容的动作、原帧锚点与包内模板不一致的帧、共享字段冲突等。
- `PositionSamples`：每约 2 秒给出每只角色的 `OffsetY`（精灵相对 transform 的纵向偏移范围）、`PositionY`（transform 自身纵向位置范围）与 `Restored`（被还原成原图的次数），用于判断画面位移来自锚点/尺寸、游戏移动还是贴图内容。

## 新建时从游戏导出官方皮肤模板

新建角色皮肤时勾选「从游戏导出官方皮肤素材」，并选择该人物已有的官方皮肤，可修改热更新缓存目录；默认使用当前用户的 AppData/LocalLow/feimo/AstralParty_CN/com.unity.addressables，同时检索当前游戏 `*_Data/StreamingAssets/aa`。导出来源与方案替换目标相互独立，可用皮肤 B 的素材替换原皮肤 A；所选资源须已下载到本地，来源不存在或属于其他人物时明确报错，不退回默认皮肤。不勾选则创建空白自定义包。

导出使用内置 C# 提取器，仅访问本地文件：读取序列化对象、解码纹理并按原 Sprite 顶点/UV 还原透明 PNG，同时导出精灵锚点与动画剪辑到 `source-metadata.json`（含 catalog、读取的 bundle、缺失资源、逐帧尺寸/锚点）。资源未下载时说明缺失，动画/图片完全未定位不会创建空模板；语音、视频与特效保留原版。

任务提交后进入后台队列并按加入顺序执行，任务面板显示阶段、百分比与进度条，等待中的任务可取消排队、运行中的可终止；成功后选中新包但不自动启用，失败清理半成品。

## 导出当前动作为 GIF

二维精灵动画工具栏「导出当前动作 GIF」按当前帧顺序、逐帧时长（无逐帧时长时按界面 FPS）与循环设置导出：保留透明背景、每帧清背景避免残影、不同尺寸按最大画布底部居中对齐，编码为 256 色单层透明 GIF。写入临时文件后原子替换目标，失败或取消保留原文件。

## 动画逐帧编辑

- 导入或保存后保留正在编辑的动作与帧；新帧数量变少时只把当前帧限制在有效范围。动作切换时回到第一帧，空动作的 FPS/循环使用默认值。
- 每帧提供透明缩略图，可逐帧跳转或滑块跳转；替换选中帧时先选 PNG/JPG，再按原帧画布裁剪或等比补边（默认不裁剪，输出透明 PNG），保留帧数、顺序、FPS、循环与逐帧时长。
- 映射包会同步 `SpriteMappings`，同一原精灵被多次引用时一起更新；独立动画只更新选中位置。
- 导入帧序列会替换整个动作并移除该动作旧映射；「恢复此动作」移除动作帧列表与该动作映射，其他动作不受影响。旧图保留在包目录，不会覆盖源素材。

## 当前限制

- 媒体、语音、视频、特效与战斗背景加载未实现：`Media` 字段可以保存携带文件，运行时提示并保留原版。
- `HUDScale` / `HUDOffsetX` / `HUDOffsetY` 字段为预留，尚不生效；HUD 实际布局由图片私有地址继承原图。
- 特殊 Timeline 的动作名称与短时状态仍需按角色补齐。
- 用户导入的独立动画序列使用包 `Layout` 确定锚点，与逐帧映射的原帧锚点不同。
- 尚未启用皮肤包时不修改角色。

## 路径

- 包：`游戏目录/AstralParty_ModLoader/character-skins/<id>/character-skin.json`
- 方案：`游戏目录/AstralParty_ModLoader/character-skin-schemes/<id>/character-scheme.json`
- 配置：`游戏目录/AstralParty_ModLoader/mods/CharacterSkinMod/config.json`
- 状态：`游戏目录/AstralParty_ModLoader/mods/CharacterSkinMod/status.json`
- 包内 Id 使用 `A-Z a-z 0-9 _ -`，最多 80 字符；所有相对路径限制在包目录；图片为 PNG/JPG；单包总帧数最多 10000，每动作最多 2000。

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
  "UseOriginalSpriteLayout": true,
  "Layout": {"PivotX": 0.5, "PivotY": 0.1, "PixelsPerUnit": 100, "Scale": 1, "HudScale": 1, "HudOffsetX": 0, "HudOffsetY": 0},
  "Media": {}
}
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
    { "Id": "other", "HeroId": 102, "TargetSkinItemId": 0, "OfficialSourceSkinItemId": 100102001, "CustomPackageId": "" }
  ]
}
```

Toys 会将清单写为 PascalCase，与 SDK CesiumJson 的区分大小写字段读取一致；导入第三方包须保持该结构，目录缺失或清单无效会拒绝整份启用方案并写入状态错误。
