# Steam 无进程绕过(steam-bypass)——设计与实机结论

> 面向"以后的我 / 别的维护者"。**先读第 3 节「四个非显然的实机结论」,再看代码** ——
> 这一节是本文档最有价值的部分,它能省掉你重新踩一遍坑。
>
> 实现位置: `modding\msvc\src\CesiumLoader\steamhack.{h,cpp}`(主体)、
> `config.{h,cpp}`(配置)、`loader.cpp`(调用点)。
> **哪些是实机验证过的、哪些还没跑过,第 5 节逐条标注**(阶段5 目前只做到"构建通过 + 安装期探针设计",
> 实机回归待做)。

---

## 1. 目标

**让国服客户端在 Steam 进程完全不运行时,也能正常启动、登录、并在多人模式里建房/进房。**

具体要达到:

- 游戏进程正常启动(不因"非 Steam 客户端"自杀退出);
- 登录成功;
- 点「创建房间」能真正进入房间(而不是"点了没反应");
- 点「加入房间」/房间列表进房同样可用;
- 全程不崩(`Player.log` 里 `NullReferenceException` 次数为 0)。

**明确不做的事**:不伪造 Steam 身份、不绕过任何服务端校验、不改动 `steam_api64.dll` 的认证语义。
本方案只是"在 Steam 缺席时,让游戏侧的 Steam 集成优雅地退化成空操作"。

---

## 2. 四个阶段 + 两个补充方案

游戏里所有 Steam 相关崩溃只来自这几个地方,按发现顺序分层;每层各有一个"主解法"和(曾经的)
"补丁"。最终定版配置只启用 **6 个 hook**。

### 阶段1 —— 自杀门(默认开)

| # | 目标方法 | 做法 | 变量 |
|---|---------|------|------|
| 1 | AOT 类 `SteamManager`(全局命名空间)的 `Awake()` | 原生 inline hook,整段换成 `no-op` | `steamBypassEnabled`(缺省 `true`) |
| 2 | `steam_api64.dll!SteamAPI_RestartAppIfNecessary` | `MH_CreateHookApi` 后恒返回 `0`(附加保险) | `steamBypassRestartCheck`(缺省 `true`) |

- 调用链(实测):`SteamManager.Awake() <- SteamPlatform.OnApplication() <- BnSdk.BN_Platform.OnApplication(...)`。
- `Awake()` 里会打印 `[ERROR] [SteamManager] 非Steam客户端启动, 退出游戏` 并退出;换成 no-op 后这段永不执行。
- IL2CPP 是**纯 AOT(无 JIT)**,patch 掉原生代码没有运行期重编译覆盖,是安全的。
- hook 2 依赖从游戏目录推导 `AstralParty_CN_Data\Plugins\x86_64\steam_api64.dll`
  (兜底:扫描 `<游戏目录>\*_Data\Plugins\x86_64\steam_api64.dll`);推导失败只记日志,不阻止启动。

### 阶段2 —— 大厅匹配(默认开)

| # | 目标方法 | 做法 | 变量 |
|---|---------|------|------|
| 3 | `Steamworks.SteamMatchmaking.CreateLobbyAsync(System.Int32)` | 返回"**已完成且结果为 null** 的 `Task<Lobby?>`" | `steamBypassMatchmaking`(缺省 `true`) |
| 4 | `Steamworks.SteamMatchmaking.JoinLobbyAsync(Steamworks.SteamId)` | 同上 | 同上 |

- 实测异常(Steam 全关时点"创建房间",游戏**不崩**,只是这个 async 异常把建房流程整个吃掉):

  ```
  NullReferenceException
  UI.UICom_CreateRoom.RequestCreateRoom ()
  Steamworks.SteamMatchmaking.CreateLobbyAsync (System.Int32 maxMembers)
  ```

- 游戏侧代码(`decomp_full\UI\UICom_CreateRoom.cs:131-142`):

  ```csharp
  ulong steamLobbyId = 0uL;
  Lobby? val = await SteamMatchmaking.CreateLobbyAsync(4);
  if (val.HasValue) { ...; steamLobbyId = ...; }          // 结果为空 -> 整段跳过
  ...RequestCreateC2S(..., steamLobbyId, ...);             // LobbyId=0 = "无 Steam 大厅"(协议合法)
  ```

- **返回类型从被挂钩方法自己的 `MethodInfo` 推导**,不硬编码 `Lobby` —— 两个 hook 共用一个
  `make_default_completed_task(hookedMi)`。

### 阶段3 —— `Nullable<Lobby>.get_HasValue()` 恒 `false`(**实测作废,保留但无效**)

| # | 目标方法 | 做法 | 变量 |
|---|---------|------|------|
| 5 | `System.Nullable\`1<Steamworks.Data.Lobby>::get_HasValue()` | 原生 inline hook,恒返回 `false` | `steamBypassLobbyHasValue`(缺省 `true`) |

- 当初的想法:`val.HasValue` 应该会真实 call 到 AOT 方法 `Nullable\`1<Lobby>::get_HasValue()`,把它 hook 成恒 false 即可。
- **实测否定**:实机日志里这个 hook 的"首次被拦截"那行**从未出现**。原因见第 3.2 节。
- hook 留着无害(万一将来真走 AOT 调用还能兜住),但**它不是解法**。安装时会额外记一行说明。

### 方案C(主解法)—— 绕过 `runtime_invoke`,按原生 ABI 直调 `Task<T>..ctor(T)`

| 变量 | 取值 |
|------|------|
| `steamBypassTaskCtorMode` | `"auto"`(默认) / `"direct"` / `"invoke"` |

- 根因见第 3.1 节:`il2cpp_runtime_invoke` 给 `Task<T>..ctor(T)` 传 `Nullable<T>` **值类型参数**时编组不正确。
- 做法:不经过 `runtime_invoke`,按 Windows x64 ABI 直接调 ctor 的 `methodPointer`:

  ```cpp
  typedef void (*CtorFn)(void* thisObj, void* pArg);
  ((CtorFn)method_pointer_of(ctor))(taskObj, 全零 256B 栈缓冲区);   // >8 字节结构体按引用传参
  ```

