// speedhack.h - 变速引擎 (inline hook 系统时间函数)
//
// 原理: 与 CheatEngine/speedhack-rs 相同 —— inline hook 4 个时间 API,
// 按倍率缩放返回值, 让游戏感知的时间流逝变快/变慢:
//   GetTickCount / GetTickCount64 / timeGetTime / QueryPerformanceCounter
//
// 生命周期: speedhack_init() 在 DllMain(DLL_PROCESS_ATTACH) 调用(越早越好,
//           游戏逻辑普遍基于这些 API 计时); speedhack_shutdown() 在 DETACH 调用。
// 线程安全: SetSpeed 与 hook 回调并发安全(原子读倍率)。
// 时间连续性: 倍率切换时自动重设时间零点, 保证虚拟时间不跳变(无负回绕)。

#pragma once

// 安装 hook(返回是否成功; 失败不影响加载器其余功能)
bool speedhack_init();

// 卸载 hook(进程退出时)
void speedhack_shutdown();

// 设置倍率: 1.0 = 正常, 2.0 = 2 倍速, 0.5 = 半速。返回是否成功。
bool speedhack_set_speed(double speed);

// 当前倍率。
double speedhack_get_speed();

// 是否已安装 hook。
bool speedhack_active();
