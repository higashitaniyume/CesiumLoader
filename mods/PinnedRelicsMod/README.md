# 关键筹码置顶 PinnedRelicsMod 1.2.1

玩家持有名单内筹码后，在左侧对应玩家信息栏的星币下方横排显示游戏原生图标和品质底圈。四名玩家分别跟随各自持有状态，仅显示图片，不显示名称、层数或说明，也不更改原筹码排序及对局数据。

原独立仓库：[Aperfish/PinnedRelicsMod](https://github.com/Aperfish/PinnedRelicsMod)。

## 默认筹码与中文名配置

默认八种，按以下顺序排列：

```text
肾上腺素-高效,磨刀石,友情徽章,安全气囊,紫色飞星,金色飞星,美工刀-初级,美工刀-锋利
```

在 Toys 配置页或模组目录的 `config.json` 中修改 `pinnedRelicNames`。追加名字即可增加，删除名字即可取消；空字符串清空显示。按输入顺序排列，同种筹码只显示一次。仅在对应玩家持有时显示，不按生命值或充能条件隐藏。

例如，在默认名单后追加“星币锤”：

```json
"pinnedRelicNames": "肾上腺素-高效,磨刀石,友情徽章,安全气囊,紫色飞星,金色飞星,美工刀-初级,美工刀-锋利,星币锤"
```

这是一项字段示例，不要覆盖整份 JSON；启动器输入框只填写名字列表。
支持英文/中文逗号、顿号、分号、换行或 `|` 分隔，忽略空白并兼容常见横线和全角括号。“友情微章”可作为“友情徽章”的别名。未知名字跳过并记录日志。

完整的 90 种名称见 [支持的筹码.md](支持的筹码.md)，包括“时间怀表(破碎)”。保存后由配置热重载生效；启动器未触发重载时请重启游戏。

## 设置

| 字段 | 默认值 | 作用 |
| --- | --- | --- |
| `enabled` | `true` | 显示总开关 |
| `pinnedRelicNames` | 上述八个中文名 | 置顶名单和排列顺序 |
| `iconSize` | `30` | 图标边长，16～64 |
| `iconGap` | `3` | 图标间距，0～20 |
| `offsetX` | `0` | 相对星币数字左边缘水平偏移，-300～300，正数向右 |
| `offsetY` | `3` | 相对星币数字下边缘垂直偏移，-100～200，正数向下 |

尺寸和偏移使用游戏 UI 局部单位，对四名玩家统一生效。每行空间不足时自动等比缩小，保证横排；大量筹码会让图标变小。

1.2.0 使用 `Enabled` / `PinnedRelicNames` / `IconSize` / `IconGap` / `OffsetX` / `OffsetY`，升级后会自动把已有值迁移到新字段，自定义名单不会被重置。

## 原生图片与性能

- 使用 `RelicInfoConfigure.Icon` 获取图案，由游戏 `UI.TextureLoader` / `TextureManager` 加载；品质底圈使用 `UICom_Relic_Quality`。无需 `assets` 文件夹，不访问 Wiki 或额外网络图片。
- 图标按需创建，纹理由游戏缓存；组件释放时通过原生 loader 归还引用，不手工销毁共享纹理。
- 每 0.2 秒真实时间刷新持有状态。缓存 HUD；HUD 失效后只在场景切换后的一段时间内重新定位，避免在非对局界面反复遍历界面树。
- 每次刷新最多新建 2 个图标，切换场景或改配置后图标会陆续出现，不会在同一帧集中创建整排。
- 单个筹码的图标创建失败只跳过该图标，不影响其他玩家和其他筹码，也不会中断对局。
- 行附着到对应玩家信息组件并以星币文字为锚点，跟随头像移动、缩放和目标选择重挂接；隐藏时跟随 HUD，不接收鼠标输入。
- 场景切换、配置变更或卸载时清理模组创建的 UI。名称目录直接编入 C#，不依赖 HybridCLR 缺失的 DLL 内嵌资源读取接口。

## 安装与更新

合并并随加载器发布后，Toys 可随加载器安装此模组。独立安装时，将模组 DLL、同名 JSON 清单和配置放入 `AstralParty_ModLoader/mods/PinnedRelicsMod/`，启用后重启游戏。

保留旧 `config.json` 时，原有自定义名单不会被覆盖；如需两种新增默认美工刀，请追加 `美工刀-初级,美工刀-锋利`。旧 Wiki `assets` 目录不再使用。不依赖战斗牌点数、战斗胜率助手或加速模组。

## 构建与检查

在仓库根目录执行，使用仓库 `refs/game/` 及 SDK 项目，无需手动指定本机游戏路径：

```powershell
dotnet build mods/PinnedRelicsMod/PinnedRelicsMod.csproj -c Release -p:NuGetAudit=false
dotnet test tests/PinnedRelicsMod.Tests/PinnedRelicsMod.Tests.csproj -c Release -p:NuGetAudit=false
```

运行 `python mods/PinnedRelicsMod/tools/generate_catalog_code.py` 可由 `RelicCatalog.tsv` 重新生成 C# 名称目录。更新游戏名称时需同步 TSV 与《支持的筹码.md》。

名称、排序、持有状态与行宽逻辑可脱离 Unity 检查。小尺寸清晰度受原始素材和 UI 缩放影响。

## 许可

详见 [NOTICE.md](NOTICE.md)。这是非官方第三方模组，使用前请自行确认游戏规则。