- 三个取值:`"auto"` = 先 direct,安装期探针校验(`IsCompleted` / `get_Result` / `hasValue` 诊断)失败则
  **自动退回** `invoke`,并在日志里说明实际走的哪条路;`"direct"` = 只用 direct,校验失败即不挂钩;
  `"invoke"` = 旧行为。非法取值按 `"auto"` 处理并记日志。
- 两条路径**各自打印 hasValue 诊断**,所以日志里能明确看出 direct 有没有把 hasValue 变成 false。

### 方案B(备用安全网)—— `Lobby` 方法无害化(**默认关**,见第 4 节)

| # | 目标方法 | 做法 |
|---|---------|------|
| 6 | `Steamworks.Data.Lobby::SetPublic()` | no-op |
| 7 | `Steamworks.Data.Lobby::SetJoinable(System.Boolean)` | no-op(不解引用 `thisPtr`) |
| 8 | `Steamworks.Data.Lobby::get_Id()` | 恒返回 `0`(SteamId 零值)——**仅当 `Id` 是属性**;是字段则只记日志 |

- 目的:即使方案C 的 ABI 假设不成立、hasValue 仍被判成 true,这一段也不再抛 `NullReferenceException`。
- 这三个都是**非泛型值类型实例方法**,不存在共享泛型问题,被解释执行时会真实走 AOT 入口
  (与阶段3 的情况不同)。
- **`steamBypassLobbyMethods` 默认必须是 `false`** —— 理由见第 4 节与第 5 节。

### 阶段5 —— `LobbyQuery.RequestAsync()` 恒返回"结果为空 `Lobby[]` 的已完成 `Task`"(默认开)

> 编号接着上面排,所以它是 **#9**;但它在**发现顺序**上是最后一个阶段。

| # | 目标方法 | 做法 | 变量 |
|---|---------|------|------|
| 9 | `Steamworks.Data.LobbyQuery::RequestAsync()` | 原生 inline hook,恒返回一个**预建好的**、结果为**长度 0 的 `Lobby[]`** 的已完成 `Task<Lobby[]>` | `steamBypassLobbyQuery`(缺省 `true`) |

- **故障(实机实测 6 条 `NullReferenceException`,栈顶就是本方法)**:退房 / 解散(`OnExitRoomS2CServerCallBack`)、
  被踢(`OnRoomKickPlayerS2CServerCallBack`)时:

  ```
  NullReferenceException
  GameLogic.RoomLogic.OnExitRoomS2CServerCallBack (party.protocol.ExitRoomS2C model, ...)
  Core.Net.NetManager.Update ()
  Steamworks.Data.LobbyQuery.RequestAsync ()          <- NRE 真正抛出点
  ```

- **★ 对本文档此前"未实测"标注的更正**:此前附录A 写的是"`SteamMatchmaking.LobbyList` 属性/查询
  无判空,**Steam 未初始化时行为未实测**,最坏是访问违例"。现在有实测数据了,据此更正:
  - 抛点在 **`await ...RequestAsync()`** 上(栈顶就是 `LobbyQuery.RequestAsync`),
    **不是** `SteamMatchmaking.LobbyList` 取值本身;
  - 危害也不是"最坏是访问违例",而是 **`await` 之后的代码全部不执行** ——
    本地房间状态不清、被踢提示不弹、不返回房间列表页。这是**真实功能缺失**,不是"仅仅少一次清理"。
- 游戏侧(`decomp_full\GameLogic\RoomLogic.cs:381-401`;`:545-573` 被踢路径同构):

  ```csharp
  if (model.Dissolve || model.PlayerId == self)
  {
      LobbyQuery lobbyList = SteamMatchmaking.LobbyList;                     // :383 / :553
      Lobby[] array = await ((LobbyQuery)(ref lobbyList)).RequestAsync();    // :384 / :554 <- NRE 抛出点
      if (array != null && array.Length > 0) { ...((Lobby)(ref val)).Leave()... }
  }
  roomController.UpdateRoomByExit(model.MasterId, model.PlayerId, model.Dissolve);  // :398 <- 之前被跳过
  if (self || model.Dissolve) ClearRoomInfo();                                       // :401 <- 之前被跳过
  ```

  被踢路径被跳过的还有 `ShowTips(1029)` 与 `ReturnThePanelDirectlyInHomeScene(...)`(`:567-573`)。
- **修法**:返回长度 0 的数组 → `array != null && array.Length > 0` 为 `false` → 跳过 Steam 大厅清理
  → `:398` / `:401` / `:567-573` 正常执行。
- **实现要点**(细节见 `steamhack.cpp` 的"阶段5"一节):
  - 从 `RequestAsync` 自己的 `MethodInfo` 推导返回类型 → `Task<Lobby[]>` 的 `Il2CppClass`
    (日志会打印**返回类型全名**,实机应为 `System.Threading.Tasks.Task\`1<Steamworks.Data.Lobby[]>`);
  - 用 `il2cpp_array_new(Lobby, 0)` 造长度 0 的数组(注意第 1 个参数是**元素**类型 `Lobby`,
    不是数组类型 —— 传数组类型会得到 `Lobby[][]`);
  - **复用方案C 已验证的原生 ABI 直调 ctor 机制**直调 `Task<Lobby[]>::.ctor(Lobby[])`。
    与方案C 唯一的差别:这里的泛型参数是**引用类型 `Lobby[]`**(指针本身就是实参),
    **不涉及 `Nullable` 装箱**,所以没有 3.1 节那个编组问题;
  - `Task<T>` 上有**多个** 1 参数构造函数(`internal Task(TResult)` 与 `public Task(Func<TResult>)`),
    所以实现里**按参数类型精确筛**出 `Task<TResult>(TResult)`;筛不到才退回"名字 + 参数个数"匹配,
    且那条路仍要过探针校验;
  - 安装期探针(全程 `__try/__except`):`IsCompleted == true` **且** `get_Result()` 是
    **长度 0 的 `Lobby[]`**;校验不通过 → **不挂钩**;
  - 该 `Task` **只构造一次,所有调用复用同一个实例**(`Task<T>` 不可变,`get_Result()` 恒返回同一个空数组),
    并被**显式 GC root 住**:模块级全局变量 + (`il2cpp_gchandle_new` 导出存在时)一个显式 GC 句柄
    —— 它若被回收就是 use-after-free,所以这一条是硬要求;
  - **影响面为零**:全仓 grep 确认全游戏**只有这 2 处**调用 `LobbyQuery.RequestAsync`
    (`RoomLogic.cs:384` 与 `:554`),挂钩这一个方法不影响任何其它 Steam 行为。
