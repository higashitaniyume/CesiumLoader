// windowtitle.h - 给游戏窗口标题追加 profile 名(多 profile 辅助)
//
// 为什么需要它:
//   同时开多个实例时, 任务栏和 Alt+Tab 里每个窗口的标题完全一样, 分不清哪个是哪个 profile。
//   加载器本来就知道本次启动用的是哪个 profile, 所以在窗口出现后把 " · <profile 名>"
//   追加到标题后面。
//
// 行为:
//   - 只在选了 profile 时生效; doorstop_config.json 的 profileWindowTitle=false 可关闭,
//     关掉只影响标题显示, 目录隔离照常;
//   - 等游戏窗口出现(优先 UnityWndClass), 之后定期检查标题 —— 游戏自己把标题重置回
//     原样时补上后缀, 不重复叠加;
//   - 全程后台线程, 不占游戏主线程; 任何失败只记日志, 不影响游戏。

#pragma once

#include <string>

namespace cesium::windowing
{

// 启动标题后缀线程(幂等)。
// profile_name 已经去掉路径(只留目录名); 空串 = 不做任何事。
void windowtitle_start(const std::wstring& profile_name);

} // namespace cesium::windowing
