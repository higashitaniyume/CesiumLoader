// steamhack.h - Steam 绕过 (原生层补丁, 不改动 steam_api64 的认证语义)
//
// 背景(实测 + 静态分析确认):
//   国服客户端启动时, AOT 类 SteamManager(全局命名空间, **不在热更程序集里**,
//   即位于 GameAssembly.dll 的 AOT 镜像中)的 Awake() 会检测 Steam 客户端是否可用;
//   不可用时打印
//       [ERROR] [SteamManager] 非Steam客户端启动, 退出游戏
//   然后退出游戏。调用栈:
//       SteamManager.Awake() <- SteamPlatform.OnApplication()
//                           <- BnSdk.BN_Platform.OnApplication(...)
//
// 策略: **五阶段**
//
//   阶段1 —— Steam 自杀门(4 个 hook 里的前 2 个):
//     1.1 把 SteamManager.Awake() 换成 no-op;
//     1.2 附加保险: 让 steam_api64.dll 的 SteamAPI_RestartAppIfNecessary 恒返回 0。
//     不伪造 Steam 身份, 不 hook / 不替换 steam_api64.dll 的认证语义;
//     IL2CPP 是纯 AOT(无 JIT), 没有运行期重编译覆盖, patch 掉原生代码是安全的。
//
//   阶段2 —— Steam 大厅匹配(后 2 个 hook), 修**"Steam 全关时点创建房间毫无反应"**:
//     实测异常(BnSdk 错误上报, 游戏不崩, 只是这个 async 异常把建房流程整个吃掉):
//         NullReferenceException
//         UI.UICom_CreateRoom.RequestCreateRoom ()
//         FairyGUI.EventBridge.CallInternal (...)
//         FairyGUI.Stage.HandleMouseEvents ()
//         Steamworks.SteamMatchmaking.CreateLobbyAsync (System.Int32 maxMembers)
//         --- End of stack trace from previous location where exception was thrown ---
//     即: 真实 CreateLobbyAsync 在 Steam 未初始化时自己抛 NRE(异常在 await 处被重新抛出)。
//     修法: 挂钩 CreateLobbyAsync / JoinLobbyAsync, 直接返回一个**已完成且结果为 null**
//     的 Task<Lobby?>。游戏侧(UICom_CreateRoom.cs:131-142):
//         ulong steamLobbyId = 0uL;
//         Lobby? val = await SteamMatchmaking.CreateLobbyAsync(4);
//         if (val.HasValue) { ...; steamLobbyId = ...; }     // 结果为空 -> 整段跳过
//         ...RequestCreateC2S(..., steamLobbyId, ...);        // LobbyId=0 = "无 Steam 大厅"
//     即只有 val.HasValue 时才覆盖 LobbyId, 返回空结果 -> 协议里带 0 -> 服务端正常建房。
//     返回类型**从被挂钩方法自己的 MethodInfo 推导**, 不硬编码 Lobby。
//
//   阶段3 —— Nullable<Lobby>.get_HasValue 恒 false(第 5 个 hook) —— **已实测作废, 保留但无效**:
//     点"创建房间"后异常从 CreateLobbyAsync **前进到了** Steamworks.Data.Lobby.SetPublic():
//         NullReferenceException
//         UI.UICom_CreateRoom.RequestCreateRoom ()
//         Steamworks.Data.Lobby.SetPublic ()
//         --- End of stack trace from previous location where exception was thrown ---
//     即 val.HasValue 被判成了 true(照 UICom_CreateRoom.cs:133 进了 if 块, 到 136 行炸)。
//     实测证据: 安装期探针里 get_Result() 返回的是**非空**装箱对象, 而"空 Nullable"装箱本应得
//     NULL(il2cpp_value_box 在同样的输入下确实返回了 NULL) —— 说明 il2cpp_runtime_invoke 把
//     Nullable<Lobby> 参数编组进 Task<T>..ctor(T) 时 hasValue 落成了非零(编组差异, 不是逻辑错)。
//     原本的想法: val.HasValue 会真实 call 到 AOT 方法 Nullable`1<Lobby>::get_HasValue(), 把它
//     hook 成恒 false 即可。**实测否定了这条路线**: 实机日志里这个 hook 的"首次被拦截"那行
//     **从未出现** —— HybridCLR 解释器把 Nullable<T>.HasValue 当**内建指令**内联处理, 不走
//     MethodInfo->methodPointer。所以 hook 留着无害, 但**它不是解法**。
//
//   方案C(主) —— 绕过 runtime_invoke, 按**原生 ABI** 直调 Task<T>..ctor(T):
//     根因: il2cpp_runtime_invoke 把 Nullable<Lobby>(值类型)参数编组进 Task<T>..ctor(T) 时
//     hasValue 落成了非零(安装期探针: il2cpp_field_get_value_object 返回**非空**装箱对象,
//     而"空 Nullable"按 .NET 语义装箱应得 NULL)。不再试图用 runtime_invoke 修它, 改为:
//         typedef void (*CtorFn)(void* thisObj, void* pArg);
//         ((CtorFn)method_pointer_of(ctor))(taskObj, 全零 256B 栈缓冲区);
//     依据 Windows x64 ABI: >8 字节的结构体参数按**引用**传递; IL2CPP 的 AOT 代码是 MSVC 编译的
//     C++, 遵循同一套 ABI; runtime_invoke 才是那个多此一举、会把 Nullable 编组坏的中间层。
//     由 config steamBypassTaskCtorMode 控制: "auto"(默认, direct 探针失败自动退回 invoke) /
//     "direct" / "invoke"。两条路径各自打印 hasValue 诊断, 日志里能明确看出 direct 是否
//     把 hasValue 变成了 false。
//
//   方案B(安全网) —— Steamworks.Data.Lobby 上会炸的方法挂成无害:
//     Lobby.SetPublic() -> no-op; Lobby.SetJoinable(bool) -> no-op; Lobby.Id 若是**属性**则
//     get_Id() 恒返回 0(是**字段**则只记日志, 不硬来)。这三个都是**非泛型值类型实例方法**,
//     不存在共享泛型问题, 被解释执行时会真实走 AOT 入口(与上面阶段3 的情况不同)。
//     目的: 即使方案C 的 ABI 假设不成立、hasValue 仍是 true, SetPublic/SetJoinable 也不再抛异常,
//     UICom_CreateRoom.cs:142 的 RequestCreateC2S(..., steamLobbyId=0, ...) 照样执行。
//
//   阶段5 —— Steamworks.Data.LobbyQuery::RequestAsync() 兜底(第 6 个 hook), 修
//     **"退房/解散/被踢后本地房间状态不清、被踢提示不弹、不返回房间列表页"**:
//     实机实测 6 条 NRE, 栈顶就是本方法:
//         NullReferenceException
//         GameLogic.RoomLogic.OnExitRoomS2CServerCallBack (party.protocol.ExitRoomS2C model, ...)
//         Core.Net.NetManager.Update ()
//         Steamworks.Data.LobbyQuery.RequestAsync ()          <- NRE 真正抛出点
//     **更正此前文档标为"未实测"的那一点**: NRE 抛在 `await ...RequestAsync()` 上, **不是**
//     `SteamMatchmaking.LobbyList` 取值本身; 危害也不是"最坏是访问违例", 而是 **await 之后的代码
//     全部不执行**(真实功能缺失)。游戏侧 RoomLogic.cs:381-401(退房/解散)与 :545-573(被踢)同构:
//         Lobby[] array = await ((LobbyQuery)(ref lobbyList)).RequestAsync();  // :384 <- 这里抛
//         if (array != null && array.Length > 0) { ...Leave()... }             // 长度 0 -> 跳过
//         roomController.UpdateRoomByExit(...);                                // :398 <- 之前被跳过
//         if (self || model.Dissolve) ClearRoomInfo();                         // :401 <- 之前被跳过
//     修法: 挂钩 RequestAsync() -> **恒返回一个预建好的、结果为长度 0 的 Lobby[] 的已完成
//     Task<Lobby[]>**。于是 `array.Length > 0` 为 false -> 跳过 Steam 大厅清理 -> :398/:401/:567-573
//     正常执行。影响面为零: 全仓 grep 确认全游戏**只有这 2 处**调用(RoomLogic.cs:384 与 :554)。
//     实现要点见 steamhack.cpp 的 "阶段5" 一节 —— 复用方案C 的**原生 ABI 直调 ctor**机制, 但实参是
//     "长度 0 的 Lobby[] 对象指针"(引用类型泛型参数, 不涉及 Nullable 装箱)。
//
// 生命周期: steamhack_install() 由 loader.cpp 的 boot_thread 在
//   g_il2cpp.thread_attach(domain) 之后、wait_hybridclr() 之前调用 ——
//   此时 IL2CPP 已就绪、AOT 镜像已加载(且热更程序集还没进来, 枚举到的就是 AOT 镜像),
//   而 AOT 的 Awake() 要到进程启动约 7s 后才执行, 窗口充裕。
//   **安装失败只记日志, 绝不阻止游戏启动**。
//
// MinHook 所有权: MinHook 是**进程级单例**, speedhack.cpp 已经初始化过它。
//   本模块把 MH_ERROR_ALREADY_INITIALIZED 当成成功, 且**绝不调用 MH_Uninitialize()**,
//   只在 steamhack_uninstall() 里摘掉自己创建的那几个 hook。
//
// 与 speedhack/speedctl/mod 加载流程互不影响: 唯一的耦合点是 loader.cpp 里的一次调用。