- **失败一律 fail-safe**:解析不到类 / `methodPointer` 为空 / ctor 参数被判定为值类型 /
  探针校验不通过 / MinHook 失败 —— 都**只记日志、不挂钩**,回到"那 6 条 NRE",绝不让安装期崩游戏。
- **注意它仍是"启动时无条件安装"**:所以即使从 Steam 启动、真大厅可用,`RequestAsync` 也照样被换成
  空数组 —— 与阶段2 一样,要保留真大厅行为必须把本开关设为 `false`(见第 6 节)。

### 目标方法一览 + RVA 记录方式

所有 hook 的解析过程都写日志,这是排查的核心手段:

- **解析记录**:域内程序集数量与名单 → 每个候选命中位置(`assembly` / `namespace`)→
  `MethodInfo*` 与 `methodPointer` 地址 → **相对模块基址的偏移** → 参数个数 →
  返回值/工厂解析的每个候选命中与失败原因 → 每个 hook 的安装结果。
- **RVA 的记录方式**:`steamhack.cpp` 里 `rva_of(p)` 用 `GetModuleHandleW(L"GameAssembly.dll")` 取基址,
  打印成 `RVA +0x……`;`steam_api64.dll` 的 hook 目标则用 `offset_in(steam, target)` 打印
  `+0x…… steam_api64.dll`。**日志里的十六进制偏移可以直接跳进 IDA/Ghidra**。
- **已记录的崩溃 RVA**:上一轮把游戏打死的参数约定错误是 `0xC0000005 / GameAssembly.dll+0x291ec8`
  (调用栈 `il2cpp_field_get_value_object <- invoke_task_factory <- resolve_task_factory <- install_lobby_hook`)。

### 安装时机(很关键)

`steamhack_install()` 由 `loader.cpp` 的 boot 线程在
**`g_il2cpp.thread_attach(domain)` 之后、`wait_hybridclr()` 之前**调用:

- 此时 IL2CPP 已就绪、AOT 镜像已加载,**且热更程序集还没进来**(所以枚举到的就是 AOT 镜像);
- AOT 的 `SteamManager.Awake()` 要到进程启动约 **7 秒**后才执行,窗口充裕。

`MinHook` 是**进程级单例**(`speedhack.cpp` 已经初始化过):本模块把
`MH_ERROR_ALREADY_INITIALIZED` 当成功,且**绝不调用 `MH_Uninitialize()`**;
`steamhack_uninstall()` 只摘掉自己创建的 hook。

**失败只记日志,绝不阻止游戏启动**:绕过没装上时游戏行为与打补丁前完全一致。

---

## 3. 四个非显然的实机结论(最重要)

### 3.1 `il2cpp_runtime_invoke` 给 `Task<T>..ctor(T)` 传 `Nullable<T>` 值类型参数时**编组不正确**

- **症状**:`CreateLobbyAsync` 已被换成"返回已完成的空 Task",但 `await` 回来后 `val.HasValue` 仍是 `true`,
  游戏照旧执行 `Lobby.SetPublic()` 并抛 `NullReferenceException`。
- **证据(安装期探针)**:`get_Result()` 返回的是**非空装箱对象**;而"空 `Nullable`"按 .NET 语义装箱**应得 `NULL`**
  (同样的输入下 `il2cpp_value_box` 确实返回了 `NULL`)。说明 `runtime_invoke` 把 `Nullable<Lobby>`
  参数编组进 `Task<T>..ctor(T)` 时 `hasValue` 落成了非零 —— 是**编组差异,不是逻辑错误**。
- **必须绕过它**:按 **Windows x64 ABI** 直接调用 ctor 的 `methodPointer`
  —— **>8 字节的结构体参数按引用传递**;IL2CPP 的 AOT 代码是 MSVC 编译的 C++,遵循同一套 ABI,
  `runtime_invoke` 才是那个多此一举、会把 `Nullable` 编组坏的中间层。
- **并且要补一个 `MethodInfo*` 尾参**:IL2CPP 的实例方法原生签名 = `(this, ..., MethodInfo*)`。
  漏掉尾参同样会炸。见 `pod_call_task_ctor_direct`。

### 3.2 HybridCLR 解释器把 `Nullable.HasValue` 当**内建指令内联**,该方法的 inline hook **从未被命中**

- `Nullable<Lobby>.get_HasValue` 的 inline hook 在实机日志里**从来没有出现过"首次被拦截"那行**
  —— 一次都没命中。
- 原因:热更程序集由 **HybridCLR 解释执行**,解释器把 `Nullable<T>.HasValue` 当**内建指令**直接内联处理,
  **不走 `MethodInfo->methodPointer`**。挂在那个 `methodPointer` 上的 hook 自然永不执行。
