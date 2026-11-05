// taptaphack.h - TapTap 版绕过 (AOT 层, 与 steamhack 平行)
//
// 背景(静态分析 + 实跑确认):
//   TapTap 测试版走 TapTap SDK(taptap_api.dll + tapsdkcore.dll + AOT 程序集
//   TapSDK.Core.Runtime.dll / TapSDK.Core.Standalone.Runtime.dll / BnSdk.dll /
//   BnSdk.Runtime.dll), 没有 Steamworks。TapTap 自己的“非 TapTap 客户端启动就退出”
//   门闸长在 TapSDK.Core.Standalone.TapClientStandalone.IsLaunchedFromTapTapPC
//   (以及 TapCoreStandalone.IsLaunchedFromTapTapPC), 内部走 RunClientBridgeMethod,
//   拿不到客户端时返回 needQuitGame=true -> 游戏弹“获取游戏信息失败,请从 TapTap 客户端
//   重新启动游戏”对话框并退出。
//   这两个方法都是异步的, 签名:
//       ParamCount=0, ReturnType=System.Threading.Tasks.Task<System.Boolean>
//   绕过把它们替换成“恒返回预建好的已完成 Task<bool> = true”(构造一次, 两处复用)。
//
//   另外插一个附加保险: taptap_api.dll!TapSDK_RestartAppIfNecessary() 恒返回 0,
//   与 Steam 的 SteamAPI_RestartAppIfNecessary 同槽位。登录链 stub 预留但默认关闭。
//
// 生命周期: 与 steamhack 相同 — thread_attach 之后、wait_hybridclr 之前安装,
// 失败只记日志, 绝不阻止游戏启动。MinHook 所有权归 speedhack.cpp, 本模块不调用
// MH_Uninitialize()。

#pragma once

#include "runtime/il2cpp_safe.h"

bool taptaphack_install(const cesium_safe::il2cpp_tbl& t,
                        bool bypass_launched,
                        bool bypass_restart_check,
                        bool bypass_login);

int taptaphack_hook_count();

void taptaphack_uninstall();

namespace cesium::taptap
{
using ::taptaphack_hook_count;
using ::taptaphack_install;
using ::taptaphack_uninstall;
}
