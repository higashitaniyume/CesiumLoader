# CesiumLoader 2.9.0

## 调试 Shell

控制台里的 `cesium>` 提示符现在按常见 shell 的方式编辑：

- `←` / `→` 移动光标，`Ctrl+←` / `Ctrl+→` 按词移动；`Home` / `End`（或 `Ctrl+A` / `Ctrl+E`）跳到行首 / 行尾。
- `Backspace` / `Delete` 删光标前 / 光标处的字符；`Ctrl+Backspace` / `Ctrl+Delete` 按词删除。
- `Ctrl+W` 删前一个词，`Ctrl+K` 删到行尾，`Ctrl+U` 删到行首，`Ctrl+Y` 粘回刚删掉的内容。
- `↑` / `↓` 翻历史命令；翻历史前没提交的内容会保留，翻回最新时恢复。
- 命令较长、输入折行时，光标仍然停在正确的字符位置。

## Tab 补全

- 第一个词补全命令名：`statu` + `Tab` → `status`。
- `help` 后面补全命令名。
- `get` / `set` / `ls` 后面补全配置键：`get loader.consoleT` + `Tab` → `get loader.consoleTopmost`。
- 只有一个候选时直接补齐并补一个空格；有多个候选时先补公共前缀，再按 `Tab` 在下方列出全部候选。
- 引号里的内容不补全。

## 控制台日志

- 模组日志转发到控制台时按级别着色：`DBG` 暗灰、`INF` 默认灰白、`WRN` 黄色、`ERR` / `FTL` 红色；时间戳与级别保持暗灰。
- 日志文件内容不变，仍是纯文本。

## 发布包

- 加载器包新增 `CesiumLoader-NOTICE.txt`（版权、许可与免责声明）和 `CesiumLoader-LICENSE.txt`（AGPL-3.0 全文）。
- 两个文件会同时出现在游戏 exe 目录（`version.dll` 同级）和 `AstralParty_ModLoader` 目录；安装或使用即表示同意该许可。

加载器 / SDK / CLI 版本统一到 2.9.0。更新后重启游戏生效，已有模组配置与自定义值保留。