- **推论(重要)**:凡是"HotFix 侧对某个 BCL 内建成员的调用",都不要指望用挂 `methodPointer` 的方式拦截。
  要判断某个 hook 到底有没有生效,**只能看日志里有没有"首次被拦截"**,不能看"安装成功"。
  ("安装成功"只说明 `MH_CreateHook` 返回了 `MH_OK`,不代表会被调用。)

### 3.3 `Lobby.Id` 是**属性**(`get_Id` + `<Id>k__BackingField`),不是字段

- 游戏侧写法 `((Lobby)(ref value)).Id` 编译成 `Lobby::get_Id()` 调用,反编译文本里
  `decomp_full\UI\UICom_CreateRoom.cs:140` 是 `SteamId.op_Implicit(((Lobby)(ref value)).Id)`。
- 影响:方案B 第 8 项**只在 `Id` 是属性时**才能挂钩(`get_Id()` 恒返回 0);
  若是字段则**字段不可 hook**,模块只记日志、不硬来。安装期会把字段名列表与含 `Id`/`Public`/`Join`
  的方法名列表打进日志,便于确认。
- 实测结论:`Id` 是属性,所以 `get_Id` 可以挂。

### 3.4 退房/解散/被踢时的 NRE 抛在 `await LobbyQuery.RequestAsync()` 上,**不是** `SteamMatchmaking.LobbyList`

- **症状(实机)**:离开或解散房间后 UI 看着正常,但 `Player.log` 里出现 **6 条 `NullReferenceException`**,
  栈顶是 `Steamworks.Data.LobbyQuery.RequestAsync ()`(完整栈见 2 节"阶段5")。
- **结论**:
  - `LobbyQuery.RequestAsync()` 是**返回 `Task<Lobby[]>` 的 async 方法**。它内部真的去问 Steam
    要大厅列表(最终走到 `Steamworks.Data.LobbyQuery.RequestAsync ()` 里的原生
    `SteamMatchmaking` 调用),Steam 未初始化时这个 `await` 直接抛 NRE;
  - 因此**它是 `await` 上的异常**,属于"`async` 方法内异常" —— 危害是 **`await` 之后的代码全部不执行**,
    而不是"访问违例崩游戏"。
- **为什么值得单独记一条**:此前文档把它猜成"`SteamMatchmaking.LobbyList` 取值本身可能访问违例"
  (见附录A 的旧结论),方向猜错了 —— 于是"建议方案"也错了(见附录A 现在的更正)。
  这条也再次印证 3.2 的教训:**hook 到底有没有效,只能看日志,不能看推测**。
- 阶段5 的修法是"让它返回一个长度 0 的数组",而不是"给 `RoomLogic` 加 `try/catch`"
  (后者要动热更程序集,需要走 HybridCLR 流程)。

---

## 4. 配置项全表

配置来源: `<游戏 exe 目录>\AstralParty_ModLoader\doorstop_config.json`。
**所有字段可选;缺失/损坏时使用默认值,不会阻止游戏启动。**

| 键 | 类型 | 默认值 | 作用 | 风险 / 备注 |
|----|------|--------|------|-------------|
| `steamBypassEnabled` | bool | **缺省 = `true`** | 阶段1:把 AOT 类 `SteamManager.Awake()` 换成 no-op,让游戏不自杀退出 | 关掉 = 非 Steam 启动时游戏会退出(等于原版) |
| `steamBypassRestartCheck` | bool | **缺省 = `true`** | 阶段1 附加保险:`SteamAPI_RestartAppIfNecessary` 恒返回 0 | 仅在 `steamBypassEnabled=true` 时有意义;无害 |
| `steamBypassMatchmaking` | bool | **缺省 = `true`** | 阶段2:`CreateLobbyAsync` / `JoinLobbyAsync` 返回"已完成的空 Task",修"点创建/加入房间没反应" | **只受本开关控制**,与阶段1 两个开关互不影响。关掉 = 创建/加入房间仍无反应 |
| `steamBypassLobbyHasValue` | bool | **缺省 = `true`** | 阶段3:`Nullable\`1<Lobby>::get_HasValue()` 恒返回 `false` | **实测作废**(3.2 节):hook 从不被命中,留着无害。仅在 `steamBypassMatchmaking=true` 时有意义 |
| `steamBypassTaskCtorMode` | string | `"auto"` | 方案C:造空 Task 时 `Task\`1..ctor(T)` 的调用方式 —— `auto`(先 direct,探针失败自动退回 invoke)/ `direct` / `invoke` | 非法值按 `"auto"` 处理并记日志。仅在 `steamBypassMatchmaking=true` 时有意义 |
| `steamBypassLobbyMethods` | bool | **`false`(★ 必须是 false)** | 方案B 备用安全网:`Lobby.SetPublic()`/`SetJoinable(bool)` 挂 no-op,`Id` 是属性则 `get_Id()` 恒返回 0 | **实测 `true` 会让游戏启动早期崩溃**(见下)。仅在 `steamBypassMatchmaking=true` 时有意义 |
| `steamBypassLobbyQuery` | bool | **缺省 = `true`** | 阶段5:`LobbyQuery.RequestAsync()` 恒返回"预建好的、结果为**长度 0 的 `Lobby[]`** 的已完成 `Task<Lobby[]>`",修"退房/解散/被踢后 `await` 之后的代码被 NRE 跳过" | **只受本开关控制**,与上面几个 `steamBypass*` 互不影响(它不依赖阶段2 的动态返回值工厂)。关掉 = 退房/解散/被踢后本地房间状态仍不清、被踢提示仍不弹、仍不返回房间列表页。全游戏只有 2 处调用(`RoomLogic.cs:384`/`:554`),影响面为零 |
| `sdkVersion` | string | `"2.2.0"` | 当前分发的 SDK 版本,用于校验 mod 声明的 `SdkVersion` | 升级 SDK 时同步更新 |

> `loaderVersion` 不是 JSON 键,它是 `config.h` 里的 `static constexpr const char* loaderVersion`(当前 `"2.2.0"`),
> 与 tag `modloader-<版本>` 对应,启动横幅打印。

