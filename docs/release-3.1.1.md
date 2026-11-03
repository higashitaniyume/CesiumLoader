# CesiumLoader 3.1.1

## 新增内置模组：角色半透明助手（CharacterTransparencyMod）

- 按快捷键 `H`（默认）使游戏中的角色半透明化（默认 36% 不透明度），方便在角色堆叠或处于关键地块时清晰查看下方地块图标、事件与怪物信息。
- **触发模式开关**：提供 `holdMode` 开关，支持「长按切换」（按住半透明、松开恢复）与「点击切换」（按一次开启、再按一次恢复），默认为长按模式。
- **自定义不透明度**：支持在配置中自定义目标不透明度（`opacity`，范围 0.0 ~ 1.0 或 0 ~ 100%）。
- **技能兼容与透明度还原**：松开或切回时精准还原角色原本的透明度，与角色隐身技能（如邦妮、修女）互不冲突。
- **输入保护**：处于聊天输入框或文本聚焦状态时不响应热键。
- 随包配置带完整多语言注释（简中、繁中、英文、日文），Toys 配置界面可直接捕获修改快捷键与参数。

## SDK 路径服务（CesiumLoader.SDK.IO.Path / SdkPath）

- **统一路径发现**：提供 `GameDirectory`、`LoaderDirectory`、`ModsDirectory`、`ConfigsDirectory`、`LogsDirectory`、`SkinsDirectory`、`AudioDirectory`、`SpeedDirectory`、`WindowDirectory` 等属性，由加载器与环境变量直接协商，告别易失效的反射与相对路径猜测。
- **便捷定位与子路径拼接**：提供 `GetGamePath(...)`、`GetLoaderPath(...)`、`GetModDirectory(...)`、`GetModPath(...)`、`GetModDataPath(...)`、`GetModConfigPath(...)`、`GetSkinPath(...)`、`GetAudioPath(...)` 等 API，支持一键拼接模组目录、数据目录与资源文件。
- **免冲突门面**：
  - `CesiumLoader.SDK.IO.Path`：包含全套路径属性、查找方法与常用 `System.IO.Path` 转发（如 `Combine`、`GetFileName` 等），模组无需额外引用 `System.IO`。
  - `CesiumLoader.SDK.SdkPath` 与 `CesiumLoader.SDK.IO.Paths`：提供根命名空间和复数别名，在同时使用 `System.IO` 时彻底避免类名歧义。
- **目录安全保障**：内置 `EnsureDirectoryExists(...)` 与 `SanitizeFileName(...)`。

---

加载器 / SDK / CLI 版本统一到 3.1.1。更新后重启游戏生效，已有模组配置与自定义值保留。
