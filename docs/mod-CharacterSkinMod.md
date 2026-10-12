# CharacterSkinMod 0.1.2

本地角色皮肤模组，Toys 的皮肤页提供并列的卡牌皮肤与角色皮肤 Tab。

## 已实现

- 按 HeroId 与原皮肤 ItemID 启用。SkinItemId=0 覆盖该角色所有原皮肤。
- FairyGUI 图片缓存和可见 GLoader 的资源替换：立绘、SFW、头像、HUD、准备图、窄幅图、半身图、地块标志。CharacterMap 为角色共用地址，影响该角色的倒计时和小地图头像。
- Name/Nick/Biography/Lines 角色共用文案及皮肤长短描述。没有原文案 ID 时回退并警告。
- PNG/JPG 动作帧序列、逐帧时长、FPS、循环、自定义 pivot/PPU/缩放，原精灵名 SpriteMappings。
- 0.1.2 新增 UseOriginalSpriteLayout：仅对 SpriteMappings 生效，使用被替换原帧的归一化 pivot 与 PPU，仍应用 Layout.Scale。供保持原画布尺寸的逐帧皮肤模板使用；映射跟随原动画时序，独立动作序列继续使用布局设置。
- 已制作蓝海晴默认皮肤 100114001 示例 ZIP：17 个标准图片槽位加升星插图、23 个原动作、905 张精灵帧。通过 Toys 导入/导出和 SDK CesiumJson 清单读取校验，原尺寸、锚点和文件来源记录在 source-metadata.json。示例包未自动启用，游戏中的映射布局仍待下一次启动验证。
- 保留原 Animator、事件与人物 Transform；OnUpdate 恢复原精灵，OnLateUpdate 应用自制精灵。动作由原精灵名称推断，尚未实现 Animator normalizedTime 同步；连续同动作重触发和暂停需真机核对。
- 地图人物按 owner 匹配；无 owner 的 UI 动画通过原图集名与 CharacterLabel 的精确前缀匹配，不明图集保持原版。
- Toys 新建、ZIP 导入导出、删除（移入 .deleted）、启用停用、多角色并行、图片选择、动画导入预览、文案及清单编辑。

## 新建时从游戏生成默认模板

在新建角色皮肤窗口勾选「从游戏导出所选角色的默认皮肤」，可修改热更新缓存目录。默认使用当前用户的 AppData/LocalLow/feimo/AstralParty_CN/com.unity.addressables，同时检索当前游戏的 *_Data/StreamingAssets/aa。默认模板固定对应角色默认原皮肤；不勾选继续创建空白方案。

导出需要 Python 3、UnityPy 和 Pillow；本机已验证。其他机器缺少时安装 `python -m pip install UnityPy Pillow`，当前版本未内嵌 Python 环境。提取仅访问本地文件，不联网。按缓存 catalog 的地址依赖定位 bundle，优先热更新缓存；读取 GameData_CN 中的实际 Character/Skin 配置（缺少时使用 Toys 配置），再导出图片、精灵和动画剪辑。资源未下载时说明缺失或失败，动画/图片完全未定位则不创建空模板。语音、视频与特效保留原版，Texts 留空。

窗口显示阶段进度，关闭窗口会继续在后台提取。成功后选中新包，不自动启用；失败清理半成品。source-metadata.json 记录 catalog、读取的 bundle、缺失资源与逐帧尺寸/锚点。实际通过创建服务提取蓝海晴：18 个图片入口、23 个动作、905 帧、缺失 0，来源同时包含安装目录 bundle 和 Addressables 缓存 __data。其他角色需依其本地已下载资源验证。

## Toys 动画逐帧操作

导入或保存后保留正在编辑的动作和帧；新帧数量变少时仅将当前帧限制在有效范围，不跳回 Idle。动作切换时重置为第一帧，空动作的 FPS/循环使用默认值。

每帧提供透明背景缩略图，点击选中并显示大图，可用上一帧、下一帧或滑块跳转。替换选中帧先选择 PNG/JPG，再按原帧画布裁剪或等比补边，默认不裁剪，输出透明 PNG；保留帧数、顺序、FPS、循环与逐帧时长。映射包会同步 SpriteMappings；同一原精灵有多次引用时一起更新以保持预览与游戏一致，独立动画只更新选中位置。

导入帧序列替换整个动作并移除该动作旧映射，使用独立帧序列。恢复此动作移除动作帧列表和该动作映射；其他动作不受影响。旧图保留在包目录中，避免覆盖源素材。真实 WebView 验证导入单帧后动作仍为 Walk、刷新后保留选中帧、缩略图点击、前后帧、滑块和单次播放终点；无 JS 错误。

## 当前限制与验证状态

编译通过；Toys 测试 250 通过、1 跳过；真实 WebView 验证深色页面、弹窗、消息和窄窗口无横向溢出，并检查矩形裁剪预览与透明补边命令。14:53:16 真机启动日志确认 0.1.0 加载并成功读取 lanhaiq 包，进入蓝海晴对局，没有角色模组警告；探针运行在替换前，尚不足以证明最终渲染结果。0.1.1 已部署 DLL/PDB/JSON，新增 status.json 的图片/精灵实际应用计数，用于下一次启动核对 Sprite 创建和赋值。

Toys 图片选择支持按原资源尺寸进行固定比例矩形裁剪，或不裁剪、等比缩放并透明补边，输出透明 PNG。尺寸目录由本地原素材提取（1676 个资源），默认蓝海晴立绘 1120×1740、CharacterMap 200×200。SkinItemId=0 使用该角色首套原皮肤的槽位尺寸；指定原皮肤使用对应尺寸。未知资源使用界面明确标注的受限预设尺寸。既有图片不会自动改写，需要重新选择保存才规范化。

媒体、语音、视频、特效、战斗背景加载尚未实现。Media 字段可保存携带文件，运行时明确警告并保留原版。GIF 转换、图集导入、HUD 布局字段的应用尚未实现；HUDScale/HUDOffset 字段仅预留，不应当成可用功能。特殊 Timeline 的动作名称与短时状态需要进一步覆盖。不是完整资源替换的最终版本。

所有设置在完全退出并重启游戏后生效。尚未启用包时不修改角色。

## 路径

- 包：游戏目录/AstralParty_ModLoader/character-skins/<id>/character-skin.json
- 配置：游戏目录/AstralParty_ModLoader/mods/CharacterSkinMod/config.json
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
{"Enabled": true, "ActivePackages": ["my-blue-skin"]}
```

Toys 会将清单写为 PascalCase，与 SDK CesiumJson 的区分大小写字段读取一致。导入第三方包须保持该结构；目录缺失或清单无效时模组记录一次警告并跳过。