### ★ 为什么 `steamBypassLobbyMethods` 的默认值必须是 `false`

**实机教训,不要改回 `true`:**

- 把该键设为 `true`(其余配置与已验证通过的配置**完全一致**)会导致**游戏启动早期崩溃**:
  退出码 `0xC0000005`(访问冲突),崩溃模块 `GameAssembly.dll`,而**崩溃前最后一条日志正是方案B 的
  "`Lobby.get_Id` 首次被拦截"**。
- 把该键改回 `false` 后,同样配置下游戏完全正常(启动成功、登录成功、点「创建房间」成功进房,
  `Player.log` 里 `NullReferenceException` = 0)。
- 结论:`true` 是**地雷值**。任何**缺省该键**的旧配置都会踩到它,所以加载器侧默认值必须是 `false`。
  改默认值时同步改了 `config.h` / `config.cpp` / `dist` 与 `staging` 的 `doorstop_config.json` /
  `README.md` 的配置表 / `steamhack.h` 的默认参数。

**为什么现在不需要它**:主解法(方案C)已把 Task 结果构造成**真正的空 `Nullable`**(`HasValue=false`),
于是游戏侧 `if (val.HasValue) { ... }` 整块被跳过,`Lobby.SetPublic()` / `Lobby.SetJoinable()`
**根本不会被调用** —— 没有任何异常需要它兜。

**它现在的定位**:只有"哪天 `hasValue` 又变回 `true`"(游戏更新、方案C 的 ABI 假设失效等)
时才可能有用。**启用前请自行在实机上验证启动不崩**(它自己就是实测过的崩溃源),
不要因为"看起来更保险"就打开。

---

## 5. 已验证的测试矩阵

定版 `version.dll`(阶段1-3 + 方案C,**5 个 hook**):**343552 字节**,
`SHA256 = EE84C27556C3E3D1E753751EFCE1A30D62F53D03A5760F9BC12D436FD98ADE36`
(Release x64,MSVC;`src\CesiumLoader\bin\Release\version.dll`)。

### 5.1 阶段1-3 + 方案C 的历史记录(已实机验证)

| # | 配置要点 | 环境 | 结果 | 判定 |
|---|---------|------|------|------|
| 1 | `steamBypassLobbyMethods = true`(其余同最终) | Steam 进程**全关** | **启动早期崩溃**:`0xC0000005` / `GameAssembly.dll`;崩溃前最后一条日志是方案B 的 `Lobby.get_Id` 首次被拦截 | ❌ 作废 |
| 2 | **最终配置**:`steamBypassMatchmaking=true`,`steamBypassLobbyMethods=false`,`steamBypassEnabled`/`steamBypassRestartCheck`/`steamBypassLobbyHasValue`/`steamBypassTaskCtorMode` 全部缺省 | Steam 进程**全关** | 游戏正常启动 ✅、登录成功 ✅、点「创建房间」**成功进入房间** ✅、`Player.log` 中 `NullReferenceException` 出现次数 = **0** ✅、`5/5` hook 全装 ✅、无崩溃 ✅ | ✅ **通过(阶段5 之前的定版)** |
| 3 | 加入房间 / 被踢 / 房间解散等路径 | — | 当时**未单独做端到端实机回归**;后续实测补上了"退房/解散/被踢"的 NRE 证据(见 3.4 与阶段5) | ⚠ 已由阶段5 接手 |
| 4 | Steam 进程**运行中**启动(从 Steam 启动) | Steam 运行 | **未实测** | ⚠ 待验证 |

**为什么当时是 `5/5`**:阶段1(2 个)+ 阶段2(2 个)+ 阶段3(1 个)+ 方案B(0 个,默认关)= **5**。
方案B 打开时会变成 7 或 8(`Lobby.get_Id` 只有 `Id` 是属性时才多 1 个)。
日志里对应这行:`[steamhack] 完成: 已启用 5/5 个 hook (...)`。

### 5.2 阶段5 版本(**6 个 hook**)—— 构建通过,**实机回归待做**

当前 `version.dll`:**381440 字节**,`SHA256 = 6BB537E13C2A1180F9AA95F1BDE8F4A5952B70563B393EB117FC62261B8C05D9`
(Release x64,MSVC;`src\CesiumLoader\bin\Release\version.dll`;改动含阶段5)。

发布包:`dist\release\cesium-loader-2.2.0.zip` = `cesium-loader.zip`,**312171 字节**,
`SHA256 = 48A8D531225C193F71DE8195FE27302FA84FC61E86235F252722B9E6B0C0D22F`
(版本号仍是 `2.2.0` —— 本次未改 `loaderVersion`/`sdkVersion`)。

| # | 配置要点 | 环境 | 结果 | 判定 |
|---|---------|------|------|------|
| 5 | 最终配置 + `steamBypassLobbyQuery` 缺省(`true`) | Steam 进程**全关** | **未实测**:本次只做到"编译 0 警告 0 错误 + 安装期探针设计校验",没有跑游戏 | ⚠ **待实机验证(见下)** |
| 6 | 退房 / 解散房间 / 被踢 | Steam 进程全关 | **未实测**:预期 `Player.log` 里这 6 条 NRE 归 0,且能返回房间列表页 | ⚠ **待实机验证** |

**为什么是 `6/6`**:阶段1(2 个)+ 阶段2(2 个)+ 阶段3(1 个)+ **阶段5(1 个)** + 方案B(0 个,默认关)= **6**。
方案B 打开时会变成 8 或 9。
日志里对应这行:`[steamhack] 完成: 已启用 6/6 个 hook (...)`。

**怎么自己复验(阶段5)**:

