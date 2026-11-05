# CesiumLoader 3.3.0

## 多 profile

- **窗口标题带上 profile 名**：同时开多个实例时，游戏窗口标题会追加当前 profile 名（如 `吉星派对 · P2`），在任务栏和 Alt+Tab 里一眼区分。默认开启，可用 `profileWindowTitle` 关闭；只在配置了 `profile` 时生效，绝对路径形式的 profile 只显示目录名。
- 游戏重置窗口标题后加载器会自动补回后缀，不会重复叠加；写入标题不改变窗口大小、位置或焦点。
- AstralParty.Toys 的「游戏多开」页新增同名开关，安装或更新加载器时会把缺失的 `profileWindowTitle` 补进已有配置，注释与用户已有值保留。

---

加载器 / SDK / CLI 版本统一到 3.3.0。更新后重启游戏生效，已有模组配置与自定义值保留。
