// speedctl.h - 变速控制文件通道 (mod <-> 加载器)
//
// 为什么需要它(重要):
//   本游戏的热更程序集(经 HybridCLR 加载)在运行时**无法 P/Invoke** —— 任何
//   [DllImport] 调用都会抛 "NotSupportManaged2NativeFunctionMethod"(IL2CPP 只对
//   构建期已知的程序集生成 managed->native thunk)。实测连 kernel32 的
//   GetModuleHandleW 都调不通, 所以 mod 不可能直接调用 ap_speed_* 导出。
//   加载器与 mod 之间的原生交互一律走**文件/环境变量**(日志转发也是同一思路)。
//
// 目录: <游戏目录>\AstralParty_ModLoader\speed\   (环境变量 CESIUM_SPEED_DIR)
//   request.txt   mod -> 加载器: 期望倍率(单个十进制数, 1.0 = 关闭变速)
//   state.txt     加载器 -> mod: key=value 状态
//                   version=<加载器版本>  speed=<当前倍率>  base=<doorstop 基础倍率>
//                   active=<hook 可用 0/1>  hooks=<成功启用的 hook 数>
//
// 语义:
//   - 加载器启动时把 request.txt 重置为"当前引擎倍率", 因此上一次会话残留的请求
//     不会在下次启动时被误应用(基础倍率永远优先);
//   - 之后只处理**内容变化**: 请求非法(非数字/超范围/NaN)时忽略并记日志, 保持原倍率;
//   - 每次应用后回写 state.txt, mod 可据此显示/校验真实值;
//   - 任何失败都只记日志, 不影响加载器其余功能(加载 mod 照常)。

#pragma once

#include <string>

// 控制文件目录(供环境变量 CESIUM_SPEED_DIR 使用)。
std::wstring speedctl_dir();

// 启动监听线程(幂等)。base_speed = doorstop_config.json 的 speedhackBaseSpeed,
// 仅用于 state.txt 里的 base= 字段(记录/诊断)。不抛异常、不阻塞调用者。
void speedctl_start(const std::wstring& dir, double base_speed);