1. 看加载器日志里是否出现 `[steamhack] 完成: 已启用 6/6 个 hook`;
2. 看是否有 `[steamhack] 阶段5[探针]: 校验通过 —— 已完成 + get_Result() 是长度 0 的 Lobby[]`
   —— **这是阶段5 唯一能证明"返回值造对了"的安装期证据**;
3. **关键**:退房 / 解散房间 / 被踢之后,看有没有出现
   `[steamhack] LobbyQuery.RequestAsync 首次被拦截`
   —— 只有这行出现,才说明 hook **真的被调用了**(3.2 节的教训:安装成功 ≠ 会被调用);
4. 检查 `Player.log` 里那 6 条 `NullReferenceException` 是否归 0,以及退出房间后是否**真的返回房间列表页**;
5. 若第 2 步出现的是"未按参数类型精确筛出 `.ctor(1)`"或"探针校验未通过",说明返回值没造对 ——
   此时模块**故意不挂钩**(fail-safe),游戏行为回到没打阶段5 的状态,请把日志发出来。

> **诚实标注**:阶段5 的"安装期探针"只能证明"我们构造出来的 `Task` 是已完成且结果为空数组",
> **不能**证明"游戏真的会调用到它"。后者只有靠第 3 步那行"首次被拦截"来确认。

---

## 6. 已知损失与限制

**已确认(由 hook 语义直接推出):**

1. **Steam 大厅联机整体不可用**:`CreateLobbyAsync`/`JoinLobbyAsync` 被换成空 Task,
   因此 `steamLobbyId` 恒为 `0`(协议里"无 Steam 大厅"的合法值)。
   通过**服务端房间列表/房间号**进房仍正常,但"**Steam 好友邀请/大厅分享进房**"这条路径失效。
2. **`RoomShortInfo.SteamLobbyId` 必然为 0**:依赖它做 Steam 大厅匹配的 UI 逻辑拿不到有效大厅。
3. **Steam 成就 / Rich Presence / Steam Deck 手柄文本输入等不可用** ——
   Steam 进程不运行时本来也不可用;本方案不试图恢复它们。
4. **hook 是启动时无条件安装的,不检测 Steam 是否在运行**。
   也就是说:**即使从 Steam 启动,这些 hook 也照样生效**,Steam 大厅同样不会被创建,
   `LobbyQuery.RequestAsync()` 也照样被换成空数组。
   想要"Steam 模式下用真大厅",必须把 `steamBypassEnabled`、`steamBypassMatchmaking` 与
   **`steamBypassLobbyQuery`** 都设为 `false`(等于完全回到原版行为)。

**未验证 / 不确定(诚实标注):**

5. `SteamManager.Awake()` 被**整段 no-op** 之后,它原本是否还承担 `SteamAPI.Init` 之类的**初始化**职责?
   若承担,则从 Steam 启动时 Steam 可能处于"未初始化"状态(而不只是"大厅不建")。
   挂钩的调用链显示 `SteamPlatform.OnApplication()`(**未被 hook**)仍然执行,初始化或许在那里;
   **但这一点没有实测**。→ 属于第 5 节矩阵 #4 的风险。
6. `steamBypassLobbyMethods=true` 那次崩溃的**具体 RVA 未记录**(只记到了模块 `GameAssembly.dll`)。
   若将来要重启方案B,建议先补上精确 RVA 再动手。
7. 本方案**不涉及**服务端侧校验:建房/进房成功只说明协议上 `LobbyId=0` 被接受,不代表 Steam 相关玩法可用。
8. **阶段5 尚未实机验证**(见 5.2 节)。具体不确定的是:
   - 该 hook **到底会不会被调用**(`RequestAsync` 是否真的走 `methodPointer` 的 AOT 入口;
     3.2 节已证明"安装成功 ≠ 会被调用")—— 只能靠日志里有没有
     `LobbyQuery.RequestAsync 首次被拦截` 来判定;
   - 返回的 `Task<Lobby[]>` 与游戏侧 `await`/`async` 状态机的交互**只做了安装期探针校验**
     (`IsCompleted == true` + `get_Result()` 是长度 0 的数组),没有跑过真实的退房/被踢流程;
   - 若探针未通过,模块会**故意不挂钩**(只记日志)—— 那种情况下退房/被踢的 NRE 会照旧出现,
     属于"没修好",但**不会**让游戏更坏;
   - 该 `Task` 是**常驻的 GC root**(活着到进程结束)且**所有调用复用同一个实例**。设计上刻意如此
     (`Task<T>` 不可变),代价只是常驻一个小对象对。

---

## 7. 如何回退

### 最快回退(推荐):关开关,不换文件

编辑 `<游戏 exe 目录>\AstralParty_ModLoader\doorstop_config.json`:

```jsonc
"steamBypassEnabled": false,
"steamBypassMatchmaking": false,
"steamBypassLobbyQuery": false
```

- 三个都设 `false` → 加载器**完全跳过** Steam 绕过,游戏行为与打补丁前一致
  (非 Steam 启动会退出游戏,创建/加入房间仍会无反应)。日志会打印
  `[steamhack] steamBypassEnabled=false 且 steamBypassMatchmaking=false 且 steamBypassLobbyQuery=false, 跳过 Steam 绕过(原版行为)`。
- 只想回退某一层,就单独设那一层的开关:
  `steamBypassEnabled`(阶段1)/ `steamBypassRestartCheck`(阶段1 保险)/
  `steamBypassMatchmaking`(阶段2,同时决定阶段3 与方案B 是否有意义)/
  `steamBypassLobbyHasValue`(阶段3)/ `steamBypassLobbyMethods`(方案B,默认已关)/
  **`steamBypassLobbyQuery`(阶段5,默认开 —— 只回退它,退房/被踢的 NRE 会回来,其余照常)**。
- 想让加载器整个不干活:`"enabled": false`。

### 完全回退(连加载器一起撤掉)

