// config.h - Doorstop 式配置 (doorstop_config.json)
//
// 加载器从 AstralParty_ModLoader\doorstop_config.json 读取配置。
// 所有字段可选, 解析失败/缺失时使用默认值 —— 配置损坏不会阻止游戏启动。

#pragma once

#include <string>

struct LoaderConfig
{
    // 总开关: false 时加载器完全静默(不弹控制台、不加载任何 mod), 游戏原样运行
    bool enabled = true;

    // 托管引导程序: 原生层只负责加载这一个 DLL 并调用入口
    std::string bootstrapAssembly = "CesiumLoader.Bootstrap.dll";
    std::string bootstrapType = "CesiumLoader.Bootstrap.Bootstrap";
    std::string bootstrapMethod = "Main";

    // 编排层选择:
    //   false (默认): 原生层直接加载 sdk/mods 并调用入口 —— 可靠路径,
    //                  用 il2cpp 原生 API, 不受 HybridCLR AOT 反射裁剪影响。
    //   true (实验):   原生层只加载 bootstrap DLL, 由托管代码编排一切。
    //                  注意: HybridCLR 会裁剪部分反射 API (如 Assembly.GetType),
    //                  托管编排可能不可用; 此开关用于实验/验证。
    bool useManagedBootstrap = false;

    // 等待超时(秒)
    unsigned gameAssemblyTimeoutSec = 60;   // GameAssembly.dll 出现
    unsigned domainTimeoutSec = 30;         // il2cpp domain 就绪
    unsigned hybridclrTimeoutSec = 60;      // HybridCLR 热更(AstralParty.Runtime)就绪

    // 控制台
    bool consoleEnabled = true;             // 是否分配控制台窗口
    bool consoleTopmost = false;            // 控制台窗口是否置顶(默认不置顶: 置顶会一直压着游戏)

    // mod 日志 -> 控制台 转发线程
    bool forwardActivityLog = true;

    // 变速引擎基础倍率: 加载器 hook 装好后立即应用, 一直保持。
    // 1.0 = 正常(默认), 2.0 = 全程 2 倍速。**低于 1 倍(减速)被硬性禁止** ——
    // 写 0.5 之类的值会被当成非法并退回 1.0(见 speedhack.h 的 kSpeedMin)。
    // 设为 1.0 或 0 则不启用基础倍率(SpeedHackMod 可用热键临时变速)。
    double speedhackBaseSpeed = 1.0;

    // 变速控制文件通道(<speed>\request.txt / state.txt)。默认开:
    // mod 侧的热更程序集无法 P/Invoke, 只能通过文件请求倍率 ——
    // 关掉它等于"游戏内热键变速"不可用(基础倍率仍然生效)。
    bool speedControlEnabled = true;

    // Steam 自杀门绕过(见 steamhack.h)。默认**开**:
    // 国服客户端在非 Steam 客户端启动时, AOT 类 SteamManager(全局命名空间)的
    // Awake() 会打印 "[ERROR] [SteamManager] 非Steam客户端启动, 退出游戏" 并退出。
    // 本开关把 Awake() 换成 no-op(原生 inline hook), 让游戏继续启动。
    // 设为 false = 恢复原版行为(非 Steam 启动会退出游戏)。
    bool steamBypassEnabled = true;

    // 附加保险(默认开): 让 steam_api64.dll 的 SteamAPI_RestartAppIfNecessary
    // 恒返回 0。它本是 Steamworks 的无害样板调用, 万一退出判断不在 Awake() 里也能兜住。
    // 仅在 steamBypassEnabled 为 true 时才有意义。
    bool steamBypassRestartCheck = true;

    // Steam 大厅匹配绕过(阶段2, 默认**开**) —— 修"Steam 全关时点创建房间/加入房间毫无反应"。
    // 实测异常(BnSdk 错误上报):
    //     NullReferenceException
    //     UI.UICom_CreateRoom.RequestCreateRoom ()
    //     ... --- End of stack trace from previous location ---
    //     Steamworks.SteamMatchmaking.CreateLobbyAsync (System.Int32 maxMembers)
    // 即真实 CreateLobbyAsync 在 Steam 未初始化时自己抛 NRE, 这个 async 异常把建房间流程整个吃掉。
    // 本开关把 CreateLobbyAsync / JoinLobbyAsync 换成"返回一个已完成、结果为 null 的 Task<Lobby?>",
    // 游戏侧 LobbyId 保持 0(= "无 Steam 大厅", 协议合法)后正常建房/进房。
    // 只受本开关控制(与 steamBypassEnabled / steamBypassRestartCheck 互不影响)。
    // 设为 false = 恢复原版行为(创建/加入房间仍会因 async 异常而无反应)。
    bool steamBypassMatchmaking = true;

