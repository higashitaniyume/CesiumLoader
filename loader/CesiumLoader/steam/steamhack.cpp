// steamhack.cpp - Steam 绕过实现 (原理与生命周期见 steamhack.h)
//
// 九个 hook(最多), 分五个阶段, 共用一套 MinHook 生命周期 / uninstall:
//
//  阶段1 —— Steam 自杀门:
//   1) SteamManager.Awake() -> no-op
//      IL2CPP 的 MethodInfo 第一个字段就是原生代码指针(methodPointer),
//      IL2CPP 是纯 AOT(无 JIT), 直接 inline hook 该地址即可 —— 返回即"什么也不做",
//      于是 Awake() 里那条 "非Steam客户端启动, 退出游戏" 永远不会执行。
//   2) steam_api64.dll!SteamAPI_RestartAppIfNecessary -> 恒返回 0 (附加保险)
//
//  阶段2 —— Steam 大厅匹配(修"Steam 全关时点创建房间毫无反应"):
//   3) Steamworks.SteamMatchmaking.CreateLobbyAsync(System.Int32) -> 已完成的空 Task
//   4) Steamworks.SteamMatchmaking.JoinLobbyAsync(Steamworks.SteamId) -> 已完成的空 Task
//      两个处理器都走同一个通用辅助函数 make_default_completed_task(hookedMi),
//      返回值类型从**被挂钩方法自己**的 MethodInfo 推导, 不硬编码 Lobby。
//      (详细原理与实测异常见文件下半部分 "Steam 大厅匹配绕过" 一节)
//
//  阶段3 —— Nullable<Lobby>.get_HasValue 恒 false —— **已实测作废, 保留但无效**:
//   5) System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue() -> 恒返回 false
//      **实测结论**: 这个 hook 的"首次被拦截"那行在实机日志里从未出现 —— HybridCLR 解释器把
//      Nullable<T>.HasValue 当**内建指令**内联处理, 根本不走 MethodInfo->methodPointer。
//      hook 留着无害(万一将来真走 AOT 调用还能兜住), 但**它不是解法**。安装时会明确记一行说明。
//
//  方案C —— Task<T>..ctor(T) 原生 ABI 直调(主解法, config: steamBypassTaskCtorMode):
//     实测根因: il2cpp_runtime_invoke 给 Task<T>..ctor(Nullable<Lobby>) 传**值类型参数**时编组
//     不正确 —— hasValue 落成非零(证据: 探针里 il2cpp_field_get_value_object 返回**非空**装箱对象,
//     而"空 Nullable"按 .NET 语义装箱应得 NULL), 于是 await 回来后 val.HasValue 被判成 true。
//     绕过 runtime_invoke, 按 Windows x64 ABI 直调 ctor 的 methodPointer(见 pod_call_task_ctor_direct)。
//     "auto"(默认) = 先 direct, 探针校验失败自动退回 invoke 并在日志说明; 另有 "direct" / "invoke"。
//
//  方案B —— Lobby 方法无害化(安全网, config: steamBypassLobbyMethods):
//   6) Steamworks.Data.Lobby::SetPublic()            -> no-op
//   7) Steamworks.Data.Lobby::SetJoinable(System.Boolean) -> no-op
//   8) Steamworks.Data.Lobby::get_Id()               -> 恒返回 0(SteamId 零值), **仅当 Id 是属性**
//      (Id 若是字段则只记日志 —— 字段不可 hook。安装期会打印字段名列表与含 Id/Public/Join 的
//      方法名列表, 把这个结论写进日志。)
//      目的: 即使 hasValue 仍为 true, UICom_CreateRoom.cs:136/138 也不再抛 NRE,
//      第 142 行 RequestCreateC2S(..., steamLobbyId=0, ...) 照样发出。
//
//  阶段5 —— Steamworks.Data.LobbyQuery::RequestAsync() 兜底(config: steamBypassLobbyQuery):
//   9) Steamworks.Data.LobbyQuery::RequestAsync() -> 恒返回"预建好的、结果为**长度 0 的 Lobby[]**
//      的已完成 Task<Lobby[]>"。
//      实机实测 6 条 NRE, 栈顶就是本方法(退房/解散 -> OnExitRoomS2CServerCallBack;
//      被踢 -> OnRoomKickPlayerS2CServerCallBack):
//          NullReferenceException
//          GameLogic.RoomLogic.OnExitRoomS2CServerCallBack (party.protocol.ExitRoomS2C model, ...)
//          Core.Net.NetManager.Update ()
//          Steamworks.Data.LobbyQuery.RequestAsync ()          <- NRE 真正抛出点
//      **更正文档里此前标为"未实测"的那一点**: 抛点在 `await ...RequestAsync()` 上, **不是**
//      `SteamMatchmaking.LobbyList` 取值本身; 危害也不是"最坏是访问违例", 而是 **await 之后的代码
//      全部不执行**(真实功能缺失: 本地房间状态不清、被踢提示不弹、不返回房间列表页)。
//      修法见文件下半部分 "阶段5" 一节 —— 复用方案C 的**原生 ABI 直调 ctor**机制, 但泛型参数是
//      **引用类型 Lobby[]**(不涉及 Nullable 装箱), 实参是"一个长度 0 的数组对象指针"。
//
// 失败一律只记日志: 绕过没装上时游戏行为与打补丁前**完全一致**(非 Steam 启动仍会退出,
// 创建房间仍无反应), 绝不因本模块使游戏无法启动。所有安装期的解析与间接调用都走 POD + __try/__except。
//
// 日志策略(需求核心): 解析过程的每一步都写日志 —— 域内程序集数量与名单、
// 每个命中位置(assembly/namespace)、MethodInfo 与 methodPointer 地址、
// 相对 GameAssembly.dll 的偏移、参数个数、返回值/工厂解析的每个候选命中与失败原因、
// 每个 hook 的安装结果, 以及 **direct / invoke 两条路径各自的 hasValue 诊断与自己算出的结论**。
// 即使没命中也能量化定位, 下一轮只需看日志就能收敛候选。

#include "steam/steamhack.h"

#include "platform/loader.h"

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <filesystem>

#include <fmt/format.h>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <MinHook.h>

namespace fs = std::filesystem;


#include "steam/steam_diagnostics.h"
#include "steam/steamhack_core_part.cpp"
#include "steam/steamhack_matchmaking_part.cpp"
#include "steam/steamhack_lobby_part.cpp"
#include "steam/steamhack_lobby_hooks_part.cpp"
#include "steam/steamhack_entry_part.cpp"