备份位置:**`outputs\backup-before-steambypass\`**(工作区根目录下)

| 文件 | 大小 | 说明 |
|------|------|------|
| `outputs\backup-before-steambypass\version.dll` | 206848 字节 | 打 Steam 绕过**之前**的加载器 |
| `outputs\backup-before-steambypass\doorstop_config.json` | 1648 字节 | 打 Steam 绕过之前的配置(不含任何 `steamBypass*` 键) |

把这两个文件按原名覆盖回游戏目录(`version.dll` → 游戏 exe 目录;
`doorstop_config.json` → `AstralParty_ModLoader\`),即回到加 Steam 绕过之前的状态。

### 卸载加载器

删除游戏 exe 目录下的 `version.dll` 即可(可选:再删 `AstralParty_ModLoader\` 目录)。
注意旧版独立变速器也用 `version.dll`(speedhack-rs),**不要与它同时安装**。

---

## 8. 日志对照表(排障用)

| 日志片段 | 含义 |
|---------|------|
| `[steamhack] 安装 Steam 绕过: 阶段1 ... + 方案C ... + 方案B ...` | 安装开始 |
| `[steamhack] 命中 SteamManager.Awake: image=... (RVA +0x...)` | 阶段1 hook 1 解析成功 |
| `[steamhack] 未找到 SteamManager.Awake(候选命名空间已全部试过) —— Steam 自杀门未被绕过` | 阶段1 **失败**,非 Steam 启动会退出 |
| `[steamhack] SteamManager.Awake 已被 no-op 拦截, Steam 校验未执行(游戏继续启动)` | 阶段1 hook 1 **首次被命中** |
| `[steamhack] steam_api64.dll 已加载: <路径>` | hook 2 的目标模块找到了 |
| `[steamhack] steamBypassTaskCtorMode="auto"/"direct"/"invoke"` | 方案C 实际走的模式 |
| `[steamhack] 阶段3 说明(实测): Nullable<Lobby>.get_HasValue 这条路线**已作废** ...` | 每次安装都会打印的提醒 |
| `[steamhack] steamBypassLobbyMethods=false, 跳过方案B ...` | **定版配置下的正常输出**(方案B 关闭) |
| `[steamhack] 阶段5: **已按参数类型精确命中** 类..ctor(1): 参数类型全名=Steamworks.Data.Lobby[] ...` | 阶段5 成功挑出正确的 `Task<TResult>(TResult)` ctor(不是 `Task(Func<TResult>)`) |
| `[steamhack] 阶段5: 返回值解析 **返回类型全名 = System.Threading.Tasks.Task\`1<Steamworks.Data.Lobby[]>** ...` | 阶段5 推导出的返回类型(**核对这行**) |
| `[steamhack] 阶段5[探针]: 校验通过 —— 已完成 + get_Result() 是长度 0 的 Lobby[]` | **阶段5 返回值造对了的安装期证据** |
| `[steamhack] 阶段5: 探针校验未通过 —— **不挂钩**` | 阶段5 **故意没挂钩**(fail-safe):返回值不可信,退房/被踢的 NRE 会照旧 |
| `[steamhack] LobbyQuery.RequestAsync 首次被拦截: this=0x... -> 恒返回预建好的 Task<Lobby[]>` | **阶段5 hook 真的被调用了**(唯一可靠证据) |
| `[steamhack] steamBypassLobbyQuery=false, 跳过阶段5 ...` | 阶段5 被显式关闭时的正常输出 |
| `[steamhack] 完成: 已启用 6/6 个 hook (...)` | **当前定版配置下的预期结果**(方案B 默认关) |
| `[steamhack] 完成: 已启用 5/5 个 hook (...)` | **阶段5 之前**那版定版的预期结果(旧构建) |
| `[steamhack] 未安装任何 hook —— 游戏行为与原版一致` | 全部失败,需查上面的失败原因 |

---

## 附录A. Steam 调用点清单(阶段1-5 之后的兜住情况)