    // 阶段3(默认**开**): 把 AOT 方法 System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue()
    // 换成"恒返回 false"。
    // 为什么还需要它: CreateLobbyAsync 已经返回了"已完成的空 Task<Lobby?>", 但
    // il2cpp_runtime_invoke 把 Nullable<Lobby> 参数编组进 Task<T>..ctor(T) 时 hasValue 落成了
    // 非零, 于是 await 回来后 val.HasValue 为 true, 游戏继续走 Lobby.SetPublic() 并抛
    // NullReferenceException(实测栈: UI.UICom_CreateRoom.RequestCreateRoom -> Steamworks.Data.Lobby.SetPublic)。
    // RequestCreateRoom 在热更程序集里由 HybridCLR 解释执行, val.HasValue 是一次真实的方法调用,
    // 所以能挂原生 hook; 强制 false 后 steamLobbyId 保持 0(协议里"无 Steam 大厅"合法值),
    // RequestCreateC2S 正常发出。语义自洽: Steam 不存在时任何 Lobby? 都不该有值。
    // 仅在 steamBypassMatchmaking 为 true 时有意义。false = 不挂钩(游戏退回 SetPublic 的 NRE)。
    bool steamBypassLobbyHasValue = true;

    // 当前分发的 SDK 版本(SemVer)。加载器用它校验 mod 声明的 SdkVersion,
    // 不兼容(mod 要求更高版本)时拒绝加载该 mod 并记录警告。
    // 发布新版 SDK 时手动更新此字段 + dist\sdk\CesiumLoader.SDK.dll。
    // 方案C 主方案(默认 "auto"): 造"已完成的空 Task<Lobby?>"时, Task<T>..ctor(T) 的调用方式。
    //
    // 实测根因(上一轮已定位, 不再调研): il2cpp_runtime_invoke 给 Task<T>..ctor(Nullable<Lobby>)
    // 传值类型参数时**编组不正确** —— 传进去的 Nullable 的 hasValue 落成了非零
    // (安装期探针里 il2cpp_field_get_value_object 返回的是**非空**装箱对象, 而"空 Nullable"
    //  按 .NET 语义装箱应得 NULL)。于是 await 回来后 val.HasValue 被判成 true, 游戏执行
    // UICom_CreateRoom.cs:136 的 Steamworks.Data.Lobby.SetPublic() 并抛 NullReferenceException。
    // **不再试图用 runtime_invoke 修它**, 改为直接按原生 ABI 调 ctor 的 methodPointer。
    //
    // 取值:
    //   "auto"  (默认) = 先走 direct(原生 ABI 直调), 探针校验(IsCompleted / get_Result /
    //                    hasValue 诊断)失败则**自动退回** invoke, 并在日志里说明走的哪条路;
    //   "direct"       = 只用 direct; 校验失败即不挂钩(游戏行为与原版一致);
    //   "invoke"       = 只用 il2cpp_runtime_invoke(旧行为)。
    // 非法取值按 "auto" 处理并记日志。仅在 steamBypassMatchmaking 为 true 时有意义。
    std::string steamBypassTaskCtorMode = "auto";