#pragma once

#include "il2cpp_safe.h"   // cesium_safe::il2cpp_tbl (= 任务描述里的 Il2CppFnTable)

#include <string>          // steamhack_install 的 task_ctor_mode 参数

// 安装 Steam 绕过。t = 已填充好的 IL2CPP 函数表(loader.cpp 的 g_safe_il2cpp)。
//   bypass_awake            = 阶段1: 是否把 SteamManager.Awake() 换成 no-op
//                             (config: steamBypassEnabled, 默认 true)。
//   bypass_restart_check    = 阶段1: 是否加装 SteamAPI_RestartAppIfNecessary 保险
//                             (config: steamBypassRestartCheck, 默认 true)。
//                             仅在 bypass_awake 为 true 时有意义(否则自杀门本就没绕过)。
//   bypass_matchmaking      = 阶段2: 是否挂钩 SteamMatchmaking.CreateLobbyAsync /
//                             JoinLobbyAsync 使其返回"已完成的空 Task"
//                             (config: steamBypassMatchmaking, 默认 true)。
//                             **只受这一个开关控制**, 与阶段1 的两个开关互不影响。
//   bypass_lobby_hasvalue   = 阶段3: 是否挂钩 System.Nullable`1<Steamworks.Data.Lobby>::
//                             get_HasValue() 使其恒返回 false
//                             (config: steamBypassLobbyHasValue, 默认 true)。
//                             **实测作废(保留但无效)**: 实机日志里这个 hook 的"首次被拦截"那行
//                             从未出现 —— HybridCLR 解释器把 Nullable<T>.HasValue 当**内建指令**
//                             内联处理, 根本不走 MethodInfo->methodPointer。hook 可以留着(无害),
//                             但它**不是**解法, 真正的解法是 task_ctor_mode(方案C)+ 方案B。
//                             仅在 bypass_matchmaking 为 true 时有意义(阶段2 没装就没有
//                             Nullable<Lobby> 上下文可解析)。**只受这一个开关控制**。
//   task_ctor_mode         = 方案C(config: steamBypassTaskCtorMode, 默认 "auto"):
//                             Task<T>..ctor(T) 的调用方式 —— "auto"(先 direct, 校验失败自动
//                             退回 invoke) / "direct"(只用原生 ABI 直调) / "invoke"(只用
//                             il2cpp_runtime_invoke, 旧行为)。非法值按 "auto" 并记日志。
//                             仅在 bypass_matchmaking 为 true 时有意义。
//   bypass_lobby_methods    = 方案B 备用安全网(config: steamBypassLobbyMethods, **默认 false**):
//                             把 Steamworks.Data.Lobby.SetPublic()/SetJoinable(bool) 挂成 no-op,
//                             并在 Id 是**属性**时把 get_Id() 挂成恒返回 0(是**字段**则只记日志,
//                             不硬来)。目的: 即使 hasValue 仍被判成 true, 这一段也不再抛
//                             NullReferenceException, RequestCreateC2S 照样发出。
//                             **默认 false, 不要改回 true**: 实机证明 true 会让游戏启动早期崩溃
//                             (0xC0000005 / GameAssembly.dll)。现在也不需要它 —— 方案C 已把 Task
//                             结果构造成真正的空 Nullable(HasValue=false), SetPublic/SetJoinable
//                             根本不会被调用。它只是"hasValue 若又变 true"的备用安全网,
//                             启用前需自行实机验证。详见 config.h。
//                             仅在 bypass_matchmaking 为 true 时有意义。**只受这一个开关控制**。
//   bypass_lobby_query      = 阶段5(config: steamBypassLobbyQuery, **默认 true**):
//                             是否挂钩 AOT 方法 Steamworks.Data.LobbyQuery::RequestAsync(),
//                             使其恒返回"预建好的、结果为长度 0 的 Lobby[] 的已完成 Task<Lobby[]>"。
//                             修"退房/解散/被踢后本地房间状态不清、被踢提示不弹、不返回房间列表页"
//                             —— 实测 6 条 NRE 抛在这个 await 上, 让 RoomLogic.cs:398/401 与
//                             :567-573 全部被跳过(详见文件头"阶段5"一节)。
//                             **只受这一个开关控制**: 它不依赖 bypass_matchmaking 的动态返回值工厂
//                             (自己解析 LobbyQuery/Lobby/Task<Lobby[]> 并单独造 Task), 与阶段1/2/3
//                             以及方案B 完全独立。
// 返回 true = 至少装上一个 hook; false = 全部失败(游戏行为原样, 不影响启动)。
// 幂等: 重复调用只返回当前状态。
bool steamhack_install(const cesium_safe::il2cpp_tbl& t,
                       bool bypass_awake = true,
                       bool bypass_restart_check = true,
                       bool bypass_matchmaking = true,
                       bool bypass_lobby_hasvalue = true,
                       const std::string& task_ctor_mode = "auto",
                       bool bypass_lobby_methods = false,
                       bool bypass_lobby_query = true);

// 成功启用的 hook 数量(0~9)。
//   阶段1: SteamManager.Awake(no-op) + SteamAPI_RestartAppIfNecessary(=0)          -> 2
//   阶段2: SteamMatchmaking.CreateLobbyAsync / JoinLobbyAsync(返回已完成的空 Task)   -> 2
//   阶段3: System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue(恒 false)         -> 1(实测不被命中)
//   阶段5: Steamworks.Data.LobbyQuery::RequestAsync(返回长度 0 的 Lobby[] 的 Task)  -> 1
//   方案B: Lobby.SetPublic / Lobby.SetJoinable(no-op) + Lobby.get_Id(属性才 +1)      -> 2 或 3
// **定版配置(方案B 默认关)全部生效时 = 6**(阶段1 2 + 阶段2 2 + 阶段3 1 + 阶段5 1)。
// 0 表示绕过未生效 —— 非 Steam 启动时游戏仍会退出。
int steamhack_hook_count();

// 卸载本模块创建的 hook(进程退出时调用; 幂等)。
// 注意: 不会 Uninitialize MinHook —— 那是 speedhack.cpp 的职责(见上)。
void steamhack_uninstall();
