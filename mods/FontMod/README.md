# 系统字体 FontMod

把游戏界面字体替换成电脑上已经安装的字体。

v1.0.0 只替换了游戏旧版动态字体（默认正文与部分界面文字），带 `_TMP` 后缀的预烘焙 SDF 字体（含主力字体 `JingNanBoBoHei_TMP` 与标题/数字字体）以及只装在用户目录的字体都没有覆盖。v1.1.0 补齐了这些，并支持直接使用未安装的本地字体文件。

## 使用

1. 打开 Toys →「自定义」→「字体」。
2. 在卡片里挑一个字体（卡片预览就是该字体渲染的中文示例），点一下即可；有字重的字体（细体 / 粗体等）会在卡片下方列出可选的变体。
3. 游戏里立即生效，不用重启；点「恢复游戏默认」换回原字体。

选择结果写入 `mods/FontMod/config.json` 的 `fontFamily`（或 `fontPath`），下次启动自动套用。

## v1.1.0 覆盖范围

- **全部 `_TMP` SDF 字体**：运行时用所选字体动态构建单图集 `Dynamic SDF`（默认 4096×4096、SDFAA、`samplingPointSize=48`、`atlasPadding=5`、`boldWeight=0.15`），热替换全部 `_TMP` 字体，包括界面主力 `JingNanBoBoHei_TMP`。
- **同图集回退补字**：主字体缺字时（小篆体、像素体或繁体字库），用 `FontEngine.LoadFontFace` + `FontEngine.TryAddGlyphToTexture` 从回退字体把缺的字烘焙进同一张 SDF 图集，不再出现空白或方块。
- **旧版 `DynamicFont` 桥接**：把 `JingNanBoBoHei`、`SIMHEI`、`Impact`、`SHOWG`、`VonwaonBitmap` 等旧版动态字体桥接到同一套 SDF 图集，并兼容用户目录字体与未安装的本地字体文件；桥接时临时清零游戏 UI 遗留的 `faceDilate=1.0`、描边与阴影参数再还原，避免技能描述等文字膨胀糊成色块。已实例化的 `TextField` / `RichTextField` 会热切换，卸载时完整恢复原字体。
- **用户目录字体**：导出字体列表时自动合并 `Font.GetOSInstalledFontNames()` 与 `%LOCALAPPDATA%\Microsoft\Windows\Fonts`（优先 OpenType `nameID=16` TypographicFamily，支持 TTC 多字面），只装在用户目录的字体也会出现在 Toys 字体卡片里。
- **本地字体直读**：`fontPath` 可直接指定未安装的 `.ttf` / `.otf` / `.ttc`，`titleFontPath` 可单独为标题/数字装饰字体指定另一套字体。

## 配置

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `enabled` | `true` | 总开关。关闭后立即恢复游戏默认字体。 |
| `fontFamily` | 空 | 系统字体家族名（如 `Microsoft YaHei`，带字重时如 `Microsoft YaHei UI Light`）。留空 = 游戏默认。一般由 Toys 写入。 |
| `applyToAllFonts` | `true` | `true` = 连标题与数字装饰字体一起替换；`false` = 保留游戏原有的标题与数字装饰字体。 |
| `fallbackFonts` | 见下 | 逗号分隔的回退字体。用于同图集 SDF 补字与旧版动态字体缺字回退，按顺序取第一个包含该字符的字体。 |
| `checkCoverage` | `true` | 只在 `bridgeDynamicToSdf=false`（v1.0 旧版路径）时生效：缺字过多时保持游戏默认字体，避免整屏方块。 |
| `fontPath` | 空 | 直接指定本地 `.ttf` / `.otf` / `.ttc` 文件路径，无需安装到系统；填了此项优先于 `fontFamily`。 |
| `fontStyle` | 空 | 可选字重名（`Regular`、`Bold`、`Light` 等），一般留空自动识别。 |
| `titleFontPath` | 空 | 可选：标题/数字装饰字体单独用另一套字体文件，留空则与正文共用主字体。 |
| `replaceTmpFonts` | `true` | 动态生成 SDF 图集替换全部 `_TMP` 字体（含 `JingNanBoBoHei_TMP`）。 |
| `bridgeDynamicToSdf` | `true` | 把旧版 `DynamicFont` 也桥接到动态 SDF 图集；`false` = 退回 v1.0 的旧版替换路径。 |
| `replaceSceneTmpText` | `true` | 是否顺带替换场景里的原生 3D `TMPro.TMP_Text`。 |
| `atlasSize` | `4096` | 动态 SDF 图集宽高，1024~8192。单张 Alpha8 约 16MB，可容纳 5000+ 汉字。 |
| `samplingPointSize` | `48` | SDF 采样字号，16~96。兼顾锐度与单图集容量。 |
| `atlasPadding` | `5` | SDF 扩散边距，2~16。越小描边越细、字形主体越清晰。 |
| `tmpFontScale` | `1.0` | SDF 字体视觉缩放微调，0.5~2.0（越界自动回到 1.0）。 |
| `normalWeight` | `0.0` | 常规文字 SDF 字重微调，-0.5~0.5。粗黑类字体偏厚时可调成 `-0.05` 之类。 |
| `boldWeight` | `0.15` | 粗体 SDF 加粗幅度，0.0~1.0。设为 `0.0` 可关闭粗体膨胀。 |
| `warmupGameCharset` | `true` | 替换时先把游戏常用汉字表预烘焙进动态图集并补齐缺字，避免首次显示时卡顿或空白。 |

默认回退列表：`Microsoft YaHei,Microsoft YaHei UI,SimHei,SimSun,SimSun-ExtB,Yu Gothic,Meiryo,Microsoft JhengHei,Segoe UI Symbol`。

## 说明

- 字体必须是系统里已安装的字体，或者用 `fontPath` / `titleFontPath` 直接指定文件；安装新字体后需要重启游戏才能在列表里看到。字体列表按游戏能识别的系统字体名 + 用户目录字体合并列出；若配置里的名字游戏无法识别（例如手改配置写错名字），模组会保持游戏默认字体并在日志里写明原因，而不是静默换成回退字体。
- 变体能否在游戏内生效，取决于系统有没有把该字重注册成独立字体名（例如 `Microsoft YaHei UI Light`）。没有独立名字的字重不会出现在卡片里，避免选了却回退成常规体。
- 加载器自绘的浮层面板（如 F8 更新提示、PVE 面板）会跟随所选字体。
- 单图集容量约 5000+ 汉字：正常中文游戏文案够用；若所选字体字符极多（例如超大字库）且开启 `warmupGameCharset`，超出图集容量的部分会在使用时按需补字，必要时可把 `atlasSize` 调大到 `8192`。
- 玩家名等玩家自己输入的内容不在游戏字符集里，仍可能遇到字体没有的字；这类缺字同样会尝试用 `fallbackFonts` 补进同一张图集。
- 不同字体的字宽不同，个别定宽布局（按钮、姓名板、排行榜）可能出现换行或轻微位移，换回默认字体即可恢复。
- 切语言或重新登录后会自动重新套用。
- 需要退回 v1.0.0 行为时，把 `replaceTmpFonts`、`bridgeDynamicToSdf`、`replaceSceneTmpText` 都设为 `false` 即可：只替换旧版动态字体，`_TMP` 字体保持原样。