    // 方案B(备用安全网, **默认关**): 把 Steamworks.Data.Lobby 上会抛异常的方法挂成无害:
    //   1) Lobby.SetPublic()       -> no-op
    //   2) Lobby.SetJoinable(bool) -> no-op(不解引用 thisPtr)
    //   3) Lobby.Id: 先判定是字段还是属性 —— 若是**属性**(存在 get_Id)则 get_Id() 恒返回 0
    //      (SteamId 零值); 若是**字段**则只记日志(字段不可 hook), 不硬来。
    //
    // ★★★ 为什么默认**关**(实机教训, **不要改回 true**) ★★★
    //   实机验证: 本开关置 true(其余配置与已验证通过的配置完全一致)会导致**游戏启动早期崩溃** ——
    //   退出码 0xC0000005(访问冲突), 崩溃模块 GameAssembly.dll, 而崩溃前最后一条日志正是
    //   方案B 的 "Lobby.get_Id 首次被拦截"。把本开关改回 false 后, 同样配置下游戏完全正常:
    //   启动成功、登录成功、点"创建房间"成功进房, Player.log 里 NullReferenceException = 0。
    //   => true 是**地雷值**: 任何缺省本键的旧配置都会踩到它, 所以这里的默认值必须是 false。
    //
    // ★ 为什么现在**不需要**它
    //   主解法(方案C)已把 Task 的结果构造成**真正的空 Nullable**(HasValue=false), 于是游戏侧
    //   UICom_CreateRoom 的 `if (val.HasValue) { ... }` 整块被跳过, Lobby.SetPublic() /
    //   Lobby.SetJoinable() **根本不会被调用** —— 没有任何异常需要它兜。
    //
    // ★ 它现在的定位: **备用安全网**
    //   只有"哪天 hasValue 又变回 true"(游戏更新、方案C 的 ABI 假设失效等)时才可能有用。
    //   若要启用, 请**自行在实机上验证启动不崩**(它自己就是实测过的崩溃源), 不要因为"看起来更保险"
    //   就打开。仅在 steamBypassMatchmaking 为 true 时有意义(阶段2 没装就没有空 Task 可返回)。
    bool steamBypassLobbyMethods = false;

    // 阶段5(默认**开**): 把 AOT 方法 Steamworks.Data.LobbyQuery::RequestAsync() 换成
    // "恒返回一个**预建好的、结果为长度 0 的 Lobby[] 的已完成 Task<Lobby[]>**"。
    //
    // 故障(实机实测, 6 条 NRE, 栈顶就是本方法):
    //     NullReferenceException
    //     GameLogic.RoomLogic.OnExitRoomS2CServerCallBack (party.protocol.ExitRoomS2C model, int, bool)
    //     Core.Net.NetManager.Update ()
    //     Steamworks.Data.LobbyQuery.RequestAsync ()            <- NRE 真正抛出点
    //   **更正文档里此前标为"未实测"的那一点**: NRE 抛在 `await ...RequestAsync()` 上,
    //   **不是** `SteamMatchmaking.LobbyList` 取值本身; 危害也不是"最坏是访问违例", 而是
    //   **await 之后的代码全部不执行** —— 真实功能缺失。
    //
    // 游戏侧(decomp_full\GameLogic\RoomLogic.cs:381-401; :545-573 同构):
    //     if (model.Dissolve || model.PlayerId == self) {
    //         LobbyQuery lobbyList = SteamMatchmaking.LobbyList;
    //         Lobby[] array = await ((LobbyQuery)(ref lobbyList)).RequestAsync();   // <- 这里抛 NRE
    //         if (array != null && array.Length > 0) { ...Leave()... }              // 长度 0 -> 整段跳过
    //     }
    //     roomController.UpdateRoomByExit(...);        // :398 <- 之前被跳过
    //     if (self || model.Dissolve) ClearRoomInfo(); // :401 <- 之前被跳过
    //
    // 本开关返回长度 0 的数组后 `array != null && array.Length > 0` 为 false -> 跳过 Steam 大厅清理
    // -> :398 / :401 / :567-573 正常执行(本地房间状态清掉、被踢提示弹出、返回房间列表页)。
    //
    // 影响面为零: 全仓 grep 确认全游戏**只有这 2 处**调用 LobbyQuery.RequestAsync
    // (RoomLogic.cs:384 与 :554), 所以只挂钩这一个方法。
    //
    // 它是**独立**的一层(与 steamBypassEnabled / steamBypassRestartCheck / steamBypassMatchmaking /
    // steamBypassLobbyHasValue / steamBypassLobbyMethods 互不影响): 不依赖阶段2 的动态返回值工厂,
    // 只受本开关控制。设为 false = 恢复原版行为(退房/解散/被踢时 await 之后的代码仍会被 NRE 跳过)。
    bool steamBypassLobbyQuery = true;

    std::string sdkVersion = "2.2.0";

    // 加载器自身版本(SemVer)。与发布 tag (modloader-<版本>) 对应, 启动横幅会打印。
    // 与 sdkVersion 独立递增: 改动引导/互操作/打包时递增此值。
    static constexpr const char* loaderVersion = "2.2.0";
};

// 从 config_path 读取配置。文件不存在/解析失败返回默认配置(不抛异常)。
LoaderConfig load_config(const std::wstring& config_path);
