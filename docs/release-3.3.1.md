# CesiumLoader 3.3.1

## TapTap 启动绕过

新增 TapTap PC 独立客户端检测绕过支持，可直接启动游戏主程序而无需拉起 TapTap 客户端：

- **`tapTapBypassEnabled`**（默认 `false`）：主开关。拦截 `TapClientStandalone` 与 `TapCoreStandalone` 的 `IsLaunchedFromTapTapPC` 检查并返回已就绪的成功结果。
- **`tapTapBypassRestartCheck`**（默认 `false`）：为 `taptap_api.dll` 中的 `TapSDK_RestartAppIfNecessary` 挂载桩函数，恒返回 0，防止进程拉起外部客户端并退出。
- **`tapTapBypassLogin`**（默认 `false`）：为 TapSDK 相关鉴权 API（`TapSDK_Init`、`TapUser_AsyncAuthorize`、`TapSDK_GetClientID`、`TapUser_GetOpenID`）提供安全返回的桩函数。

---

加载器 / SDK / CLI 版本统一到 3.3.1。更新后重启游戏生效，已有模组配置与自定义值保留。