范围:`decomp_full\`(**热更程序集 `AstralParty.Runtime`**)。AOT 侧(`GameAssembly.dll` 里的
`SteamManager` / `SteamPlatform`)不在 `decomp_full` 里,已由阶段1 覆盖。

| 文件:行 | 代码 | 触发时机 | 是否已被 hook 兜住 | 风险 |
|---------|------|---------|-------------------|------|
| `GameLogic\RoomLogic.cs:383` | `LobbyQuery lobbyList = SteamMatchmaking.LobbyList;`<br>`Lobby[] array = await ((LobbyQuery)(ref lobbyList)).RequestAsync();` | `OnExitRoomS2CServerCallBack`:`model.Dissolve`(房间解散)**或**自己退出房间(`model.PlayerId == 自己`)时 | ✅ **阶段5 hook 兜住**(`RequestAsync` 恒返回"结果为长度 0 的 `Lobby[]`"的已完成 `Task`) | ~~高~~ → **已消除**:实测 NRE 抛在 **`:384` 的 `await RequestAsync()`** 上(**不是** `LobbyList` 取值本身 —— 此前"未实测"的判断已更正,见 3.4)。返回空数组后 `array != null && array.Length > 0` 为 `false`,跳过 Steam 大厅清理,`:398`/`:401` 恢复执行 |
| `GameLogic\RoomLogic.cs:553` | 同上 | `OnRoomKickPlayerS2CServerCallBack`:自己被踢时 | ✅ **阶段5 hook 兜住**(同一处 hook,`:554` 是第 2 个也是全游戏最后一个调用点) | ~~高~~ → **已消除**:同上;被踢路径被跳过的 `ShowTips(1029)` 与 `ReturnThePanelDirectlyInHomeScene(...)`(`:567-573`)也恢复执行 |
| `GameLogic\RoomLogic.cs:157` | `await SteamMatchmaking.JoinLobbyAsync(new SteamId { Value = ... })` | `DealJoinRoom()` ← `OnJoinRoomS2CServerCallBack`(加入房间成功回调) | ✅ 阶段2 hook 4(`JoinLobbyAsync` → 已完成空 Task) | 低 |
| `GameLogic\WatchLogic.cs:141` | `await SteamMatchmaking.JoinLobbyAsync(new SteamId { Value = ... })` | `OnWatchRefreshRoomStateS2CServerCallBack`(观战/回放切到 RUNNING) | ✅ 阶段2 hook 4 | 低 |
| `UI\UICom_CreateRoom.cs:132` | `Lobby? val = await SteamMatchmaking.CreateLobbyAsync(4);` | 点「创建房间」按钮(`RequestCreateRoom`) | ✅ 阶段2 hook 3 | 低(定版已实机验证进房成功) |
| `UI\UICom_CreateRoom.cs:136/138/140` | `val.Value.SetPublic()` / `SetJoinable(true)` / `.Id` | 同上,仅在 `val.HasValue == true` 时进入 | ✅ 方案C 使 `HasValue=false` → **整块被跳过**;方案B(默认关)是二次保险 | 低(方案B 打开反而会崩,见第 4 节) |
| `GameLogic\RoomLogic.cs:59-60`(注销见 `:83-84`) | `SteamFriends.OnGameRichPresenceJoinRequested += ...`<br>`SteamFriends.OnGameLobbyJoinRequested += ...` | `RoomLogic.Connect()` ← `GameLogicManager.Connect()`(`:201`)← `RPCMsgManager.RegisterIRPCSyncConnect()` ← `NetManager.InitConnect()`(`:160`)← **登录成功 `ConnectS2C` 处理里 `NetManager.cs:465`** | ❌ 无 hook(也不该 hook) | **中**:仅为**事件注册**。Steam 关闭时回调永不触发(所以处理函数 `:171` / `:245` 走不到);风险在于"注册"这一步碰到 Steam 静态类的初始化/回调注册。**Steam 全关实测通过**,故判定为可接受 |
| `GameLogic\RoomLogic.cs:171`、`:245` | `OnGameRichPresenceJoinRequestedForSteam` / `OnGameLobbyJoinRequestedForSteam` | Steam 回调触发时(需 Steam 在线) | ❌ 无 hook | 低(Steam 关闭时永不触发);`:193` 有 `lobbyId == 0` 提前 return 的保护 |
| `FairyGUI\InputTextField.cs:1057`、`:1059`、`:1066`、`:1070` | `SteamUtils.ShowGamepadTextInput(...)` / `OnGamepadTextInputDismissed` / `GetEnteredGamepadText()` | 输入框获得焦点时,但**条件是 `GameSettings.IsRunningOnSteamDeck`** | ❌ 无 hook | 低:`IsRunningOnSteamDeck` 是 `Core\GameSettings.cs:16` 的 `public static bool`,非 Steam Deck 环境恒为 false → 整条路径短路 |

**结论(阶段5 之后)**:此前"真正**未兜住**的只剩 `RoomLogic.cs:383` 与 `:553`"这句已经**过期** ——
这两处现在由**阶段5**(`LobbyQuery.RequestAsync()` 恒返回结果为长度 0 的 `Lobby[]` 的已完成 `Task`)
兜住。清单里已**没有**"完全未兜住"的调用点,剩下的只是上面那几条"判定为可接受/低风险"的
(Steam 关闭时走不到、或已有提前 return 保护)。

> **对旧建议的更正**:此前的建议方向是"给 `SteamMatchmaking.LobbyList` 的访问加保护
> (判 `SteamClient.IsValid` / 空查询),或在 `RoomLogic` 侧对这两处加 `try/catch`"。
> 实测证明 **NRE 不在 `LobbyList` 取值上,而在 `:384`/`:554` 的 `await RequestAsync()` 上**
> (见 3.4),所以"给 `LobbyList` 加保护"这条方向是错的;而"给 `RoomLogic` 加 `try/catch`"
> 仍然可行,但它要动**热更程序集**、必须走 HybridCLR 热更流程 —— 阶段5 用原生 hook 达到了
> 同样的效果(而且不需要改任何托管代码)。若将来 `RoomLogic` 侧自己补了 `try/catch`,
> 阶段5 仍然无害(返回值只会让那段清理被跳过)。

**注意**:`decomp_full\` 是**热更程序集**的反编译,改它需要走 HybridCLR 热更流程;AOT 侧的
`SteamManager` / `SteamPlatform` / `Steamworks.Data.LobbyQuery` **不在此清单内**
(它们只能靠原生 hook,分别由阶段1 与阶段5 处理)。

---

## 附录B. 相关文件速查

| 文件 | 内容 |
|------|------|
| `modding\msvc\src\CesiumLoader\steamhack.h` | 原理总述 + 接口签名与每个参数的语义 |
| `modding\msvc\src\CesiumLoader\steamhack.cpp` | 全部实现;文件头注释有每个 hook 的编号与说明 |
| `modding\msvc\src\CesiumLoader\config.h` | 各 `steamBypass*` 字段的默认值与理由(**含 `steamBypassLobbyMethods=false` 与阶段5 `steamBypassLobbyQuery=true` 的完整说明**) |
| `modding\msvc\src\CesiumLoader\config.cpp` | 默认值落点 |
| `modding\msvc\src\CesiumLoader\il2cpp_safe.h` | IL2CPP 导出函数表(`array_length` / `gchandle_new` 是阶段5 新增的**可选**项) |
| `modding\msvc\src\CesiumLoader\loader.cpp`(约 `:621-655`) | 安装调用点与时机说明(含"三个开关都关才跳过 Steam 绕过"的判断) |
| `modding\msvc\dist\modloader\AstralParty_ModLoader\doorstop_config.json` | **分发用配置模板(单一来源)**;`staging\` 是打包脚本从它复制的 |
| `modding\msvc\tools\package-modloader.ps1` | 打包脚本 |
| `outputs\backup-before-steambypass\` | 回退用备份 |
