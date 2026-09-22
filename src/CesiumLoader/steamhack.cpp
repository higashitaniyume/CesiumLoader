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

#include "steamhack.h"

#include "loader.h"

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

namespace
{

// ---------- 日志小工具 ----------

std::string hex_str(uintptr_t v)
{
    return fmt::format("0x{:X}", static_cast<unsigned long long>(v));
}

// 某地址相对指定模块基址的偏移(用于日志: 直接跳这个 RVA 就能在 IDA/Ghidra 里看到代码)
std::string offset_in(HMODULE mod, const void* p)
{
    if (!mod) return "(模块未加载)";
    uintptr_t base = reinterpret_cast<uintptr_t>(mod);
    uintptr_t v = reinterpret_cast<uintptr_t>(p);
    if (v < base || v - base > 0x40000000ull) return "(不在该模块内)";
    return "+" + hex_str(v - base);
}

// 相对 GameAssembly.dll 基址的偏移(AOT 方法都应该落在这里面)
std::string rva_of(const void* p)
{
    HMODULE ga = GetModuleHandleW(L"GameAssembly.dll");
    if (!ga) return "(GameAssembly.dll 未加载)";
    return "RVA " + offset_in(ga, p);
}

// ---------- hook 1: SteamManager.Awake -> no-op ----------

// Awake 是实例方法、返回 void: 签名就是 void(void* self)。
// 我们**故意不调用原函数** —— 这正是绕过本身。
using Fn_Awake = void (*)(void* self);

Fn_Awake g_real_awake = nullptr;    // MinHook trampoline(保留备用; 不在回调里调用)
void* g_awake_target = nullptr;     // 原始 methodPointer(GameAssembly.dll 内的代码)
void* g_awake_method = nullptr;     // MethodInfo*(仅诊断/日志)
bool g_awake_hooked = false;
std::atomic<long> g_awake_hits{ 0 };

void Hook_Awake(void* /*self*/)
{
    // 直接返回 = Awake 的 Steam 校验根本没跑。
    // 只在第一次调用时记日志: Awake 只会被调一次, 但万一被反复调用也不该刷屏。
    if (g_awake_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] SteamManager.Awake 已被 no-op 拦截, Steam 校验未执行(游戏继续启动)");
}

// ---------- hook 2: SteamAPI_RestartAppIfNecessary -> 0 ----------

// Steamworks 的真实签名是 bool(uint32)(1 字节返回值, 走 AL)。
// 我们恒返回 0: EAX=0 时 AL 也是 0, 两种解释下都等价于 false = "无需重启, 继续启动"。
using Fn_RestartApp = int (*)(unsigned int);

Fn_RestartApp g_real_restart = nullptr;
void* g_restart_target = nullptr;
bool g_restart_hooked = false;

int Hook_RestartApp(unsigned int /*appid*/)
{
    return 0;
}

// ---------- MinHook 生命周期 ----------

bool g_mh_ready = false;   // 本模块自己记: MinHook 是否可用(不代表是我们初始化的)
bool g_installed = false;
int g_hook_count = 0;

// MinHook 是**进程级单例**: speedhack.cpp 的 speedhack_init() 已经调过 MH_Initialize()。
// 因此 MH_ERROR_ALREADY_INITIALIZED 必须当成成功, 不能因此放弃挂钩。
bool ensure_minhook()
{
    if (g_mh_ready) return true;
    MH_STATUS st = MH_Initialize();
    if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED)
    {
        log_line("[steamhack] MinHook 初始化失败: " + std::to_string((int)st));
        return false;
    }
    g_mh_ready = true;
    if (st == MH_ERROR_ALREADY_INITIALIZED)
        log_line("[steamhack] MinHook 已由 speedhack 初始化(复用, 不重复初始化)");
    return true;
}

// ---------- SteamManager.Awake 解析 ----------

// 候选命名空间: 已知 SteamManager 在**全局命名空间**(ns=""), 但为稳妥多试几个常见
// 命名空间; 实际命中哪个会写进日志, 便于后续迭代(把无效候选删掉或补上新候选)。
const char* kCandidateNamespaces[] = {
    "",                    // 全局命名空间(已确认的实际情况)
    "AstralParty",
    "AstralParty.Steam",
    "Game",
    "Steam",
    "Steamworks",
    "BnSdk",
    "Platform",
};

// 一处命中(同一个类名可能有多个镜像/命名空间)
struct AwakeHit
{
    void* method = nullptr;   // MethodInfo*
    std::string image;        // image 名(≈ assembly 名)
    std::string ns;           // 实际命中的命名空间
};

// 遍历域内全部程序集 image, 找 SteamManager.Awake(0 参数)。
// 返回选中的 MethodInfo*(找不到返回 nullptr), 实际命中的命名空间经 out_chosen_ns 回传;
// 过程中把每一步发现写进日志。
void* resolve_awake(const cesium_safe::il2cpp_tbl& t, std::string* out_chosen_ns)
{
    // 逐项检查用到的函数指针 —— 缺哪个就记哪个, 便于区分"导出缺失"和"真的没命中"
    struct { const char* name; const void* p; } need[] = {
        { "domain_get",               (const void*)t.domain_get },
        { "domain_get_assemblies",    (const void*)t.domain_get_assemblies },
        { "assembly_get_image",       (const void*)t.assembly_get_image },
        { "image_get_name",           (const void*)t.image_get_name },
        { "class_from_name",          (const void*)t.class_from_name },
        { "class_get_method_from_name", (const void*)t.class_get_method_from_name },
    };
    std::string missing;
    for (auto& n : need)
    {
        if (!n.p)
        {
            if (!missing.empty()) missing += ", ";
            missing += n.name;
        }
    }
    if (!missing.empty())
    {
        log_line("[steamhack] IL2CPP 函数表缺失: " + missing + " —— 无法解析 SteamManager");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line("[steamhack] il2cpp domain 为空, 无法解析 SteamManager");
        return nullptr;
    }

    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line("[steamhack] domain_get_assemblies 返回空");
        return nullptr;
    }

    std::vector<void*> images;
    std::vector<std::string> image_names;
    for (size_t i = 0; i < count; i++)
    {
        if (!assemblies[i]) continue;
        void* image = t.assembly_get_image(assemblies[i]);
        if (!image) continue;
        const char* n = t.image_get_name(image);
        images.push_back(image);
        image_names.push_back(n ? n : "(null)");
    }

    // 候选 image 名一次性打进日志: 确认 AOT 镜像(Assembly-CSharp.dll 之类)真的在枚举结果里,
    // 也确认此刻热更程序集有没有提前进来。
    std::string list;
    for (size_t i = 0; i < image_names.size(); i++)
    {
        if (i) list += ", ";
        list += image_names[i];
        if (i >= 199) { list += ", ..."; break; }   // 防御: 极端情况下不刷爆日志
    }
    log_line("[steamhack] 域内程序集 " + std::to_string(image_names.size()) + " 个: " + list);

    std::vector<AwakeHit> hits;
    int class_without_awake = 0;
    for (size_t i = 0; i < images.size(); i++)
    {
        for (const char* ns : kCandidateNamespaces)
        {
            void* cls = t.class_from_name(images[i], ns, "SteamManager");
            if (!cls) continue;

            void* m = t.class_get_method_from_name(cls, "Awake", 0);
            if (m)
            {
                AwakeHit h;
                h.method = m;
                h.image = image_names[i];
                h.ns = ns;
                hits.push_back(h);
                log_line("[steamhack] 命中 SteamManager.Awake: image=" + h.image +
                         " ns=\"" + h.ns + "\" MethodInfo=" + hex_str((uintptr_t)m) +
                         " methodPointer=" + hex_str(*reinterpret_cast<uintptr_t*>(m)) +
                         " (" + rva_of(*reinterpret_cast<void**>(m)) + ")");
                break;   // 同一个 image 不必再试其他命名空间
            }

            // 类命中但 Awake(0 参数)不存在: 说明类名对上了、方法名/签名不符。
            // 把类里所有相关方法名打出来, 下一轮可以直接改方法名。
            if (class_without_awake < 20)
            {
                class_without_awake++;
                log_line("[steamhack] 类命中但无 Awake(): image=" + image_names[i] +
                         " ns=\"" + std::string(ns) + "\"");
                if (t.class_get_methods && t.method_get_name)
                {
                    void* iter = nullptr;
                    std::string names;
                    for (;;)
                    {
                        void* mm = t.class_get_methods(cls, &iter);
                        if (!mm) break;
                        const char* mn = t.method_get_name(mm);
                        if (!mn) continue;
                        if (strstr(mn, "Awake") || strstr(mn, "Steam") ||
                            strstr(mn, "Init") || strstr(mn, "Start"))
                        {
                            if (!names.empty()) names += ", ";
                            names += mn;
                        }
                    }
                    log_line("[steamhack]   相关方法: " + (names.empty() ? std::string("(无)") : names));
                }
            }
        }
    }

    if (hits.empty())
    {
        log_line("[steamhack] 未找到 SteamManager.Awake(候选命名空间已全部试过) —— Steam 自杀门未被绕过");
        return nullptr;
    }

    // 选用优先级: 首选 AOT 镜像 Assembly-CSharp.dll(游戏自己的 C# 程序集, SteamManager 就在这里);
    // 否则退回第一处命中, 并把"共有几处命中"写进日志 —— 若这里挑了不合适的, 日志足以定位。
    size_t chosen = 0;
    if (hits.size() > 1)
    {
        for (size_t i = 0; i < hits.size(); i++)
        {
            if (hits[i].image.find("Assembly-CSharp") != std::string::npos) { chosen = i; break; }
        }
    }
    if (hits.size() > 1)
        log_line("[steamhack] 共 " + std::to_string(hits.size()) + " 处命中, 选用 image=" + hits[chosen].image);

    if (out_chosen_ns) *out_chosen_ns = hits[chosen].ns;
    return hits[chosen].method;
}

// ---------- hook 安装 ----------

bool install_awake(const cesium_safe::il2cpp_tbl& t)
{
    std::string ns;
    void* method = resolve_awake(t, &ns);
    if (!method) return false;

    // IL2CPP 的 MethodInfo 第一个字段就是 methodPointer(原生代码地址)。
    // 取字段而不是解析导出, 是为了避开 il2cpp_method_get_pointer 是否被裁剪的问题。
    void* target = *reinterpret_cast<void**>(method);
    if (!target)
    {
        log_line("[steamhack] SteamManager.Awake 的 methodPointer 为空(可能未 AOT 编译) —— 跳过");
        return false;
    }

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_Awake, (LPVOID*)&g_real_awake);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHook(Awake) 失败: " + std::to_string((int)st) +
                 " (target=" + hex_str((uintptr_t)target) + ")");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(Awake) 失败: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_awake_target = target;
    g_awake_method = method;
    g_awake_hooked = true;
    log_line("[steamhack] SteamManager.Awake inline hook 已启用 (no-op, ns=\"" + ns + "\")");
    return true;
}

// ---------- steam_api64.dll 路径推导 ----------
//
// <游戏 exe 目录>\AstralParty_CN_Data\Plugins\x86_64\steam_api64.dll
// 盘符与安装路径全部从宿主 exe(GetModuleFileNameW(nullptr))推导, 不硬编码。
std::wstring steam_api_path()
{
    wchar_t buf[1024];
    DWORD n = GetModuleFileNameW(nullptr, buf, 1024);
    if (n == 0 || n >= 1024) return L"";
    std::wstring exe(buf, n);
    size_t pos = exe.find_last_of(L"\\/");
    std::wstring dir = (pos == std::wstring::npos) ? L"." : exe.substr(0, pos);

    // 主路径(国服数据目录名)
    std::wstring primary = dir + L"\\AstralParty_CN_Data\\Plugins\\x86_64\\steam_api64.dll";
    std::error_code ec;
    if (fs::exists(primary, ec)) return primary;

    // 兜底: 扫描 <游戏目录>\*_Data\Plugins\x86_64\steam_api64.dll
    // (万一数据目录不是 AstralParty_CN_Data, 例如其他区域/渠道版本)
    std::error_code ec2;
    for (fs::directory_iterator it(dir, ec2), end; !ec2 && it != end; it.increment(ec2))
    {
        std::error_code ec3;
        if (!it->is_directory(ec3) || ec3) continue;
        std::wstring candidate = it->path().wstring() + L"\\Plugins\\x86_64\\steam_api64.dll";
        if (fs::exists(candidate, ec)) return candidate;
    }

    return primary;   // 不存在也返回主路径: 调用方会记录加载失败
}

bool install_restart_check()
{
    std::wstring path = steam_api_path();
    if (path.empty())
    {
        log_line("[steamhack] 无法推导游戏目录, 跳过 RestartAppIfNecessary 保险");
        return false;
    }

    // 先确保 steam_api64.dll 已在本进程内(可能游戏自己还没加载; 已加载则只是引用计数 +1)。
    // LOAD_WITH_ALTERED_SEARCH_PATH: 让该 DLL 的依赖也从它自己的目录解析。
    HMODULE steam = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    DWORD load_err = steam ? 0 : GetLastError();
    if (!steam) steam = GetModuleHandleW(L"steam_api64.dll");
    if (!steam)
    {
        log_line(L"[steamhack] steam_api64.dll 加载失败(" + std::to_wstring(load_err) +
                 L"), 跳过 RestartAppIfNecessary 保险: " + path);
        return false;
    }
    log_line(L"[steamhack] steam_api64.dll 已加载: " + path);

    // 自己解析一次地址: MH_CreateHookApi 不回传 target, 而 enable/disable/uninstall 都需要它。
    void* target = reinterpret_cast<void*>(GetProcAddress(steam, "SteamAPI_RestartAppIfNecessary"));
    if (!target)
    {
        log_line("[steamhack] steam_api64.dll 未导出 SteamAPI_RestartAppIfNecessary, 跳过该保险");
        return false;
    }
    log_line("[steamhack] SteamAPI_RestartAppIfNecessary = " + hex_str((uintptr_t)target) +
             " (" + offset_in(steam, target) + " steam_api64.dll)");

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHookApi(L"steam_api64.dll", "SteamAPI_RestartAppIfNecessary",
                                    (LPVOID)&Hook_RestartApp, (LPVOID*)&g_real_restart);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHookApi(RestartAppIfNecessary) 失败: " + std::to_string((int)st));
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(RestartAppIfNecessary) 失败: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_restart_target = target;
    g_restart_hooked = true;
    log_line("[steamhack] SteamAPI_RestartAppIfNecessary hook 已启用 (恒返回 0)");
    return true;
}

// =====================================================================================
// 阶段2: Steam 大厅匹配绕过 —— CreateLobbyAsync / JoinLobbyAsync
// =====================================================================================
//
// 故障(实测): Steam 全关 + 阶段1 生效时, 点"创建房间"界面毫无反应。BnSdk 错误上报日志里
// 抓到的真实异常:
//     NullReferenceException: Object reference not set to an instance of an object.
//     UI.UICom_CreateRoom.RequestCreateRoom ()
//     FairyGUI.EventBridge.CallInternal (FairyGUI.EventContext context)
//     FairyGUI.Stage.HandleMouseEvents ()
//     Steamworks.SteamMatchmaking.CreateLobbyAsync (System.Int32 maxMembers)
//     --- End of stack trace from previous location where exception was thrown ---
// 游戏**不崩**, 只是这个 async 异常把整个建房间流程吃掉。
//
// 为什么"返回空任务"就是正确修法(UICom_CreateRoom.cs:131-142 原文):
//     ulong steamLobbyId = 0uL;
//     Lobby? val = await SteamMatchmaking.CreateLobbyAsync(4);   // <- 真实实现抛 NRE
//     if (val.HasValue) { ...; steamLobbyId = SteamId.op_Implicit(((Lobby)(ref value)).Id); }
//     SimpleSingletonProvider<GameLogicManager>.inst.room.RequestCreateC2S(..., steamLobbyId, ...);
// steamLobbyId 默认就是 0, **只有** val.HasValue 时才被覆盖。所以只要让 CreateLobbyAsync
// 返回"已完成且结果为 null"的 Task<Lobby?>, 游戏就按"没有 Steam 大厅"正常建房 ——
// 协议里 LobbyId = 0 是合法的"无大厅"值。JoinLobbyAsync 是同类问题
// (RoomLogic.cs:157 加入房间 / WatchLogic.cs:141 观战), 一并处理。
//
// 通用辅助函数 make_default_completed_task(hookedMi) 不硬编码 Lobby: 返回值类型从被挂钩
// 方法自己的 MethodInfo 推导, 因此对 Task<Lobby?> 以外的返回类型也自动适配。

using Fn_CreateLobbyAsync = void* (*)(int32_t max_members);
// SteamId 是值类型(struct, 单个 ulong)。IL2CPP 的 AOT ABI 把值类型按**地址**传递,
// 所以这里收到的是"指向 SteamId 的指针"; 我们只把它写进日志, 不参与任何逻辑。
using Fn_JoinLobbyAsync = void* (*)(void* steam_id_ref);

Fn_CreateLobbyAsync g_real_create_lobby = nullptr;   // MinHook trampoline(保留备用)
Fn_JoinLobbyAsync g_real_join_lobby = nullptr;
void* g_cl_target = nullptr;   // CreateLobbyAsync 的原始 methodPointer
void* g_jl_target = nullptr;   // JoinLobbyAsync 的原始 methodPointer
void* g_cl_method = nullptr;   // CreateLobbyAsync 的 MethodInfo*(helper 的键)
void* g_jl_method = nullptr;   // JoinLobbyAsync 的 MethodInfo*
bool g_cl_hooked = false;
bool g_jl_hooked = false;
std::atomic<long> g_cl_hits{ 0 };
std::atomic<long> g_jl_hits{ 0 };

// ---------- hook 5: System.Nullable`1<Lobby>::get_HasValue -> 恒 false (阶段3) ----------
//
// 为什么是这个方法: UICom_CreateRoom.RequestCreateRoom(热更程序集, HybridCLR 解释执行)里
//     Lobby? val = await SteamMatchmaking.CreateLobbyAsync(4);
//     if (val.HasValue) { ... ((Lobby)(ref value)).SetPublic(); ... }   // 炸在这里
// 阶段2 已经让 CreateLobbyAsync 返回"已完成的空 Task", 但实测 val.HasValue 仍为 true ——
// 安装期探针里 get_Result() 返回非空装箱对象, 而"空 Nullable"装箱本应得 NULL
// (il2cpp_value_box 同样输入确实返回 NULL), 说明 il2cpp_runtime_invoke 把 Nullable<Lobby>
// 参数编组进 Task<T>..ctor(T) 时 hasValue 落成了非零。
//
// 修法(不再碰 Task 的构造): 热更代码的 val.HasValue 是一次真实的方法调用, 会 call 到
// AOT 方法 System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue() —— 直接把它换成恒 false,
// steamLobbyId 保持 0(协议里"无 Steam 大厅"合法值), RequestCreateC2S 正常发出。
//
// 签名: get_HasValue 是值类型实例方法, this 是**指向 Nullable 值的指针**(AOT ABI 值类型按地址传)。
// 返回值 bool: 寄存器返回(AL), 返回 0 即可。
// 第 2 个参数: il2cpp 的**共享泛型代码**会把运行时泛型上下文的 MethodInfo 作为隐藏参数传入。
//   本模块不依赖它(恒返回 false), 但**只读寄存器、不解引用**地记一次日志 —— 它能直接说明
//   "这个方法到底是不是所有 Nullable<T> 共享的同一份代码"(见 probe_nullable_generic_sharing)。
using Fn_HasValue = bool (*)(void* this_ptr, void* generic_ctx);

Fn_HasValue g_real_hasvalue = nullptr;   // MinHook trampoline(保留备用)
void* g_hv_target = nullptr;             // get_HasValue 的原始 methodPointer
void* g_hv_method = nullptr;             // get_HasValue 的 MethodInfo*(Nullable<Lobby> 实例)
bool g_hv_hooked = false;
std::atomic<long> g_hv_hits{ 0 };

bool Hook_NullableHasValue(void* this_ptr, void* generic_ctx)
{
    if (g_hv_hits.fetch_add(1, std::memory_order_relaxed) == 0)
    {
        const bool same = (generic_ctx != nullptr && generic_ctx == g_hv_method);
        log_line("[steamhack] Nullable<Lobby>.get_HasValue 首次被拦截: this=" +
                 hex_str(reinterpret_cast<uintptr_t>(this_ptr)) +
                 " 第2寄存器参数(共享泛型代码的隐藏上下文, 仅记原始值不解引用)=" +
                 hex_str(reinterpret_cast<uintptr_t>(generic_ctx)) +
                 " 与本 Instantiation 的 MethodInfo(" + hex_str(reinterpret_cast<uintptr_t>(g_hv_method)) +
                 ")一致=" + (same ? "是" : "否") +
                 " -> 恒返回 false(任何 Lobby? 都视同无值, 游戏侧 LobbyId 保持 0)");
    }
    return false;
}

// 造"默认返回值"的方式
enum TaskFactoryKind
{
    TF_NONE = 0,
    TF_FROM_RESULT,       // cls.FromResult(1)               —— 规格要求的主路径
    TF_CTOR_OBJECT_NEW,   // object_new(cls) + cls..ctor(1)  —— 等价兜底
    TF_COMPLETED_TASK,    // cls.get_CompletedTask()          —— 仅非泛型 Task 可用
};

// =====================================================================================
// 方案C: Task<T>..ctor(T) 的调用方式(config: steamBypassTaskCtorMode)
// =====================================================================================
//
// 实测根因(已定位, 不再调研): il2cpp_runtime_invoke 给 Task<T>..ctor(Nullable<Lobby>) 传
// 值类型参数时**编组不正确** —— 传进去的 Nullable 的 hasValue 落成了非零。证据: 安装期探针里
// il2cpp_field_get_value_object(m_result, task) 返回的是**非空**装箱对象, 而"空 Nullable"
// 按 .NET 语义装箱应得 NULL(il2cpp_value_box 在同样的零初始化输入下确实返回了 NULL)。
// 于是 await 回来后 val.HasValue 被判成 true -> UICom_CreateRoom.cs:136 SetPublic() NRE。
//
// 方案C: **不再走 runtime_invoke**, 用 *(void**)ctorMethodInfo 拿到 methodPointer 直接当原生
// 函数调用。Windows x64 ABI 下 >8 字节的结构体参数按引用(指针)传递, IL2CPP 生成的 AOT 代码
// 遵循同一套 ABI, 所以
//     void (*)(void* thisObj, void* pArg)
// 正好对应生成的 (Task<T>* this, Nullable<Lobby> result)。Nullable<Lobby> = bool hasValue +
// Lobby(内含 SteamId, 8B) = 9B -> 补齐 16B -> 走引用。256B 全零栈缓冲 = "hasValue=false"。
enum TaskCtorMode
{
    CTOR_MODE_AUTO = 0,   // 先 direct, 探针校验失败则自动退回 invoke(默认)
    CTOR_MODE_DIRECT = 1, // 只用 direct(原生 ABI); 失败即不挂钩
    CTOR_MODE_INVOKE = 2, // 只用 il2cpp_runtime_invoke(旧行为)
};

TaskCtorMode g_ctor_mode = CTOR_MODE_AUTO;
const char* g_ctor_mode_name = "auto";

// 解析配置字符串; 非法取值按 AUTO 处理并经 out_unknown 告知调用方(由调用方记日志)。
TaskCtorMode parse_ctor_mode(const std::string& s, bool* out_unknown)
{
    if (out_unknown) *out_unknown = false;
    if (s == "direct") return CTOR_MODE_DIRECT;
    if (s == "invoke") return CTOR_MODE_INVOKE;
    if (s.empty() || s == "auto") return CTOR_MODE_AUTO;
    if (out_unknown) *out_unknown = true;
    return CTOR_MODE_AUTO;
}

const char* ctor_mode_name(TaskCtorMode m)
{
    return (m == CTOR_MODE_DIRECT) ? "direct" : (m == CTOR_MODE_INVOKE) ? "invoke" : "auto";
}

// 探测成功时实际采用的路径(日志与运行期分发共用)
enum CtorPath
{
    CPATH_UNSET = 0,
    CPATH_DIRECT = 1,   // 原生 ABI 直调 .ctor methodPointer(方案C)
    CPATH_INVOKE = 2,   // il2cpp_runtime_invoke(旧路径)
};

const char* ctor_path_name(int p)
{
    return (p == CPATH_DIRECT) ? "direct(原生 ABI 直调 ctor, 绕过 runtime_invoke 编组)"
                               : "invoke(il2cpp_runtime_invoke)";
}

// 单个被挂钩方法的"默认返回值工厂": 安装期解析 + 探测一次, 命中路径只做一次缓存好的调用。
struct TaskFactory
{
    const char* label = "?";
    void* hooked_method = nullptr;   // MethodInfo*(键)
    void* ret_type = nullptr;        // Il2CppType*(被挂钩方法的返回值类型)
    void* ret_class = nullptr;       // Il2CppClass*(该 Il2CppType 对应的类, 即 Task<T>)
    std::string ret_type_name = "?";
    void* factory = nullptr;         // 用来造返回值的 MethodInfo*
    TaskFactoryKind kind = TF_NONE;
    bool resolved = false;
    bool ok = false;

    // ---- 方案C 新增 ----
    void* ctor_ptr = nullptr;        // .ctor(1) 的 methodPointer(原生函数入口, 直调用)
    int   ctor_path = CPATH_UNSET;   // 探测成功后实际采用的路径(CPATH_*): 运行期按它分发
    std::atomic<long> runtime_logged{ 0 }; // 运行期 direct 路径的详细日志只记一次

    // ---- 阶段3 新增: 工厂第一个参数(值类型)的类 ----
    // 为了装箱, 安装期已经从工厂参数解析出 pcls = class_from_system_type(type_get_object(
    // method_get_param(ctorMi, 0))), 这里把它**留下来复用**, 不再重复解析:
    // 它就是 System.Nullable`1<Steamworks.Data.Lobby>, 是 get_HasValue 挂钩和 hasValue
    // 字段诊断的入口。
    void* param_class = nullptr;                  // Il2CppClass*(Nullable<Lobby>)
    std::string param_class_name = "(未解析)";     // 类名(日志: 要能确认是 Nullable`1)
    bool param_is_valuetype = false;
    bool param_is_nullable = false;               // 类名/类型名里含 "Nullable"
    void* hasvalue_method = nullptr;              // Nullable<Lobby>.get_HasValue 的 MethodInfo*
    bool hasvalue_resolved = false;               // 已尝试解析(不等于成功)
    bool hasvalue_ok = false;                     // 真的挂上了

    std::atomic<long> failures{ 0 }; // 只记首次失败, 不刷屏
    std::atomic<long> box_logged{ 0 }; // 装箱路径只记一次(安装期那次必定记录), 运行期不刷屏
};

TaskFactory g_factory_cl;   // CreateLobbyAsync
TaskFactory g_factory_jl;   // JoinLobbyAsync

// steamhack_install 时保存一份函数表副本: 两个 hook 处理器需要它。
// (loader.cpp 的 g_safe_il2cpp 是静态的、生命周期足够, 这里复制一份更稳妥。)
cesium_safe::il2cpp_tbl g_t;
bool g_t_ready = false;

// 读 MethodInfo 的第一个字段 = methodPointer。IL2CPP 的稳定布局, 阶段1 的 Awake hook 也用它。
void* method_pointer_of(const void* mi)
{
    return mi ? *reinterpret_cast<void* const*>(mi) : nullptr;
}

std::string cstr_or(const char* s, const char* def)
{
    return s ? std::string(s) : std::string(def);
}

std::string class_name_of(const cesium_safe::il2cpp_tbl& t, void* cls)
{
    if (!cls) return "(null)";
    if (!t.class_get_name) return "(class_get_name 缺失)";
    return cstr_or(t.class_get_name(cls), "(null)");
}

// =====================================================================================
// SEH 安全边界(只允许 POD) —— 上一轮把游戏打死的正是这里的调用约定错误
// =====================================================================================
//
// 崩溃(实测, 0xC0000005 / GameAssembly.dll+0x291ec8):
//     GameAssembly  il2cpp_class_get_declaring_type
//     GameAssembly  il2cpp_field_get_value_object     <- 在这里解引用我们的 args[0]
//     GameAssembly  il2cpp_field_get_value_object
//     VERSION       steamhack.cpp:547 invoke_task_factory
//     VERSION       steamhack.cpp:899  resolve_task_factory
//     VERSION       steamhack.cpp:1060 install_lobby_hook
//     VERSION       steamhack.cpp:1211 steamhack_install
// 根因: il2cpp_runtime_invoke 对**值类型参数**要求传"装箱对象", 上一轮给 Nullable<Lobby>
// 传了 args[0] = nullptr, runtime_invoke 内部按装箱对象解引用 -> 访问违例(参数约定错误,
// 不是逻辑错误)。
//
// 修法两条, 缺一不可:
//   1) 参数按类型装箱: 值类型 -> il2cpp_value_box(零初始化栈缓冲区); 若它按 .NET 语义对
//      "空 Nullable"返回 NULL, 再用 il2cpp_object_new(同一个类)兜底拿一个非空装箱实例。
//      引用类型 -> 仍然传 nullptr。
//   2) 所有 runtime_invoke / value_box / object_new 调用都关在下面的 POD 函数里并加
//      __try/__except 兜底: 万一还有别的约定错误, 也只是"这一次调用失败"(安装期探针失败
//      -> 不挂钩, 运行期某次调用失败 -> 记日志返回 nullptr), 不再带走整个进程。
//
// 硬性约束: MSVC 不允许在"需要对象展开(unwinding)"的函数里使用 __try(C2712), 所以下面两个
// 函数体内**只允许 POD**(指针/整数/char 数组), 不得出现 std::string/std::vector 等需要析构
// 的对象 —— 所有日志与字符串拼接都留给调用方(普通 C++ 函数)。

// 装箱路径(诊断用: 日志里要能看出走的是哪条路)
enum BoxPath
{
    BOX_NONE = 0,              // 0 参数工厂, 或引用类型参数 -> args[0] 保持 nullptr
    BOX_VALUE_BOX = 1,         // il2cpp_value_box 成功
    BOX_OBJNEW_AFTER_NULL = 2, // il2cpp_value_box 返回 NULL(空 Nullable 的 .NET 语义) -> object_new 兜底
    BOX_OBJNEW_NO_EXPORT = 3,  // il2cpp_value_box 导出缺失 -> 直接用 object_new
    BOX_FAILED_UNKNOWN = 4,    // 判不出参数是不是值类型 -> 不冒险, 本次失败
    BOX_FAILED_ALL = 5,        // 是值类型, 但两条装箱路都拿不到装箱对象 -> 本次失败
};

// POD 结果(无构造函数/无析构函数, 可以安全地在 __try 函数里当局部变量)
struct PodTaskInvoke
{
    void* object;              // runtime_invoke 造出来的任务对象(nullptr = 失败)
    void* exception;           // 托管异常对象(nullptr = 无)
    unsigned long seh_code;    // 捕获到的 SEH 异常码(0 = 未捕获)
    void* boxed_arg;           // 实际传给 args[0] 的装箱对象(诊断)
    int box_path;              // BOX_*
    int param_count;           // 工厂方法参数个数
    int param_is_valuetype;    // -1 未知 / 0 否 / 1 是
    int invoke_done;           // 1 = 真的执行到了 runtime_invoke
    int obj_alloc_ok;          // 构造函数路径: il2cpp_object_new 是否成功(非该路径恒为 1)
};

// 【危险调用 #1】装箱参数 + 调工厂造 Task。全程 __try 兜底。
// 只做 POD 操作, 返回值全部经 out 回传; 不做任何日志。
void pod_invoke_task_factory(const cesium_safe::il2cpp_tbl* t,
                             void* factory, void* ret_class, int is_ctor_object_new,
                             PodTaskInvoke* out)
{
    out->object = nullptr;
    out->exception = nullptr;
    out->seh_code = 0;
    out->boxed_arg = nullptr;
    out->box_path = BOX_NONE;
    out->param_count = 0;
    out->param_is_valuetype = -1;
    out->invoke_done = 0;
    out->obj_alloc_ok = 1;

    void* args[1] = { nullptr };
    void* obj = nullptr;

    __try
    {
        int pc = 0;
        if (factory && t->method_get_param_count)
            pc = (int)t->method_get_param_count(factory);
        out->param_count = pc;

        int proceed = 1;
        if (pc >= 1)
        {
            // ---- 取工厂第一个参数的类型, 判定是否值类型 ----
            void* pt = (t->method_get_param ? t->method_get_param(factory, 0) : nullptr);
            void* pcls = nullptr;
            if (pt && t->type_get_object && t->class_from_system_type && t->class_is_valuetype)
            {
                void* pto = t->type_get_object(pt);
                if (pto) pcls = t->class_from_system_type(pto);
            }

            if (!pcls)
            {
                // 判不出参数类型时绝不"猜 null 能行" —— 那正是上一轮的崩溃姿势
                out->box_path = BOX_FAILED_UNKNOWN;
                proceed = 0;
            }
            else if (!t->class_is_valuetype(pcls))
            {
                out->param_is_valuetype = 0;   // 引用类型: null 就是 null
                out->box_path = BOX_NONE;
            }
            else
            {
                out->param_is_valuetype = 1;   // 值类型: 必须装箱
                // 零初始化的栈缓冲区 = "默认值"的原始字节。Nullable<Lobby> 远小于 256 字节
                // (bool hasValue + Lobby), 零初始化即"空 Nullable"(hasValue=false)。
                unsigned char buf[256];
                for (int i = 0; i < 256; ++i) buf[i] = 0;

                void* boxed = nullptr;
                if (t->value_box)
                {
                    boxed = t->value_box(pcls, buf);
                    out->box_path = boxed ? BOX_VALUE_BOX : BOX_OBJNEW_AFTER_NULL;
                }
                else
                {
                    out->box_path = BOX_OBJNEW_NO_EXPORT;
                }

                // 安全网: value_box 对空 Nullable 可能返回 NULL(.NET 装箱语义), 换成
                // object_new 得到的"零初始化装箱实例" —— 保证 args[0] 非空。
                if (!boxed && t->object_new) boxed = t->object_new(pcls);

                if (boxed)
                {
                    args[0] = boxed;
                    out->boxed_arg = boxed;
                }
                else
                {
                    out->box_path = BOX_FAILED_ALL;
                    proceed = 0;
                }
            }
        }

        if (proceed && is_ctor_object_new)
        {
            obj = (t->object_new ? t->object_new(ret_class) : nullptr);
            if (!obj) { out->obj_alloc_ok = 0; proceed = 0; }
        }

        if (proceed && t->runtime_invoke && factory)
        {
            void* exc = nullptr;
            void* r = t->runtime_invoke(factory, obj, args, &exc);
            out->invoke_done = 1;
            out->exception = exc;
            if (r)
                out->object = r;
            else if (is_ctor_object_new)
                out->object = obj;   // 构造函数返回 void -> runtime_invoke 返回 NULL, 构造好的对象就是 obj
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // 捕获访问违例等 SEH 异常: 只作废这一次调用, 进程继续。
        out->seh_code = GetExceptionCode();
        out->object = nullptr;
        out->exception = nullptr;
        out->invoke_done = 0;
    }
}

// 【危险调用 #1b】方案C: 直接用**原生 ABI** 调用 Task<T>..ctor(T) 的 methodPointer, 绕过
// il2cpp_runtime_invoke 的参数编组(编组正是把 Nullable<Lobby>.hasValue 弄成非零的中间层)。
//
// 依据(Windows x64 ABI): 大于 8 字节的结构体参数按**引用**传递 —— 调用方在栈上放一份副本,
// 把它的地址放进 RDX。IL2CPP 的 AOT 代码是 MSVC 编译的 C++、遵循同一套 ABI, 所以
//     void (*)(void* thisObj, void* pArg, void* method_info)
// 正好对应生成的 (Task<T>* this, Nullable<Lobby> result, const MethodInfo* method)。
// Nullable<Lobby> = bool hasValue + Lobby(内含 SteamId, 8B) = 9B -> 补齐 16B -> 走引用。
// 256 字节**全零**缓冲区 = "hasValue=false 的空 Nullable"的原始字节(零初始化即 default)。
// **不再装箱**: 装箱是 runtime_invoke 那条路的中间层, 方案C 直接绕开它。
// 第 3 个参数(MethodInfo*)按 IL2CPP 惯例补上: 用不到的实例化会忽略它, 用得到的实例化若收到垃圾
// 指针就可能出问题 —— 传正确的 f.factory 是零成本的保险。
//
// 全程 __try 兜底 + 只允许 POD: 万一 ABI 假设不成立, 也只是一次访问违例被捕获 -> 该路径作废
// (auto 模式下自动退回 invoke), 绝不会把游戏打死。
struct PodDirectCtor
{
    void* object;              // object_new 分配的 Task 对象(nullptr = 失败)
    unsigned long seh_code;    // 捕获到的 SEH 异常码(0 = 未捕获)
    int   alloc_ok;            // il2cpp_object_new 是否成功
    int   called;              // 是否真的把 ctor 当原生函数调用过
    int   skipped;             // 1 = 未尝试(工厂不是 .ctor(1), 或缺 object_new/ctor_ptr)
    int   buffer_bytes;        // 传给构造函数的缓冲区大小(固定 256, 写进日志便于说明)
    int   buf_first16_zero;    // 缓冲区前 16 字节是否全零(自检: 传进去的确实是空 Nullable)
};

void pod_call_task_ctor_direct(void* object_new_fn, void* task_cls, void* ctor_ptr, void* ctor_mi,
                              void** out_obj, PodDirectCtor* out)
{
    out->object = nullptr;
    out->seh_code = 0;
    out->alloc_ok = 0;
    out->called = 0;
    out->skipped = 0;
    out->buffer_bytes = 256;
    out->buf_first16_zero = 0;
    if (out_obj) *out_obj = nullptr;

    if (!object_new_fn || !task_cls || !ctor_ptr)
    {
        out->skipped = 1;
        return;
    }

    __try
    {
        typedef void* (*ObjNewFn)(void*);
        void* obj = ((ObjNewFn)object_new_fn)(task_cls);
        if (!obj) return;
        out->alloc_ok = 1;

        // 16 字节对齐: Nullable<T> 的对齐要求是 8, 这里给 16 更保险(不依赖编译器的字符数组对齐)
        alignas(16) unsigned char buf[256];
        for (int i = 0; i < 256; ++i) buf[i] = 0;

        int z = 1;
        for (int i = 0; i < 16; ++i) if (buf[i] != 0) { z = 0; break; }
        out->buf_first16_zero = z;

        // ---- 核心: 原生 ABI 直调(不经 il2cpp_runtime_invoke) ----
        // 第 3 个参数按 IL2CPP 生成代码的惯例补上 MethodInfo* —— AOT 函数签名常常是
        //   void Task_1_ctor(Task_1_t* __this, T_t result, const MethodInfo* method)
        // 即使该实例化用不到它, 多传一个寄存器参数也无害; 万一用得到, 传正确的指针才不会读到垃圾。
        typedef void (*CtorFn)(void* this_obj, void* p_arg, void* method_info);
        out->called = 1;
        ((CtorFn)ctor_ptr)(obj, (void*)buf, ctor_mi);

        out->object = obj;
        if (out_obj) *out_obj = obj;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // 捕获访问违例等 SEH 异常: 只作废这一次调用(该路径失败), 进程继续。
        out->seh_code = GetExceptionCode();
        out->object = nullptr;
        if (out_obj) *out_obj = nullptr;
    }
}

// 【危险调用 #2】单次 runtime_invoke(用于 IsCompleted / get_Result 这类取值)。同样 SEH 兜底。
void pod_runtime_invoke(void* invoke_fn, void* method, void* obj, void** args,
                        void** out_result, void** out_exc, unsigned long* out_seh)
{
    *out_result = nullptr;
    *out_exc = nullptr;
    *out_seh = 0;
    if (!invoke_fn || !method) return;

    typedef void* (*InvokeFn)(void*, void*, void**, void**);
    InvokeFn fn = reinterpret_cast<InvokeFn>(invoke_fn);

    __try
    {
        void* exc = nullptr;
        void* r = fn(method, obj, args, &exc);
        *out_result = r;
        *out_exc = exc;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *out_seh = GetExceptionCode();
        *out_result = nullptr;
        *out_exc = nullptr;
    }
}

// =====================================================================================
// 【危险调用 #3/#4】Nullable<T> 的 hasValue 字段读取(纯诊断) —— 同样只允许 POD + SEH 兜底
// =====================================================================================
//
// 需求(重要): "构造出样本 Task 后, 再尝试读出该 Nullable<Lobby> 的 hasValue 字段实际值"。
// 这是把"Task 里存的到底是有值还是无值"从推理变成**事实**的唯一办法 —— 也直接决定阶段3 的
// 挂钩是否真的必要。读取用 il2cpp 自己的字段 API(字段偏移由 il2cpp 给出, 不猜字节位置):
//     il2cpp_class_get_field_from_name(cls, "hasValue")  -> FieldInfo*
//     il2cpp_field_get_offset(field)                     -> 字节偏移(权威)
//     il2cpp_field_get_value(field, data, &byte)         -> 按字段类型取值(这里是 bool, 1 字节)
// 这四个导出都是**可选**的: 缺任何一个就如实记"未能读取 hasValue 字段", 不硬来、不猜、不崩。

// 一次 hasValue 读取的全部结果(纯 POD, 可以安全地在 __try 函数里当局部变量)
struct PodNullableHasValue
{
    void* field;               // hasValue 的 FieldInfo*(nullptr = 类上没找到该字段)
    int   field_by;            // 1 = "hasValue", 2 = "has_value"(不同 corlib 的字段拼写), 0 = 未找到
    int   have_offset;         // il2cpp_field_get_offset 是否可调用
    unsigned long long offset; // hasValue 字段在结构里的字节偏移
    int   have_direct;         // 是否用"数据指针 + offset"直接读到了字节
    int   direct_value;        // 直接读到的字节(0/1)
    int   have_fieldget;       // 是否用 il2cpp_field_get_value 读到了
    int   fieldget_value;      // field_get_value 读到的字节(0/1)
    int   data_from_unbox;     // 1 = il2cpp_object_unbox, 2 = 按 Il2CppObject 头 +16 回退, 0 = 没拿到数据指针
    unsigned long seh_code;    // 捕获到的 SEH 异常码(0 = 未捕获)
};

// 【危险调用 #3】读一个 Nullable<T> 的 hasValue。
//   nullable_cls = Nullable<T> 的 Il2CppClass*
//   ptr          = is_boxed ? 装箱对象指针 : 未装箱 struct 的数据指针
// 两个来源都读一遍(直接偏移 + il2cpp_field_get_value), 互为交叉验证。
void pod_read_nullable_hasvalue(const cesium_safe::il2cpp_tbl* t, void* nullable_cls,
                                void* ptr, int is_boxed, PodNullableHasValue* out)
{
    out->field = nullptr;
    out->field_by = 0;
    out->have_offset = 0;
    out->offset = 0;
    out->have_direct = 0;
    out->direct_value = 0;
    out->have_fieldget = 0;
    out->fieldget_value = 0;
    out->data_from_unbox = 0;
    out->seh_code = 0;

    if (!t->class_get_field_from_name || !nullable_cls || !ptr) return;

    __try
    {
        // ---- 1) 找 hasValue 字段(两种拼写都试: .NET/Mono 元数据里都出现过) ----
        void* f = t->class_get_field_from_name(nullable_cls, "hasValue");
        if (f) out->field_by = 1;
        else
        {
            f = t->class_get_field_from_name(nullable_cls, "has_value");
            if (f) out->field_by = 2;
        }
        out->field = f;
        if (!f) return;

        // ---- 2) 权威偏移 + 数据指针 ----
        if (t->field_get_offset)
        {
            out->offset = (unsigned long long)t->field_get_offset(f);
            out->have_offset = 1;
        }

        char* data = nullptr;
        if (is_boxed)
        {
            if (t->object_unbox) { data = (char*)t->object_unbox(ptr); out->data_from_unbox = 1; }
            else { data = (char*)ptr + 16; out->data_from_unbox = 2; }   // Il2CppObject 头 = klass+monitor = 16
        }
        else
        {
            data = (char*)ptr;
            out->data_from_unbox = 0;
        }

        // ---- 3) 直接按偏移读 1 字节(bool) ----
        if (data && out->have_offset)
        {
            out->direct_value = (int)(unsigned char)data[out->offset];
            out->have_direct = 1;
        }

        // ---- 4) 交叉验证: 让 il2cpp 自己按字段类型取值 ----
        // 缓冲区给足 32 字节(只读首字节当 bool): 不同版本的 il2cpp 对"取值拷贝多少字节"
        // 用的是字段类型尺寸或类的 instance_size, 给够空间才不会被写坏栈。
        if (t->field_get_value && data)
        {
            unsigned char b[32];
            for (int i = 0; i < 32; ++i) b[i] = 0;
            t->field_get_value(f, data, b);
            out->fieldget_value = (int)b[0];
            out->have_fieldget = 1;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        out->seh_code = GetExceptionCode();
        out->have_direct = 0;
        out->have_fieldget = 0;
    }
}

// 【诊断/复用】只解析 Nullable<T> 的 hasValue 字段与偏移(不读值)。
// 供"探测 Task 的 m_result"用: 先拿到 hasValue 在 Nullable 里的内部偏移, 再定位 m_result 内的字节。
struct PodFieldOffset
{
    void* field;               // hasValue 的 FieldInfo*
    int   field_by;            // 1 = "hasValue", 2 = "has_value", 0 = 未找到
    int   have_offset;
    unsigned long long offset;
    unsigned long seh_code;
};

void pod_nullable_hasvalue_field(const cesium_safe::il2cpp_tbl* t, void* nullable_cls, PodFieldOffset* out)
{
    out->field = nullptr;
    out->field_by = 0;
    out->have_offset = 0;
    out->offset = 0;
    out->seh_code = 0;

    if (!t->class_get_field_from_name || !nullable_cls) return;

    __try
    {
        void* f = t->class_get_field_from_name(nullable_cls, "hasValue");
        if (f) out->field_by = 1;
        else
        {
            f = t->class_get_field_from_name(nullable_cls, "has_value");
            if (f) out->field_by = 2;
        }
        out->field = f;
        if (f && t->field_get_offset)
        {
            out->offset = (unsigned long long)t->field_get_offset(f);
            out->have_offset = 1;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        out->seh_code = GetExceptionCode();
        out->have_offset = 0;
    }
}

// 【危险调用 #4】读"样本 Task 的 m_result 字段里那个 Nullable<T>" 的 hasValue。
// 两条路:
//   A(权威): il2cpp_field_get_value_object(m_result, task) —— 让 il2cpp 自己按偏移取字段并装箱。
//            返回 NULL 就等于(按 il2cpp 的空 Nullable 装箱语义)hasValue = false。
//   B(次要): 直接按偏移读 (char*)task + m_result.offset (+ hasValue 的内部偏移)。
//            这条依赖"引用类型字段偏移已包含 16 字节对象头"的假设, 所以只作为交叉参考。
struct PodTaskResultProbe
{
    void* field;                // m_result 的 FieldInfo*
    int   found;
    int   have_offset;
    unsigned long long offset;
    int   value_object_called;  // 调用了 il2cpp_field_get_value_object
    int   value_object_is_null; // 它返回了 NULL(= 空 Nullable)
    int   have_inner;           // 从它返回的装箱对象里成功读出了 hasValue
    int   inner_value;          // 读出的 hasValue(0/1)
    int   inner_data_from_unbox;
    int   have_direct;          // 路 B 是否读到了
    int   direct_value;         // 路 B 读到的字节(0/1)
    unsigned long seh_code;
};

void pod_probe_task_result_nullable(const cesium_safe::il2cpp_tbl* t,
                                    void* task_cls, void* task_obj,
                                    void* nullable_cls,
                                    unsigned long long inner_offset, int have_inner_offset,
                                    PodTaskResultProbe* out)
{
    out->field = nullptr;
    out->found = 0;
    out->have_offset = 0;
    out->offset = 0;
    out->value_object_called = 0;
    out->value_object_is_null = 0;
    out->have_inner = 0;
    out->inner_value = 0;
    out->inner_data_from_unbox = 0;
    out->have_direct = 0;
    out->direct_value = 0;
    out->seh_code = 0;

    if (!t->class_get_field_from_name || !task_cls || !task_obj) return;

    __try
    {
        // Task<T> 的结果字段名: Mono 的 Future.cs 里是 m_result
        void* f = t->class_get_field_from_name(task_cls, "m_result");
        out->field = f;
        if (!f) return;
        out->found = 1;
        if (t->field_get_offset)
        {
            out->offset = (unsigned long long)t->field_get_offset(f);
            out->have_offset = 1;
        }

        // ---- 路 A: il2cpp 自己取值(权威, 不依赖我们理解偏移语义) ----
        if (t->field_get_value_object)
        {
            void* v = t->field_get_value_object(f, task_obj);
            out->value_object_called = 1;
            if (!v)
            {
                out->value_object_is_null = 1;   // 空 Nullable -> il2cpp 装箱得 NULL
            }
            else if (nullable_cls)
            {
                // 嵌套调用另一个 SEH 边界函数: 只读, 失败也只影响这一次诊断
                PodNullableHasValue sub;
                pod_read_nullable_hasvalue(t, nullable_cls, v, 1, &sub);
                out->have_inner = sub.have_direct ? sub.have_direct : sub.have_fieldget;
                out->inner_value = sub.have_direct ? sub.direct_value : sub.fieldget_value;
                out->inner_data_from_unbox = sub.data_from_unbox;
            }
        }

        // ---- 路 B: 直接按偏移读(参考值; 依赖引用类型字段偏移含对象头的假设) ----
        if (out->have_offset && have_inner_offset)
        {
            const char* p = (const char*)task_obj + out->offset + inner_offset;
            out->direct_value = (int)(unsigned char)*p;
            out->have_direct = 1;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        out->seh_code = GetExceptionCode();
        out->value_object_called = 0;
        out->value_object_is_null = 0;
        out->have_inner = 0;
        out->have_direct = 0;
    }
}

// 【危险调用 #5】解析 Nullable<Lobby>.get_HasValue 的 MethodInfo* 与其 methodPointer。
// 需求 #7: 解析本身也包在 POD + __try/__except 里 —— 元数据解析万一因 il2cpp 内部状态异常
// 而访问违例, 也只是这一次解析失败(只记日志、不挂钩), 绝不让安装期崩掉游戏。
struct PodHasValueMethod
{
    void* mi;                 // get_HasValue 的 MethodInfo*
    void* ptr;                // 其 methodPointer
    unsigned long seh_code;
};

void pod_resolve_hasvalue_method(const cesium_safe::il2cpp_tbl* t, void* nullable_cls, PodHasValueMethod* out)
{
    out->mi = nullptr;
    out->ptr = nullptr;
    out->seh_code = 0;

    if (!t->class_get_method_from_name || !nullable_cls) return;

    __try
    {
        void* mi = t->class_get_method_from_name(nullable_cls, "get_HasValue", 0);
        out->mi = mi;
        // MethodInfo 第一个字段 = methodPointer(IL2CPP 的稳定布局, 全场一致用法)
        if (mi) out->ptr = *reinterpret_cast<void* const*>(mi);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        out->seh_code = GetExceptionCode();
        out->mi = nullptr;
        out->ptr = nullptr;
    }
}

// ---------- SEH 边界之外: 日志与语义判断(普通 C++, 可以用 std::string) ----------

std::string hex_code_str(unsigned long code)
{
    return fmt::format("0x{:08X}", code);
}

// 装箱路径 -> 可读描述
std::string box_path_desc(int path, const PodTaskInvoke& r)
{
    switch (path)
    {
    case BOX_VALUE_BOX:
        return "il2cpp_value_box(256B 零初始化栈缓冲区)成功 -> 装箱对象 " + hex_str((uintptr_t)r.boxed_arg);
    case BOX_OBJNEW_AFTER_NULL:
        return "il2cpp_value_box 返回 NULL(空 Nullable 的 .NET 装箱语义) -> 改用 il2cpp_object_new 兜底 -> " +
               hex_str((uintptr_t)r.boxed_arg);
    case BOX_OBJNEW_NO_EXPORT:
        return "il2cpp_value_box 导出缺失 -> 改用 il2cpp_object_new 兜底 -> " + hex_str((uintptr_t)r.boxed_arg);
    case BOX_FAILED_UNKNOWN:
        return "无法判定参数是否为值类型(缺 method_get_param/type_get_object/class_from_system_type/"
               "class_is_valuetype) -> 本次失败(绝不冒险传 null)";
    case BOX_FAILED_ALL:
        return "值类型参数, 但 il2cpp_value_box 与 il2cpp_object_new 两条路都拿不到装箱对象 -> 本次失败";
    default:
        return (r.param_count >= 1) ? std::string("引用类型参数 -> args[0]=nullptr(合法)")
                                    : std::string("工厂 0 参数 -> 不需要参数");
    }
}

// =====================================================================================
// 方案C: direct 路径(原生 ABI 直调 .ctor) —— 造对象 + 记日志
// =====================================================================================

// 尝试用 direct 路径造一个 Task 对象。返回对象(nullptr = 该路径失败)。
// 详细结果经 out 回传(POD, 供调用方决定怎么记日志): 安装期全量记, 运行期只记首次。
void* run_direct_ctor(const cesium_safe::il2cpp_tbl& t, const TaskFactory& f, PodDirectCtor* out)
{
    out->object = nullptr;
    out->seh_code = 0;
    out->alloc_ok = 0;
    out->called = 0;
    out->skipped = 1;
    out->buffer_bytes = 256;
    out->buf_first16_zero = 0;

    // direct 只对"类..ctor(1) + il2cpp_object_new"这条工厂路径有意义:
    // FromResult / CompletedTask 返回的都是**引用类型**, 用 runtime_invoke 才是对的。
    if (f.kind != TF_CTOR_OBJECT_NEW || !f.ctor_ptr || !t.object_new) return nullptr;

    void* obj = nullptr;
    // f.factory 就是 Task<T>..ctor(T) 的 MethodInfo*, 作为第 3 个参数一并传给 AOT 代码(见 POD 函数的说明)。
    pod_call_task_ctor_direct((void*)t.object_new, f.ret_class, f.ctor_ptr, f.factory, &obj, out);
    return obj;
}

// 把一次 direct 尝试的结果写成日志 —— 这是"direct 到底有没有把 hasValue 变成 false"的
// 第一手证据链: ctor 入口 / 零初始化缓冲区自检 / 调用结果 / SEH。
// phase 说明这是安装期探测还是运行期第几次。
void log_direct_attempt(const cesium_safe::il2cpp_tbl& t, const TaskFactory& f,
                        const PodDirectCtor& r, void* obj, const char* phase)
{
    const std::string tag = std::string("[steamhack] ") + f.label + "[direct](" + phase + "): ";
    if (r.skipped)
    {
        log_line(tag + "不适用 —— 工厂不是 类..ctor(1)(或缺少 ctor methodPointer / il2cpp_object_new), "
                      "direct 路径未被尝试");
        return;
    }

    std::string s = tag + "原生 ABI 直调 Task<T>..ctor(T), 绕过 il2cpp_runtime_invoke: ";
    s += "ctor methodPointer=" + hex_str((uintptr_t)f.ctor_ptr);
    s += " 目标类=" + class_name_of(t, f.ret_class);
    s += " il2cpp_object_new=" + std::string(r.alloc_ok ? "成功" : "失败");
    s += " 参数=指向 " + std::to_string(r.buffer_bytes) + " 字节全零栈缓冲区(";
    s += r.buf_first16_zero ? "前16B确认全零 -> 传进去的是空 Nullable(hasValue=false)"
                            : "前16B非零(异常, 应不可能)";
    s += ")";

    if (r.seh_code)
        s += " | 原生调用触发 SEH 异常 " + hex_code_str(r.seh_code) +
             " —— 已被 __try/__except 捕获, **本路径作废**";
    else if (!r.called)
        s += " | 未执行调用(见上)";
    else
        s += std::string(" | 原生调用完成, 返回对象=") +
             (obj ? hex_str((uintptr_t)obj) : std::string("(空)"));
    log_line(s);
}

// hasValue 读取结果 -> 可读描述。**必须写明"读到没有"**: 字段找不到 / 导出缺失 / SEH 异常
// 都要如实写出来(需求原文: 做不到就说明做不到, 不要硬来)。
std::string hasvalue_desc(const PodNullableHasValue& h, bool boxed)
{
    if (h.seh_code)
        return "读取触发 SEH 异常 " + hex_code_str(h.seh_code) +
               " —— 已被 __try/__except 捕获, 未能读取 hasValue 字段";
    if (!h.field)
        return "未能读取 hasValue 字段(类上既没有 \"hasValue\" 也没有 \"has_value\", 或 "
               "il2cpp_class_get_field_from_name 导出缺失)";

    std::string s = "hasValue 字段 FieldInfo=" + hex_str((uintptr_t)h.field) +
                    " 名称=" + std::string(h.field_by == 1 ? "\"hasValue\"" : "\"has_value\"");
    s += h.have_offset ? (" 偏移=" + std::to_string(h.offset))
                       : std::string(" 偏移=(il2cpp_field_get_offset 导出缺失, 未取到)");
    s += h.have_direct ? (" 直接按偏移读=" + std::string(h.direct_value ? "true" : "false"))
                       : std::string(" 直接按偏移读=(未取到)");
    s += h.have_fieldget ? (" il2cpp_field_get_value=" + std::string(h.fieldget_value ? "true" : "false"))
                         : std::string(" il2cpp_field_get_value=(导出缺失或未取到)");
    if (boxed)
        s += " (数据指针来源: " +
             std::string(h.data_from_unbox == 1 ? "il2cpp_object_unbox"
                                                : (h.data_from_unbox == 2 ? "按 Il2CppObject 头 +16 回退"
                                                                          : "未取到")) +
             ")";
    return s;
}

// 诊断(需求核心): 读"样本 Task 的 m_result 字段里那个 Nullable<Lobby>" 的 hasValue 真实值。
// 这一步**直接证实**构造出来的 Task 到底是有值还是无值。
// 纯日志: 读不到(缺导出 / 找不到字段 / SEH)一律如实写"未能读取 hasValue 字段", 不影响返回值。
// **返回值** = 判定结论(供调用方打一条明确的"结论"行):
//     1  = hasValue 为 true(非空)
//     0  = hasValue 为 false(空 Nullable)
//    -1  = 未能判定(读不到)
// 判定优先级: 权威路径 il2cpp_field_get_value_object 优先; 它没走通才退回"按偏移直读"(参考值,
// 依赖"引用类型字段偏移含 16B 对象头"的假设, 单独看不予采信)。
int log_sample_task_hasvalue(const cesium_safe::il2cpp_tbl& t, void* probe, void* task_cls,
                             const TaskFactory& f, const char* label)
{
    if (!f.param_class)
    {
        log_line(std::string("[steamhack] ") + label +
                 ": 未能读取 hasValue 字段(工厂参数类未解析, 没有 Nullable 类可用)");
        return -1;
    }

    // hasValue 在 Nullable 结构里的内部偏移(权威值来自 il2cpp_field_get_offset)
    PodFieldOffset inner;
    pod_nullable_hasvalue_field(&t, f.param_class, &inner);

    PodTaskResultProbe tr;
    pod_probe_task_result_nullable(&t, task_cls, probe, f.param_class,
                                   inner.have_offset ? inner.offset : 0ULL,
                                   inner.have_offset, &tr);

    std::string msg = std::string("[steamhack] ") + label + ": 样本 Task 的 m_result(" +
                      f.param_class_name + ") hasValue 诊断 -> ";
    if (tr.seh_code)
    {
        msg += "读取触发 SEH 异常 " + hex_code_str(tr.seh_code) +
               " —— 已被 __try/__except 捕获, 未能读取 hasValue 字段";
    }
    else if (!tr.found)
    {
        msg += "未能读取 hasValue 字段(Task 类上找不到 m_result, 或 "
               "il2cpp_class_get_field_from_name 导出缺失)";
    }
    else
    {
        msg += "m_result FieldInfo=" + hex_str((uintptr_t)tr.field);
        msg += tr.have_offset ? (" 偏移=" + std::to_string(tr.offset))
                              : std::string(" 偏移=(il2cpp_field_get_offset 导出缺失)");
        if (tr.value_object_called)
        {
            if (tr.value_object_is_null)
                msg += " | il2cpp_field_get_value_object -> NULL(空 Nullable 的装箱语义 => hasValue=false)";
            else if (tr.have_inner)
                msg += " | il2cpp_field_get_value_object -> 装箱对象, 读出 hasValue=" +
                       std::string(tr.inner_value ? "true" : "false");
            else
                msg += " | il2cpp_field_get_value_object -> 装箱对象, 但未能读出其中的 hasValue 字段";
        }
        else
        {
            msg += " | il2cpp_field_get_value_object 导出缺失, 未走权威路径";
        }
        if (tr.have_direct)
            msg += std::string(" | 次要(按偏移直读, 假设引用类型字段偏移含 16B 对象头)=") +
                   (tr.direct_value ? "true" : "false");
        else
            msg += " | 次要(按偏移直读)=(未取到)";
    }
    log_line(msg);

    // ---- 判定结论(调用方会用它打一条"结论"行) ----
    if (tr.seh_code || !tr.found) return -1;
    if (tr.value_object_called && tr.value_object_is_null) return 0;   // 权威路径: NULL = 空 Nullable
    if (tr.have_inner) return tr.inner_value ? 1 : 0;                  // 从权威路径返回的装箱对象里读出
    if (tr.have_direct) return tr.direct_value ? 1 : 0;                // 仅供参考(见上面的假设说明)
    return -1;
}

// 真正造出返回值(安装期探针与两个 hook 处理器都走这里)。
// kind==TF_CTOR_OBJECT_NEW 时先 il2cpp_object_new 再调实例构造函数。
// 失败返回 nullptr; out_exc 回传托管异常对象(供日志区分"抛异常"与"返回空")。
// 所有 il2cpp 调用都经 pod_* 的 SEH 边界, 异常只影响这一次调用。
void* invoke_task_factory(const cesium_safe::il2cpp_tbl& t, TaskFactory& f, void** out_exc)
{
    if (out_exc) *out_exc = nullptr;

    PodTaskInvoke r;
    pod_invoke_task_factory(&t, f.factory, f.ret_class,
                            (f.kind == TF_CTOR_OBJECT_NEW) ? 1 : 0, &r);

    // ---- 装箱路径: 每个工厂只记一次(安装期那次总会记), 运行期不刷屏 ----
    if (f.box_logged.fetch_add(1, std::memory_order_relaxed) == 0)
    {
        const std::string vt = (r.param_is_valuetype < 0)
                                   ? std::string("未知")
                                   : (r.param_is_valuetype ? std::string("是") : std::string("否"));
        log_line("[steamhack] " + std::string(f.label) + ": 参数装箱 工厂参数个数=" +
                 std::to_string(r.param_count) + " 值类型=" + vt + " 路径=" + box_path_desc(r.box_path, r));

        // ---- 额外诊断(需求核心): 直接读出"我们刚装箱并传给工厂的那个 Nullable<Lobby>"
        //      的 hasValue 字段真实值 —— 把"传进去的到底是空 Nullable 还是有值 Nullable"
        //      从推理变成事实。读不到就如实记"未能读取"。
        if (r.param_is_valuetype == 1)
        {
            if (f.param_class && r.boxed_arg)
            {
                PodNullableHasValue hv;
                pod_read_nullable_hasvalue(&t, f.param_class, r.boxed_arg, 1, &hv);
                log_line("[steamhack] " + std::string(f.label) + ": 装箱参数 hasValue 诊断(类=" +
                         f.param_class_name + " 装箱对象=" + hex_str((uintptr_t)r.boxed_arg) + ") -> " +
                         hasvalue_desc(hv, true));
            }
            else
            {
                log_line("[steamhack] " + std::string(f.label) +
                         ": 未能读取 hasValue 字段(工厂参数类未解析或没有装箱对象)");
            }
        }
    }

    // ---- SEH 兜底: 记日志 + 判定失败(安装期 -> 不挂钩; 运行期 -> 本次返回 nullptr) ----
    if (r.seh_code)
    {
        if (f.failures.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] " + std::string(f.label) + ": runtime_invoke 触发 SEH 异常 " +
                     hex_code_str(r.seh_code) + " —— 已被 __try/__except 捕获, 本次调用作废"
                     "(安装期出现即判定失败 -> 不挂钩, 游戏行为与原版一致)");
        return nullptr;
    }

    if (r.exception)
    {
        if (out_exc) *out_exc = r.exception;
        return nullptr;
    }

    if (!r.invoke_done || !r.object)
    {
        if (r.box_path == BOX_FAILED_UNKNOWN || r.box_path == BOX_FAILED_ALL)
        {
            // 原因已在装箱日志里写过, 这里不重复
        }
        else if (r.obj_alloc_ok == 0)
        {
            log_line("[steamhack] " + std::string(f.label) +
                     ": il2cpp_object_new 分配 Task 对象失败(或 object_new 导出缺失) -> 本次失败");
        }
        return nullptr;
    }
    return r.object;
}

// 读装箱对象的数据区首字节(装箱 bool 只有 1 字节)。
// 优先用 il2cpp_object_unbox; 缺失时退回"跳过 Il2CppObject 头"的写法 ——
// Il2CppObject 头 = { Il2CppClass* klass; void* monitor; } = x64 下 16 字节,
// 与"MethodInfo 首字段 = methodPointer"(阶段1 已验证可用)是同一类布局假设。
unsigned char boxed_byte(const cesium_safe::il2cpp_tbl& t, void* boxed)
{
    if (!boxed) return 0;
    const char* data = t.object_unbox ? reinterpret_cast<const char*>(t.object_unbox(boxed))
                                      : reinterpret_cast<const char*>(boxed) + 16;
    return *reinterpret_cast<const unsigned char*>(data);
}

// 沿"类自身 -> 父类 -> 祖父类..."找 0 参数的命名方法, 且要求 methodPointer 非空。
// 必要性: Task<T> 的 IsCompleted 等成员**继承**自非泛型 Task, 直接在 Task<T> 自己身上
// 查不一定查得到; 显式走父类链就不依赖 il2cpp_class_get_method_from_name 的父链搜索语义。
void* find_method_up_chain(const cesium_safe::il2cpp_tbl& t, void* cls, const char* name)
{
    if (!cls || !t.class_get_method_from_name) return nullptr;
    void* cur = cls;
    for (int depth = 0; cur && depth < 8; depth++)
    {
        void* m = t.class_get_method_from_name(cur, name, 0);
        if (m && method_pointer_of(m)) return m;
        if (!t.class_get_parent) return nullptr;
        cur = t.class_get_parent(cur);
    }
    return nullptr;
}

// 深度校验探测对象: 必须"已完成", 且取结果为"无值"。
// 这把"返回值语义正确"从推理变成**开机即可验证的事实**, 也让构造函数这类兜底路径具备同等
// 可信度 —— 否则万一造出的是未完成任务, 游戏的 await 会**永久挂起**(而不是报错), 更难排查。
//
// path             = 本次校验针对哪条构造路径("direct" / "invoke"), 只用于日志标签 ——
//                    需求: direct / invoke 两条路径各自的 hasValue 诊断都要能看到。
// strict_nullable  = true 时, 把"hasValue 必须为 false(判定得出时)"也当成通过条件:
//                    * direct 路径 = true —— 方案C 的存在意义就是把 hasValue 弄成 false;
//                      若 direct 造出来的 Task 里 hasValue 仍是 true, 说明 ABI 假设不成立,
//                      探针即判失败 -> auto 模式自动退回 invoke。
//                    * invoke 路径 = false —— 保持既有已验证行为(hasValue 被编组坏时靠方案B 兜),
//                      否则一次 hasValue 误判就会让两个大厅 hook 全部装不上, 反而比现状更差。
// 返回 false = 这个默认返回值不可信, 不要挂钩。
bool probe_task_completed_and_null(const cesium_safe::il2cpp_tbl& t, void* probe,
                                   const TaskFactory& f, const char* label, const char* path,
                                   bool strict_nullable)
{
    const std::string tag = std::string(label) + "[" + path + "]";
    if (!probe || !t.runtime_invoke) return true;   // 缺依赖时不做校验, 不阻断

    void* cls = t.object_get_class ? t.object_get_class(probe) : nullptr;
    if (!cls) cls = f.ret_class;
    if (!cls) return true;

    void* args[1] = { nullptr };

    // ---- 1) 完成态: IsCompleted 必须为 true ----
    void* get_completed = nullptr;
    if (t.class_get_method_from_name)
    {
        get_completed = t.class_get_method_from_name(cls, "get_IsCompleted", 0);
        if (!get_completed) get_completed = find_method_up_chain(t, cls, "get_IsCompleted");
    }
    if (!get_completed || !method_pointer_of(get_completed))
    {
        log_line("[steamhack] " + tag +
                 ": 找不到可用的 IsCompleted getter —— 跳过完成态校验(仅按对象类型放行)");
        return true;
    }

    void* exc = nullptr;
    void* boxed = nullptr;
    unsigned long seh = 0;
    pod_runtime_invoke((void*)t.runtime_invoke, get_completed, probe, args, &boxed, &exc, &seh);
    if (seh)
    {
        log_line("[steamhack] " + tag + ": IsCompleted 调用触发 SEH 异常 " +
                 hex_code_str(seh) + " —— 已被 __try/__except 捕获, 判定失败 -> 不挂钩");
        return false;
    }
    if (exc || !boxed)
    {
        log_line("[steamhack] " + tag + ": IsCompleted 调用失败" +
                 (exc ? "(托管异常)" : "(返回空)") + " —— 不挂钩");
        return false;
    }
    const bool completed = boxed_byte(t, boxed) != 0;
    log_line("[steamhack] " + tag + ": 探测对象 IsCompleted=" +
             (completed ? "true" : "false") +
             (t.object_unbox ? " (经 il2cpp_object_unbox 读装箱值)"
                             : " (按 Il2CppObject 头 +16 读装箱值)"));
    if (!completed)
    {
        log_line("[steamhack] " + tag +
                 ": 探测任务不是 RanToCompletion —— 不挂钩(否则游戏 await 会永久挂起)");
        return false;
    }

    // ---- 1.5) 额外诊断(需求核心): 读样本 Task 的 m_result 里那个 Nullable<Lobby> 的 hasValue ----
    // 放在这里而不是最后: 这样即使这个 Task<T> 上查不到 get_Result()(下面的提前 return),
    // hasValue 字段诊断也一定已经写进日志了。
    const int hv = log_sample_task_hasvalue(t, probe, cls, f, tag.c_str());

    // ---- 1.6) 结论行(需求: 要能明确看出这次 direct 到底把 hasValue 变成了什么) ----
    log_line("[steamhack] " + tag + ": hasValue 结论 = " +
             (hv == 0 ? "**false**(空 Nullable —— 正是期望结果, 游戏侧不会进 if 块)"
                      : hv == 1 ? "**true**(非空 —— 与期望相反, 游戏侧会进 if 块并调用 Lobby.SetPublic)"
                                : "未能判定(读不到)"));

    if (strict_nullable && hv == 1)
    {
        log_line("[steamhack] " + tag +
                 ": 严格校验失败 —— 该路径造出的 Task 里 hasValue 仍为 true(方案C 的 ABI 假设不成立)");
        return false;
    }

    // ---- 2) 结果必须"无值" ----
    // 已完成 -> get_Result 不会阻塞, 可以安全调用(若未完成就调它会永久等待, 所以上面必须先查完成态)。
    void* get_result = t.class_get_method_from_name ? t.class_get_method_from_name(cls, "get_Result", 0) : nullptr;
    if (!get_result || !method_pointer_of(get_result))
    {
        log_line("[steamhack] " + tag +
                 ": 类上没有 get_Result(非泛型 Task 属正常) —— 跳过结果校验");
        return true;
    }
    void* rexc = nullptr;
    void* rv = nullptr;
    unsigned long rseh = 0;
    pod_runtime_invoke((void*)t.runtime_invoke, get_result, probe, args, &rv, &rexc, &rseh);
    if (rseh)
    {
        log_line("[steamhack] " + tag + ": get_Result() 触发 SEH 异常 " +
                 hex_code_str(rseh) + " —— 已被 __try/__except 捕获, 判定失败 -> 不挂钩");
        return false;
    }
    if (rexc)
    {
        log_line("[steamhack] " + tag +
                 ": get_Result() 抛出托管异常(说明任务处于 Faulted/Canceled 态) —— 不挂钩");
        return false;
    }
    // 注意: 我们传进去的结果参数就是 null, 所以这里**不可能**是真值:
    //   rv == nullptr -> 空 Nullable / 引用类型 null(CLR 语义);
    //   rv != nullptr -> IL2CPP 把"无值 Nullable"装箱成了对象(装箱实现差异), 语义仍是"无值"。
    // 两种情况都安全, 只记录实际观测到哪一种。
    log_line("[steamhack] " + tag + ": 探测对象 get_Result()=" +
             (rv ? ("装箱对象 " + hex_str((uintptr_t)rv) + "(空 Nullable 的装箱差异, 仍代表无值)")
                 : std::string("null(无值)")) +
             " —— 校验通过: 已完成 + 结果为 null");
    return true;
}

// 未命中诊断: 把某个类里名字含 Lobby/Async/Task/From/Result 的方法名列出来(上限 20),
// 便于下一轮直接定位候选入口。
void dump_class_methods(const cesium_safe::il2cpp_tbl& t, void* cls, const char* label)
{
    if (!cls || !t.class_get_methods || !t.method_get_name) return;
    void* iter = nullptr;
    std::string names;
    int shown = 0;
    for (;;)
    {
        void* m = t.class_get_methods(cls, &iter);
        if (!m) break;
        const char* n = t.method_get_name(m);
        if (!n) continue;
        if (strstr(n, "Lobby") || strstr(n, "Async") || strstr(n, "Task") ||
            strstr(n, "From") || strstr(n, "Result"))
        {
            if (shown >= 20) { names += ", ..."; break; }
            if (shown) names += ", ";
            names += n;
            shown++;
        }
    }
    log_line(std::string("[steamhack] ") + label +
             ": 该类里含 Lobby/Async/Task/From/Result 的方法名(上限20): " +
             (names.empty() ? std::string("(无)") : names));
}

// 未命中诊断(阶段3 专用): 把 Nullable 类里**所有**方法名列出来(上限 24)。
// 类名含 Nullable 时上面那个过滤条件基本筛不出东西, 所以这里不做名字过滤。
void dump_all_methods(const cesium_safe::il2cpp_tbl& t, void* cls, const char* label)
{
    if (!cls || !t.class_get_methods || !t.method_get_name) return;
    void* iter = nullptr;
    std::string names;
    int shown = 0;
    for (;;)
    {
        void* m = t.class_get_methods(cls, &iter);
        if (!m) break;
        const char* n = t.method_get_name(m);
        if (!n) continue;
        if (shown >= 24) { names += ", ..."; break; }
        if (shown) names += ", ";
        names += n;
        shown++;
    }
    log_line(std::string("[steamhack] ") + label + ": 该类全部方法名(上限24): " +
             (names.empty() ? std::string("(无, 或 class_get_methods/method_get_name 缺失)") : names));
}

// 用指定路径造一个默认返回值并做完整校验(类型 + 完成态 + 结果为 null + hasValue 诊断)。
// 返回探测对象(nullptr = 该路径失败); 日志一律带 [direct] / [invoke] 标签 ——
// 需求: 两条路径各自的 hasValue 诊断结果都要能看到, 以便明确判断 direct 有没有把 hasValue
// 变成 false。
//
// path_kind == CPATH_DIRECT 时对 hasValue 做**严格校验**: 方案C 的意义就是把 hasValue 弄成 false,
// 若 direct 造出的 Task 里 hasValue 仍为 true, 说明 ABI 假设不成立 -> 判该路径失败,
// auto 模式便会自动退回 invoke(见 resolve_task_factory)。
void* try_build_and_validate(const cesium_safe::il2cpp_tbl& t, TaskFactory& f, int path_kind)
{
    const char* path = (path_kind == CPATH_DIRECT) ? "direct" : "invoke";
    void* probe = nullptr;

    if (path_kind == CPATH_DIRECT)
    {
        PodDirectCtor dr;
        probe = run_direct_ctor(t, f, &dr);
        log_direct_attempt(t, f, dr, probe, "安装期探测");
    }
    else
    {
        void* exc = nullptr;
        probe = invoke_task_factory(t, f, &exc);
        log_line(std::string("[steamhack] ") + f.label + "[invoke]: runtime_invoke 造 Task " +
                 (probe ? ("完成, 对象=" + hex_str((uintptr_t)probe))
                        : ("失败(" + std::string(exc ? "抛出托管异常" : "返回空") + ")")));
    }

    if (!probe) return nullptr;

    if (t.object_get_class)
    {
        void* pcls = t.object_get_class(probe);
        const bool same = (pcls == f.ret_class);
        log_line(std::string("[steamhack] ") + f.label + "[" + path + "]: 探测对象=" +
                 hex_str((uintptr_t)probe) + " 类=" + class_name_of(t, pcls) +
                 " 与期望返回类型一致=" + (same ? "是" : "否"));
        if (!same && f.kind != TF_COMPLETED_TASK)
        {
            log_line(std::string("[steamhack] ") + f.label + "[" + path + "]" +
                     ": 对象类型与期望返回类型不一致 —— 本路径失败(避免把错误类型的对象交给游戏)");
            return nullptr;
        }
        if (!same)
            log_line(std::string("[steamhack] ") + f.label + "[" + path + "]" +
                     ": 类不一致, 但返回类型是非泛型 Task(CompletedTask 可能是内部子类), 接受");
    }
    else
    {
        log_line(std::string("[steamhack] ") + f.label + "[" + path + "]: 探测对象=" +
                 hex_str((uintptr_t)probe) + " (object_get_class 缺失, 跳过类型校验)");
    }

    // 深度校验: 造出来的任务必须真的"已完成"且"结果为 null"。
    // direct 路径额外要求"hasValue 为 false(判定得出时)", 见函数注释。
    const bool strict_nullable = (path_kind == CPATH_DIRECT);
    if (!probe_task_completed_and_null(t, probe, f, f.label, path, strict_nullable)) return nullptr;
    return probe;
}

// 解析"造默认返回值"的工厂。返回 true 表示已解析**且探测成功**(f.ok = true)。
//
// 三种候选(按可靠性排序; 一律要求 methodPointer 非空才采用):
//   A) cls.FromResult(1) —— 规格要求的主路径。但注意 Mono/.NET 里
//        public static Task<TResult> FromResult<TResult>(TResult result)
//      声明在**非泛型 Task** 上 (mono/mcs/class/referencesource/mscorlib/system/threading/Tasks/Task.cs),
//      所以闭合类 Task<T> 上通常根本查不到它; 万一 il2cpp 沿基类查到了, 拿到的也很可能是
//      **未实例化的开放泛型方法定义** —— 用"返回类型指针必须与被挂钩方法完全一致"把它挡掉,
//      绝不用开放泛型方法去 runtime_invoke。
//   B) il2cpp_object_new(cls) + cls..ctor(1) —— 等价兜底。Mono 的 Future.cs 原文:
//        // Construct a pre-completed Task<TResult>
//        internal Task(TResult result) : base(false, TaskCreationOptions.None, default(CancellationToken))
//      构造出来就是 RanToCompletion 的 Task<T>; 而 FromResult 的实现就是 new Task<TResult>(result),
//      所以两条路径语义完全相同。
//   C) cls.get_CompletedTask() —— **仅当返回类型是非泛型 Task 时**才允许。
//      在 Task<T> 的位置塞一个非泛型 Task 会造成类型混淆(await 会去读 Task<T>.m_result),
//      必须明确禁止。
bool resolve_task_factory(const cesium_safe::il2cpp_tbl& t, TaskFactory& f, void* hooked_mi, const char* label)
{
    f.label = label;
    f.hooked_method = hooked_mi;
    f.resolved = true;
    f.ok = false;
    f.factory = nullptr;
    f.kind = TF_NONE;
    f.ctor_ptr = nullptr;
    f.ctor_path = CPATH_UNSET;

    // ---- 依赖的导出: 缺任何一个都做不了, 直接放弃该 hook(游戏行为与原版一致) ----
    struct { const char* n; const void* p; } need[] = {
        { "method_get_return_type",     (const void*)t.method_get_return_type },
        { "type_get_object",            (const void*)t.type_get_object },
        { "class_from_system_type",     (const void*)t.class_from_system_type },
        { "class_get_method_from_name", (const void*)t.class_get_method_from_name },
        { "runtime_invoke",             (const void*)t.runtime_invoke },
    };
    std::string missing;
    for (auto& n : need) if (!n.p) { if (!missing.empty()) missing += ", "; missing += n.n; }
    if (!missing.empty())
    {
        log_line(std::string("[steamhack] ") + label + ": IL2CPP 导出缺失(" + missing +
                 ") —— 无法构造默认返回值, 不挂钩");
        return false;
    }

    // ---- 返回类型 -> System.Type -> Il2CppClass ----
    void* ret = t.method_get_return_type(hooked_mi);
    if (!ret)
    {
        log_line(std::string("[steamhack] ") + label + ": method_get_return_type 返回空 —— 不挂钩");
        return false;
    }
    f.ret_type = ret;
    f.ret_type_name = t.type_get_name ? cstr_or(t.type_get_name(ret), "(null)") : std::string("(type_get_name 缺失)");

    void* tobj = t.type_get_object(ret);
    if (!tobj)
    {
        log_line(std::string("[steamhack] ") + label + ": type_get_object(" + f.ret_type_name + ") 返回空 —— 不挂钩");
        return false;
    }
    void* cls = t.class_from_system_type(tobj);
    if (!cls)
    {
        log_line(std::string("[steamhack] ") + label + ": class_from_system_type 返回空 —— 不挂钩");
        return false;
    }
    f.ret_class = cls;

    log_line(std::string("[steamhack] ") + label + ": 返回值解析 返回类型=" + f.ret_type_name +
             " Il2CppType=" + hex_str((uintptr_t)ret) +
             " System.Type=" + hex_str((uintptr_t)tobj) +
             " Il2CppClass=" + hex_str((uintptr_t)cls) +
             " 类名=" + class_name_of(t, cls));

    // ---- 判定"是 Task<T> 还是非泛型 Task" ----
    // 三条信号, 任一命中即认为是泛型(用于禁止 Task.CompletedTask 兜底 —— 在 Task<T> 的位置
    // 塞非泛型 Task 会让 await 去读 Task<T>.m_result, 属于类型混淆, 必须挡住):
    //   1) 返回类型名含 '`'(IL2CPP/ECMA 的泛型类名形如 Task`1);
    //   2) 类名含 '`';
    //   3) 结构判据: Task<T> 有 0 参数方法 get_Result, 非泛型 Task 没有 —— 即使前两条依赖的
    //      il2cpp_type_get_name / il2cpp_class_get_name 恰好缺失也依然成立。
    const std::string cls_name = class_name_of(t, cls);
    const bool by_type_name = (f.ret_type_name.find('`') != std::string::npos);
    const bool by_class_name = (cls_name.find('`') != std::string::npos);
    const bool by_get_result = (t.class_get_method_from_name &&
                                t.class_get_method_from_name(cls, "get_Result", 0) != nullptr);
    const bool cls_is_generic = by_type_name || by_class_name || by_get_result;
    log_line(std::string("[steamhack] ") + label + ": 泛型判定 类型名含`=" + (by_type_name ? "是" : "否") +
             " 类名含`=" + (by_class_name ? "是" : "否") +
             " 有 get_Result()=" + (by_get_result ? "是" : "否") +
             " -> " + (cls_is_generic ? "Task<T>(泛型)" : "非泛型 Task"));

    // ---- 候选 A: cls.FromResult(1) ----
    void* cand = t.class_get_method_from_name(cls, "FromResult", 1);
    if (cand)
    {
        void* cand_ret = t.method_get_return_type(cand);
        void* cand_ptr = method_pointer_of(cand);
        const bool ret_match = (cand_ret == f.ret_type);
        log_line(std::string("[steamhack] ") + label + ": cls.FromResult 解析 命中=是 MethodInfo=" +
                 hex_str((uintptr_t)cand) + " methodPointer=" + hex_str((uintptr_t)cand_ptr) +
                 " 返回类型指针与本体一致=" + (ret_match ? "是" : "否"));
        if (ret_match && cand_ptr)
        {
            f.factory = cand;
            f.kind = TF_FROM_RESULT;
        }
        else
        {
            log_line(std::string("[steamhack] ") + label +
                     ": FromResult 校验未通过(返回类型指针不一致 或 methodPointer 为空) —— "
                     "判定为基类上的开放泛型定义, 不采用, 转构造函数兜底");
        }
    }
    else
    {
        log_line(std::string("[steamhack] ") + label +
                 ": cls.FromResult 解析 命中=否 (FromResult 实际声明在非泛型 Task 上, 属预期)");
    }

    // ---- 候选 B: object_new(cls) + cls..ctor(1) ----
    if (!f.factory)
    {
        void* ctor = t.class_get_method_from_name(cls, ".ctor", 1);
        void* ctor_ptr = method_pointer_of(ctor);
        if (ctor && ctor_ptr && t.object_new)
        {
            f.factory = ctor;
            f.ctor_ptr = ctor_ptr;   // 方案C: 记住原生入口, direct 路径直调它
            f.kind = TF_CTOR_OBJECT_NEW;
            log_line(std::string("[steamhack] ") + label + ": 采用构造函数 类..ctor(1) MethodInfo=" +
                     hex_str((uintptr_t)ctor) + " methodPointer=" + hex_str((uintptr_t)ctor_ptr) +
                     " + il2cpp_object_new —— 与 FromResult 等价(Mono 源码注释即 "
                     "\"Construct a pre-completed Task<TResult>\")");
            log_line(std::string("[steamhack] ") + label +
                     ": 方案C 可用性 —— 上面这个 methodPointer 就是【原生 ABI 直调】的入口; "
                     "steamBypassTaskCtorMode=\"" + std::string(g_ctor_mode_name) + "\"");
        }
        else
        {
            log_line(std::string("[steamhack] ") + label + ": 类..ctor(1) 解析 命中=" +
                     (ctor ? "是" : "否") +
                     (ctor && !ctor_ptr ? " 但 methodPointer 为空" : "") +
                     (t.object_new ? "" : " 且 il2cpp_object_new 导出缺失") + " —— 该兜底不可用");
        }
    }

    // ---- 候选 C: 仅非泛型 Task 才允许 CompletedTask ----
    if (!f.factory && !cls_is_generic)
    {
        void* getter = t.class_get_method_from_name(cls, "get_CompletedTask", 0);
        if (!getter && t.class_get_property_from_name && t.property_get_get_method)
        {
            void* prop = t.class_get_property_from_name(cls, "CompletedTask");
            if (prop) getter = t.property_get_get_method(prop);
        }
        if (getter && method_pointer_of(getter))
        {
            f.factory = getter;
            f.kind = TF_COMPLETED_TASK;
            log_line(std::string("[steamhack] ") + label +
                     ": 返回类型是非泛型 Task, 采用 Task.CompletedTask getter MethodInfo=" +
                     hex_str((uintptr_t)getter));
        }
        else
        {
            log_line(std::string("[steamhack] ") + label + ": Task.CompletedTask 解析 命中=否");
        }
    }
    else if (!f.factory)
    {
        log_line(std::string("[steamhack] ") + label +
                 ": 返回类型是泛型 Task<T> —— 明确禁止用 Task.CompletedTask 兜底(类型混淆), 已跳过该候选");
    }

    if (!f.factory)
    {
        log_line(std::string("[steamhack] ") + label + ": 三种构造方式全部失败 —— 不挂钩(游戏行为与原版一致)");
        dump_class_methods(t, cls, label);
        return false;
    }

    // ---- 工厂参数检查: 值类型参数**必须能装箱**(上一轮的崩溃就在这一步之后) ----
    //
    // 实测崩溃点: 工厂是 Task<Lobby?>..ctor(Nullable<Lobby>), Nullable<Lobby> 是值类型, 而上一轮
    // 给 args[0] 传了 nullptr -> runtime_invoke 内部 il2cpp_field_get_value_object 解引用空指针
    // -> 0xC0000005。现在改为如实装箱(见 pod_invoke_task_factory), 这里只做**能力检查**:
    // 判不出参数类型、或值类型但装箱导出全缺 -> 直接放弃该 hook(绝不冒进传 null)。
    if (t.method_get_param_count && t.method_get_param)
    {
        const uint32_t pc = t.method_get_param_count(f.factory);
        log_line(std::string("[steamhack] ") + label + ": 工厂方法参数个数=" + std::to_string(pc));
        if (pc == 1)
        {
            void* pt = t.method_get_param(f.factory, 0);
            const std::string ptn = (pt && t.type_get_name) ? cstr_or(t.type_get_name(pt), "(null)")
                                                            : std::string("(未知)");
            bool is_vt = false;
            bool determined = false;
            void* pcls = nullptr;
            if (pt && t.type_get_object && t.class_from_system_type && t.class_is_valuetype)
            {
                void* pto = t.type_get_object(pt);
                pcls = pto ? t.class_from_system_type(pto) : nullptr;
                if (pcls)
                {
                    is_vt = t.class_is_valuetype(pcls);
                    determined = true;
                }
            }
            log_line(std::string("[steamhack] ") + label + ": 工厂方法参数类型=" + ptn +
                     " 值类型=" + (determined ? (is_vt ? "是" : "否") : "未知(无法判定)"));
            if (determined && pcls)
            {
                // ---- 阶段3 复用点: 把这一个 Il2CppClass* 留下来 ----
                // 它就是 System.Nullable`1<Steamworks.Data.Lobby>, 是 get_HasValue 挂钩与
                // hasValue 字段诊断的入口 —— 不必为此重新解析一次。
                f.param_class = pcls;
                f.param_class_name = class_name_of(t, pcls);
                f.param_is_valuetype = is_vt;
                f.param_is_nullable = is_vt &&
                                      (ptn.find("Nullable") != std::string::npos ||
                                       f.param_class_name.find("Nullable") != std::string::npos);
                log_line(std::string("[steamhack] ") + label + ": 工厂参数类 pcls=" + hex_str((uintptr_t)pcls) +
                         " 类名=" + f.param_class_name +
                         " (阶段3 将用它解析 Nullable.get_HasValue)" +
                         " 类名确认含\"Nullable\"=" + (f.param_is_nullable ? "是" : "否"));
            }

            if (!determined)
            {
                log_line(std::string("[steamhack] ") + label +
                         ": 无法判定工厂参数是否为值类型(缺 type_get_object / class_from_system_type / "
                         "class_is_valuetype) —— 不冒进, 不挂钩");
                return false;
            }
            if (is_vt && !t.value_box && !t.object_new)
            {
                log_line(std::string("[steamhack] ") + label +
                         ": 工厂参数是值类型, 但 il2cpp_value_box 与 il2cpp_object_new 都缺失 —— "
                         "无法装箱(传 null 会像上一轮那样崩游戏), 不挂钩");
                return false;
            }
            if (is_vt)
            {
                log_line(std::string("[steamhack] ") + label + ": 工厂参数是值类型(" +
                         (ptn.find("Nullable") != std::string::npos ? "空 Nullable" : "非 Nullable") +
                         ") —— 将用 256B 零初始化缓冲区装箱: il2cpp_value_box" +
                         (t.value_box ? "(可用)" : "(导出缺失)") +
                         (t.object_new ? " + il2cpp_object_new 兜底(空 Nullable 装箱返回 NULL 时)" : "(无兜底)"));
            }
        }
    }

    // ---- 探测: 真的造一个默认返回值出来, 校验非空 + 类型 + 完成态 + 结果为 null(+ hasValue) ----
    //
    // 方案C(主): direct —— 按原生 ABI 直调 .ctor 的 methodPointer, 绕过 il2cpp_runtime_invoke
    //            对 Nullable<Lobby> 的错误编组(它把 hasValue 弄成了非零)。
    // steamBypassTaskCtorMode: "auto"(默认, direct 校验失败自动退回 invoke) / "direct" / "invoke"。
    const bool direct_possible =
        (f.kind == TF_CTOR_OBJECT_NEW) && f.ctor_ptr != nullptr && t.object_new != nullptr;
    void* probe = nullptr;

    if (g_ctor_mode == CTOR_MODE_INVOKE)
    {
        log_line(std::string("[steamhack] ") + label +
                 "[invoke]: steamBypassTaskCtorMode=\"invoke\" —— 只走 runtime_invoke(不尝试 direct)");
        probe = try_build_and_validate(t, f, CPATH_INVOKE);
        if (probe) f.ctor_path = CPATH_INVOKE;
    }
    else if (direct_possible)
    {
        log_line(std::string("[steamhack] ") + label +
                 "[direct]: 尝试方案C —— 原生 ABI 直调 Task<T>..ctor(T) (steamBypassTaskCtorMode=\"" +
                 std::string(g_ctor_mode_name) + "\") ...");
        probe = try_build_and_validate(t, f, CPATH_DIRECT);
        if (probe)
        {
            f.ctor_path = CPATH_DIRECT;
        }
        else if (g_ctor_mode == CTOR_MODE_AUTO)
        {
            log_line(std::string("[steamhack] ") + label +
                     "[auto]: direct 路径未通过校验(关键看上面那条 hasValue 结论) —— "
                     "**自动退回 runtime_invoke 路径**, 见下一条日志");
            probe = try_build_and_validate(t, f, CPATH_INVOKE);
            if (probe)
            {
                f.ctor_path = CPATH_INVOKE;
                log_line(std::string("[steamhack] ") + label +
                         "[auto]: 已退回 invoke 路径并通过校验 —— runtime_invoke 这条路 hasValue 可能仍被判为 "
                         "true, 届时应由方案B(Lobby.SetPublic/SetJoinable/Id 挂成无害)兜住");
            }
        }
        else
        {
            log_line(std::string("[steamhack] ") + label +
                     "[direct]: steamBypassTaskCtorMode=\"direct\" 且 direct 校验失败 —— "
                     "按配置不退回 invoke, 不挂钩(fail-safe)");
        }
    }
    else
    {
        const std::string why = (f.kind != TF_CTOR_OBJECT_NEW)
                                    ? std::string("工厂不是 类..ctor(1)")
                                    : (!f.ctor_ptr ? std::string("ctor methodPointer 为空")
                                                   : std::string("il2cpp_object_new 导出缺失"));
        if (g_ctor_mode == CTOR_MODE_DIRECT)
        {
            log_line(std::string("[steamhack] ") + label + "[direct]: " + why +
                     " —— direct 路径不适用, 且模式为 \"direct\" -> 不挂钩(fail-safe)");
            return false;
        }
        log_line(std::string("[steamhack] ") + label + "[invoke]: " + why +
                 " —— direct 不适用, 改走 runtime_invoke");
        probe = try_build_and_validate(t, f, CPATH_INVOKE);
        if (probe) f.ctor_path = CPATH_INVOKE;
    }

    if (!probe)
    {
        log_line(std::string("[steamhack] ") + label +
                 ": 探测最终失败(direct/invoke 两条路径都未通过校验) —— 不挂钩(游戏行为与原版一致)");
        dump_class_methods(t, f.ret_class, label);
        return false;
    }

    log_line(std::string("[steamhack] ") + label + ": 探测成功 —— 实际采用路径 = " +
             ctor_path_name(f.ctor_path) +
             " (steamBypassTaskCtorMode=\"" + std::string(g_ctor_mode_name) + "\")");

    // 注意: 探测对象到此丢弃(不保存裸指针、不建 GC 根) —— 命中路径每次都会重新造一个新的。
    f.ok = true;
    return true;
}

// Steamworks.NET 的实际命名空间是 "Steamworks"; 后两个是兜底(万一被 flat 到全局或改名)。
const char* kMatchmakingNamespaces[] = { "Steamworks", "", "Steamworks.NET" };

// 在域内全部镜像(此刻 = AOT 镜像)里找 Steamworks.SteamMatchmaking。
// 返回选中的 Il2CppClass*, 实际命中的 image 名 / 命名空间经出参回传(供日志);
// 找不到返回 nullptr(诊断信息, 含域内程序集名单, 已写日志)。
void* resolve_steam_matchmaking(const cesium_safe::il2cpp_tbl& t, std::string* out_image, std::string* out_ns)
{
    struct { const char* n; const void* p; } need[] = {
        { "domain_get",                 (const void*)t.domain_get },
        { "domain_get_assemblies",      (const void*)t.domain_get_assemblies },
        { "assembly_get_image",         (const void*)t.assembly_get_image },
        { "image_get_name",             (const void*)t.image_get_name },
        { "class_from_name",            (const void*)t.class_from_name },
        { "class_get_method_from_name", (const void*)t.class_get_method_from_name },
    };
    std::string missing;
    for (auto& n : need) if (!n.p) { if (!missing.empty()) missing += ", "; missing += n.n; }
    if (!missing.empty())
    {
        log_line("[steamhack] IL2CPP 函数表缺失: " + missing + " —— 无法解析 SteamMatchmaking");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line("[steamhack] il2cpp domain 为空, 无法解析 SteamMatchmaking");
        return nullptr;
    }

    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line("[steamhack] domain_get_assemblies 返回空");
        return nullptr;
    }

    std::vector<void*> images;
    std::vector<std::string> image_names;
    for (size_t i = 0; i < count; i++)
    {
        if (!assemblies[i]) continue;
        void* image = t.assembly_get_image(assemblies[i]);
        if (!image) continue;
        images.push_back(image);
        image_names.push_back(cstr_or(t.image_get_name(image), "(null)"));
    }

    void* chosen = nullptr;
    std::string chosen_image;
    std::string chosen_ns;
    std::string all_images;
    for (size_t i = 0; i < images.size(); i++)
    {
        if (i) all_images += ", ";
        all_images += image_names[i];
        for (const char* ns : kMatchmakingNamespaces)
        {
            void* cls = t.class_from_name(images[i], ns, "SteamMatchmaking");
            if (!cls) continue;
            log_line("[steamhack] 命中类 SteamMatchmaking: image=" + image_names[i] +
                     " ns=\"" + std::string(ns) + "\" Il2CppClass=" + hex_str((uintptr_t)cls) +
                     " 类名=" + class_name_of(t, cls));
            // 多份命中时优先取 image 名里带 Steamworks 的那份(Steamworks.NET.dll 的 AOT 镜像)
            const bool better = (!chosen) ||
                                (chosen_image.find("Steamworks") == std::string::npos &&
                                 image_names[i].find("Steamworks") != std::string::npos);
            if (better) { chosen = cls; chosen_image = image_names[i]; chosen_ns = ns; }
            break;   // 同一个 image 不必再试其他命名空间
        }
    }

    if (!chosen)
    {
        log_line("[steamhack] 未找到类 SteamMatchmaking(候选 ns: \"Steamworks\" / 全局 / \"Steamworks.NET\")"
                 " —— 阶段2 无法安装");
        log_line("[steamhack] 域内程序集 " + std::to_string(image_names.size()) + " 个: " + all_images);
    }
    if (out_image) *out_image = chosen_image;
    if (out_ns) *out_ns = chosen_ns;
    return chosen;
}

// 解析并挂钩 SteamMatchmaking.<method_name>(arg_count 个参数)。
// 顺序很重要: **先把默认返回值工厂解析并探测成功, 再挂钩** —— 解析/探测失败就完全不挂钩,
// 游戏行为与原版一致(不会因为打不开的绕过而变得更糟)。
bool install_lobby_hook(const cesium_safe::il2cpp_tbl& t,
                        const char* method_name, int arg_count,
                        void* detour, LPVOID* real_out,
                        void** target_out, void** method_out,
                        TaskFactory& f)
{
    std::string image;
    std::string ns;
    void* cls = resolve_steam_matchmaking(t, &image, &ns);
    if (!cls) return false;

    void* mi = t.class_get_method_from_name(cls, method_name, arg_count);
    if (!mi)
    {
        log_line("[steamhack] 未命中 SteamMatchmaking." + std::string(method_name) + "(" +
                 std::to_string(arg_count) + " 参数): image=" + image + " ns=\"" + ns + "\"");
        dump_class_methods(t, cls, method_name);
        return false;
    }

    void* target = method_pointer_of(mi);
    const uint32_t pc = t.method_get_param_count ? t.method_get_param_count(mi) : (uint32_t)arg_count;
    log_line("[steamhack] 命中 SteamMatchmaking." + std::string(method_name) + ": image=" + image +
             " ns=\"" + ns + "\" MethodInfo=" + hex_str((uintptr_t)mi) +
             " methodPointer=" + hex_str((uintptr_t)target) + " (" + rva_of(target) + ")" +
             " 参数个数=" + std::to_string(pc));
    if (!target)
    {
        log_line("[steamhack] SteamMatchmaking." + std::string(method_name) +
                 " 的 methodPointer 为空(可能不是 AOT 方法) —— 跳过该 hook");
        return false;
    }

    if (!resolve_task_factory(t, f, mi, method_name)) return false;

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, detour, real_out);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHook(" + std::string(method_name) + ") 失败: " +
                 std::to_string((int)st) + " (target=" + hex_str((uintptr_t)target) + ")");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(" + std::string(method_name) + ") 失败: " +
                 std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    if (target_out) *target_out = target;
    if (method_out) *method_out = mi;
    log_line("[steamhack] SteamMatchmaking." + std::string(method_name) +
             " inline hook 已启用 (恒返回已完成的空 Task)");
    return true;
}

// =====================================================================================
// 阶段3: System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue -> 恒 false
// =====================================================================================
//
// 阶段2 之后实测: 异常从 CreateLobbyAsync **前进到了** Steamworks.Data.Lobby.SetPublic():
//     NullReferenceException
//     UI.UICom_CreateRoom.RequestCreateRoom ()
//     Steamworks.Data.Lobby.SetPublic ()
//     --- End of stack trace from previous location where exception was thrown ---
// 对应 decomp_full\UI\UICom_CreateRoom.cs:131-142 —— val.HasValue(第 133 行)被判成了 true,
// 于是进了 if 块, 到第 136 行 ((Lobby)(ref value)).SetPublic() 炸掉。
//
// 判据: 安装期探针里 get_Result() 返回**非空**装箱对象, 而"空 Nullable"装箱本应得 NULL
// (同一个 il2cpp_value_box 在零初始化缓冲区上确实返回了 NULL) —— 说明 il2cpp_runtime_invoke
// 把 Nullable<Lobby> 参数编组进 Task<T>..ctor(T) 时 hasValue 落成了非零。**不再去修 Task 的构造**。
//
// 修法: RequestCreateRoom 在**热更程序集、HybridCLR 解释执行**里, val.HasValue 是一次真实的
// 方法调用, 因此会 call 到 AOT 方法 System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue()。
// 把它换成恒返回 false: steamLobbyId 保持 0(协议里的"无 Steam 大厅"合法值), 第 142 行
// RequestCreateC2S 正常发出。语义自洽 —— Steam 不存在时任何 Lobby? 都不该 HasValue。
//
// 失败一律 fail-safe(**只记日志, 不挂钩**): 解析不到 get_HasValue、methodPointer 为空、
// MinHook 失败, 都只是"回到当前这个 NRE", 绝不让游戏更难启动。

// 【重要限制/风险提示】il2cpp 对泛型**值类型**可能只生成一份共享代码(Nullable<>.get_HasValue):
// 此时各实例(Nullable<int> / Nullable<Lobby> ...)的 MethodInfo 会指向同一个 methodPointer,
// "恒返回 false" 就会波及所有 Nullable<T>.HasValue。
// 本模块**故意不做**这个判定: 唯一可行的判定办法是拿开放泛型定义 System.Nullable`1 的
// 同名方法指针来比较, 而对泛型定义调用 il2cpp 的元数据 API 万一触发 il2cpp 自身的断言
// (abort) —— 那是 __try/__except **抓不到**的, 会直接把游戏打死。宁可"不判定"。
// 现有可用的信号(都无副作用, 见 Hook_NullableHasValue 的首次命中日志):
//   * 第 2 个寄存器参数 == 本实例的 MethodInfo -> 说明调用方在传具体实例的上下文,
//     与"共享泛型代码"一致; 反之则该实例看上去有自己的代码。
void log_hasvalue_scope_caution(const TaskFactory& f, void* target)
{
    log_line("[steamhack] " + f.param_class_name + ": get_HasValue 目标 " +
             hex_str((uintptr_t)target) + " RVA 见上方日志; "
             "共享泛型范围**未判定**(对开放泛型定义做元数据解析有触发 il2cpp 断言的风险, 属不可捕获, "
             "故按 fail-safe 原则不做) —— 若该方法在多个 Nullable<T> 间共享, 恒 false 也会作用于它们");
}

// 解析 Nullable<Lobby>.get_HasValue 的 MethodInfo*(复用 resolve_task_factory 留下的 pcls)。
// 返回 nullptr = 解析失败(原因已写日志), 调用方**不挂钩**。
void* resolve_hasvalue_method(const cesium_safe::il2cpp_tbl& t, TaskFactory& f)
{
    if (f.hasvalue_resolved) return f.hasvalue_method;
    f.hasvalue_resolved = true;
    f.hasvalue_method = nullptr;

    if (!f.param_class)
    {
        log_line("[steamhack] " + std::string(f.label) +
                 ": 未解析到工厂参数类(Nullable), 无法解析 get_HasValue —— 不挂钩(fail-safe)");
        return nullptr;
    }
    if (!t.class_get_method_from_name)
    {
        log_line("[steamhack] " + std::string(f.label) +
                 ": il2cpp_class_get_method_from_name 导出缺失 —— 无法解析 get_HasValue, 不挂钩(fail-safe)");
        return nullptr;
    }

    // 【危险调用 #5】解析包在 POD + SEH 里(需求 #7): 违例只会导致"本次解析失败", 不会崩安装期。
    PodHasValueMethod r;
    pod_resolve_hasvalue_method(&t, f.param_class, &r);
    if (r.seh_code)
    {
        log_line("[steamhack] " + std::string(f.label) + ": 解析 " + f.param_class_name +
                 "::get_HasValue() 触发 SEH 异常 " + hex_code_str(r.seh_code) +
                 " —— 已被 __try/__except 捕获, 本次不挂钩(fail-safe)");
        return nullptr;
    }
    if (!r.mi)
    {
        log_line("[steamhack] " + std::string(f.label) + ": 类 " + f.param_class_name +
                 " 上未命中 get_HasValue()(0 参数) —— 不挂钩(fail-safe)");
        dump_all_methods(t, f.param_class, "Nullable.get_HasValue");
        return nullptr;
    }

    log_line("[steamhack] " + std::string(f.label) + ": 命中 " + f.param_class_name +
             "::get_HasValue() MethodInfo=" + hex_str((uintptr_t)r.mi) +
             " methodPointer=" + hex_str((uintptr_t)r.ptr) + " (" + rva_of(r.ptr) + ")");

    if (!r.ptr)
    {
        log_line("[steamhack] " + std::string(f.label) +
                 ": get_HasValue 的 methodPointer 为空(可能未 AOT 编译) —— **只记日志, 不挂钩**(fail-safe)");
        return nullptr;
    }

    f.hasvalue_method = r.mi;
    return r.mi;
}

// 安装第 5 个 hook: Nullable<Lobby>.get_HasValue -> 恒 false。
bool install_hasvalue_hook(const cesium_safe::il2cpp_tbl& t, TaskFactory& f)
{
    void* mi = resolve_hasvalue_method(t, f);
    if (!mi) return false;

    void* target = method_pointer_of(mi);
    if (!target) return false;   // resolve_hasvalue_method 已记过日志

    // 纯提示: "恒 false" 在共享泛型代码下的影响范围无法安全判定(见函数注释), 只记一条日志。
    log_hasvalue_scope_caution(f, target);

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_NullableHasValue, (LPVOID*)&g_real_hasvalue);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHook(Nullable.get_HasValue) 失败: " + std::to_string((int)st) +
                 " (target=" + hex_str((uintptr_t)target) + ") —— 本次不挂钩(fail-safe)");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(Nullable.get_HasValue) 失败: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_hv_target = target;
    g_hv_method = mi;
    g_hv_hooked = true;
    f.hasvalue_ok = true;
    log_line("[steamhack] " + f.param_class_name +
             "::get_HasValue inline hook 已启用 (恒返回 false: 任何 Lobby? 都视同无值, "
             "游戏侧 LobbyId 保持 0 -> 走 RequestCreateC2S)");
    return true;
}

// =====================================================================================
// 方案B(安全网): Steamworks.Data.Lobby 上会抛 NRE 的方法 -> 无害
// =====================================================================================
//
// 为什么需要它: 方案C 之外的兜底。万一 direct 的 ABI 假设不成立、hasValue 仍被判成 true,
// 游戏就会执行 UICom_CreateRoom.cs:135-141 这段:
//     Lobby value = val.Value;
//     ((Lobby)(ref value)).SetPublic();                              // 136 行 <- 实测 NRE 炸在这里
//     ((Lobby)(ref value)).SetJoinable(true);                        // 138 行 <- 紧接着也会炸
//     steamLobbyId = SteamId.op_Implicit(((Lobby)(ref value)).Id);   // 140 行
// 这三个成员都是**非泛型的 Steamworks.Data.Lobby 值类型实例成员**, 不存在共享泛型代码问题,
// 被 HybridCLR 解释执行时会真实走到 AOT 入口(与阶段3 的 Nullable.HasValue 不同 —— 那个被解释器
// 当内建指令内联处理, hook 从未命中, 阶段3 的路线已实测作废)。
//   1) SetPublic()       -> no-op   签名 void(void* thisPtr)
//   2) SetJoinable(bool) -> no-op   签名 void(void* thisPtr, bool) —— **绝不解引用 thisPtr**
//   3) Id: 先判定是字段还是属性 ——
//        属性(存在 get_Id) -> 恒返回 0(SteamId 是 8 字节结构体, x64 下走 RAX 返回);
//        字段             -> **只记日志**(字段不可 hook), 不硬来。
// 目的: 即使 hasValue 仍为 true, 这两/三个调用也不再抛异常, 第 142 行
// RequestCreateC2S(..., steamLobbyId=0, ...) 照样执行。
// 失败一律只记日志(fail-safe): 解析不到类/方法、methodPointer 为空、MinHook 失败都只是"回到当前的
// NRE 状态", 绝不让安装期崩游戏。

using Fn_LobbyVoid = void (*)(void* self);
using Fn_LobbyBool = void (*)(void* self, bool joinable);
// get_Id 的返回类型是 SteamId(值类型, 8 字节): x64 ABI 下这种小结构体走 RAX 返回,
// 所以"恒返回 0"就是让 RAX=0 —— 用 void* 返回值声明即可。
// **尺寸在安装期核对过**(见 install_lobby_id): >8 字节的结构体走隐藏返回缓冲区, 那时返回 0 会写错
// 位置, 必须拒绝挂钩而不是硬来。
using Fn_LobbyGetId = void* (*)(void* self);

void* g_lsp_target = nullptr;   // Lobby.SetPublic 的原始 methodPointer
void* g_lsj_target = nullptr;   // Lobby.SetJoinable 的原始 methodPointer
void* g_lid_target = nullptr;   // Lobby.get_Id 的原始 methodPointer
void* g_lid_method = nullptr;   // Lobby.get_Id 的 MethodInfo*(诊断/日志)
Fn_LobbyVoid   g_real_lsp = nullptr;
Fn_LobbyBool   g_real_lsj = nullptr;
Fn_LobbyGetId  g_real_lid = nullptr;
bool g_lsp_hooked = false;
bool g_lsj_hooked = false;
bool g_lid_hooked = false;
std::atomic<long> g_lsp_hits{ 0 };
std::atomic<long> g_lsj_hits{ 0 };
std::atomic<long> g_lid_hits{ 0 };

void Hook_LobbySetPublic(void* /*self*/)
{
    // no-op: 原实现会去调 SteamMatchmaking.SetLobbyType(Id, ...) 并在 Steam 未初始化时抛异常。
    // **故意不解引用 self** —— 什么也不做, 也就不需要它有效。
    if (g_lsp_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] Lobby.SetPublic() 首次被拦截 -> no-op(不再抛 NullReferenceException)");
}

void Hook_LobbySetJoinable(void* /*self*/, bool joinable)
{
    // no-op: 同理不解引用 self。joinable 是基元类型(bool 走第二个寄存器参数的低字节),
    // 记它是安全的 —— 但**不碰 self**。
    if (g_lsj_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] Lobby.SetJoinable(" + std::string(joinable ? "true" : "false") +
                 ") 首次被拦截 -> no-op(不再抛 NullReferenceException)");
}

void* Hook_LobbyGetId(void* /*self*/)
{
    // 恒返回 0 = SteamId 全零 -> 游戏侧 SteamId.op_Implicit(...) 得 0
    // -> 协议里"无 Steam 大厅"的合法值。
    if (g_lid_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] Lobby.get_Id 首次被拦截 -> 恒返回 0(SteamId 零值, 游戏侧 steamLobbyId=0)");
    return nullptr;
}

// Steamworks.NET 里 Steamworks.Data.Lobby 的命名空间是 "Steamworks.Data"; 其余为兜底。
const char* kLobbyNamespaces[] = { "Steamworks.Data", "Steamworks", "", "Steamworks.NET" };

// 在域内全部镜像里找 Steamworks.Data.Lobby(与 resolve_steam_matchmaking 同一套路)。
void* resolve_lobby_class(const cesium_safe::il2cpp_tbl& t, std::string* out_image, std::string* out_ns)
{
    struct { const char* n; const void* p; } need[] = {
        { "domain_get",            (const void*)t.domain_get },
        { "domain_get_assemblies", (const void*)t.domain_get_assemblies },
        { "assembly_get_image",    (const void*)t.assembly_get_image },
        { "image_get_name",        (const void*)t.image_get_name },
        { "class_from_name",       (const void*)t.class_from_name },
    };
    std::string missing;
    for (auto& n : need) if (!n.p) { if (!missing.empty()) missing += ", "; missing += n.n; }
    if (!missing.empty())
    {
        log_line("[steamhack] 方案B: IL2CPP 函数表缺失(" + missing + ") —— 无法解析 Steamworks.Data.Lobby");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line("[steamhack] 方案B: il2cpp domain 为空, 无法解析 Lobby");
        return nullptr;
    }

    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line("[steamhack] 方案B: domain_get_assemblies 返回空");
        return nullptr;
    }

    void* chosen = nullptr;
    std::string chosen_image;
    std::string chosen_ns;
    std::string all;
    for (size_t i = 0; i < count; i++)
    {
        if (!assemblies[i]) continue;
        void* image = t.assembly_get_image(assemblies[i]);
        if (!image) continue;
        const std::string iname = cstr_or(t.image_get_name(image), "(null)");
        if (!all.empty()) all += ", ";
        all += iname;

        for (const char* ns : kLobbyNamespaces)
        {
            void* cls = t.class_from_name(image, ns, "Lobby");
            if (!cls) continue;
            log_line("[steamhack] 方案B: 命中类 Lobby: image=" + iname + " ns=\"" + std::string(ns) +
                     "\" Il2CppClass=" + hex_str((uintptr_t)cls) +
                     " 类名=" + class_name_of(t, cls));
            const bool better = (!chosen) ||
                                (chosen_image.find("Steamworks") == std::string::npos &&
                                 iname.find("Steamworks") != std::string::npos);
            if (better) { chosen = cls; chosen_image = iname; chosen_ns = ns; }
            break;   // 同一个 image 不必再试其他命名空间
        }
    }

    if (!chosen)
    {
        log_line("[steamhack] 方案B: 未找到类 Steamworks.Data.Lobby(候选 ns: \"Steamworks.Data\" / "
                 "\"Steamworks\" / 全局 / \"Steamworks.NET\")");
        log_line("[steamhack] 方案B: 域内程序集 " + std::to_string(count) + " 个: " + all);
    }
    if (out_image) *out_image = chosen_image;
    if (out_ns) *out_ns = chosen_ns;
    return chosen;
}

// 诊断(需求): 把 Lobby 类的**字段名列表**(上限 24 条)与**含 Id/Public/Join 的方法名列表**
// (上限 24 条)打进日志 —— 这是判定 "Lobby.Id 到底是字段还是属性" 的旁证。
void dump_lobby_members(const cesium_safe::il2cpp_tbl& t, void* cls)
{
    if (!cls) return;

    // ---- 字段名列表 ----
    if (t.class_get_fields && t.field_get_name)
    {
        void* iter = nullptr;
        std::string names;
        int shown = 0;
        for (;;)
        {
            void* f = t.class_get_fields(cls, &iter);
            if (!f) break;
            const char* n = t.field_get_name(f);
            if (!n) continue;
            if (shown >= 24) { names += ", ..."; break; }
            if (shown) names += ", ";
            names += n;
            shown++;
        }
        log_line("[steamhack] Lobby 类字段名(上限24): " +
                 (names.empty() ? std::string("(无)") : names));
    }
    else
    {
        log_line("[steamhack] Lobby 类字段名: 未能列出"
                 "(缺 il2cpp_class_get_fields 或 il2cpp_field_get_name 导出)");
    }

    // ---- 含 Id/Public/Join 的方法名列表 ----
    if (t.class_get_methods && t.method_get_name)
    {
        void* iter = nullptr;
        std::string names;
        int shown = 0;
        for (;;)
        {
            void* m = t.class_get_methods(cls, &iter);
            if (!m) break;
            const char* n = t.method_get_name(m);
            if (!n) continue;
            if (strstr(n, "Id") || strstr(n, "Public") || strstr(n, "Join"))
            {
                if (shown >= 24) { names += ", ..."; break; }
                if (shown) names += ", ";
                names += n;
                shown++;
            }
        }
        log_line("[steamhack] Lobby 类含 Id/Public/Join 的方法名(上限24): " +
                 (names.empty() ? std::string("(无)") : names));
    }
    else
    {
        log_line("[steamhack] Lobby 类含 Id/Public/Join 的方法名: 未能列出"
                 "(缺 il2cpp_class_get_methods 或 il2cpp_method_get_name 导出)");
    }
}

// 解析并挂钩 Lobby 上的一个"无害化"方法(SetPublic / SetJoinable)。
bool install_lobby_noop(const cesium_safe::il2cpp_tbl& t, void* cls,
                        const char* method_name, int arg_count,
                        void* detour, LPVOID* real_out, void** target_out)
{
    if (!t.class_get_method_from_name)
    {
        log_line("[steamhack] 方案B: il2cpp_class_get_method_from_name 导出缺失, 无法解析 Lobby." +
                 std::string(method_name));
        return false;
    }

    void* mi = t.class_get_method_from_name(cls, method_name, arg_count);
    if (!mi)
    {
        log_line("[steamhack] 方案B: 未命中 Lobby." + std::string(method_name) + "(" +
                 std::to_string(arg_count) + " 参数) —— 跳过该 hook");
        return false;
    }

    void* target = method_pointer_of(mi);
    log_line("[steamhack] 方案B: 命中 Lobby." + std::string(method_name) + " MethodInfo=" +
             hex_str((uintptr_t)mi) + " methodPointer=" + hex_str((uintptr_t)target) +
             " (" + rva_of(target) + ")");
    if (!target)
    {
        log_line("[steamhack] 方案B: Lobby." + std::string(method_name) +
                 " 的 methodPointer 为空(可能未 AOT 编译) —— **只记日志, 不挂钩**(fail-safe)");
        return false;
    }

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, detour, real_out);
    if (st != MH_OK)
    {
        log_line("[steamhack] 方案B: MH_CreateHook(Lobby." + std::string(method_name) + ") 失败: " +
                 std::to_string((int)st) + " (target=" + hex_str((uintptr_t)target) + ") —— 不挂钩");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] 方案B: MH_EnableHook(Lobby." + std::string(method_name) + ") 失败: " +
                 std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    if (target_out) *target_out = target;
    log_line("[steamhack] 方案B: Lobby." + std::string(method_name) +
             " inline hook 已启用 (no-op, 不再抛 NullReferenceException)");
    return true;
}

// Lobby.Id: **先判定它是字段还是属性**(需求明确要求把结论写进日志), 再决定怎么处理。
//   *out_is_property 回传"存在 get_Id 属性"这一事实(不管挂钩成不成功), 供调用方算预期 hook 数。
//   字段     -> 不可 hook: **只记日志**, 不硬来。
//   属性     -> get_Id() 恒返回 0(SteamId 在 x64 下走 RAX 返回)。
bool install_lobby_id(const cesium_safe::il2cpp_tbl& t, void* cls, bool* out_is_property)
{
    if (out_is_property) *out_is_property = false;

    // ---- 需求: 判定 Id 是字段还是属性(两边的 API 都试, 结论写进日志) ----
    void* fld = (t.class_get_field_from_name ? t.class_get_field_from_name(cls, "Id") : nullptr);
    void* mi = (t.class_get_method_from_name ? t.class_get_method_from_name(cls, "get_Id", 0) : nullptr);

    log_line("[steamhack] Lobby.Id 判定: il2cpp_class_get_field_from_name(\"Id\")=" +
             std::string(fld ? "命中 -> **是字段**" : "未命中 -> 不是字段") +
             " | il2cpp_class_get_method_from_name(\"get_Id\", 0)=" +
             std::string(mi ? "命中 -> **是属性**" : "未命中 -> 没有 get_Id 属性"));

    // 交叉印证: 走属性 API 再确认一次(缺导出就跳过, 只记日志)
    if (t.class_get_property_from_name && t.property_get_get_method)
    {
        void* prop = t.class_get_property_from_name(cls, "Id");
        void* gm = prop ? t.property_get_get_method(prop) : nullptr;
        log_line("[steamhack] Lobby.Id 交叉印证: il2cpp_class_get_property_from_name(\"Id\")=" +
                 std::string(prop ? "命中" : "未命中") + " getter=" + hex_str((uintptr_t)gm) +
                 (gm == mi ? " (与 get_Id 一致)" : ""));
    }
    else
    {
        log_line("[steamhack] Lobby.Id 交叉印证: 跳过"
                 "(缺 il2cpp_class_get_property_from_name 或 il2cpp_property_get_get_method 导出)");
    }

    if (fld)
    {
        log_line("[steamhack] Lobby.Id 结论 = **字段**(不是属性) —— 字段不可 hook"
                 "(HybridCLR 解释执行时按字段偏移直读, 没有 AOT 入口可挂)。"
                 "方案B 对 Id **只记日志, 不硬来**; steamLobbyId 的值由方案C(方案C 让 hasValue 为 false "
                 "时根本不会走到 Id)与 hasValue 判定共同决定");
        return false;
    }
    if (!mi)
    {
        log_line("[steamhack] Lobby.Id 结论 = 既不是字段, 也没有 get_Id() 属性 —— 不挂钩(fail-safe)");
        return false;
    }

    if (out_is_property) *out_is_property = true;

    void* target = method_pointer_of(mi);
    if (!target)
    {
        log_line("[steamhack] Lobby.get_Id 的 methodPointer 为空(可能未 AOT 编译) —— "
                 "**只记日志, 不挂钩**(fail-safe)");
        return false;
    }

    // ---- 返回值核对(关键安全门): 只有"<= 8 字节的值类型"才能用 RAX=0 直接返回。
    //      >8 字节的结构体在 x64 ABI 下走**隐藏返回缓冲区**(调用方传缓冲区指针), 那时返回 0
    //      会写错位置 —— 那才是真的会崩。有 il2cpp_class_value_size 就核对, 没有就只记日志
    //      (预期是 Steamworks.Data.SteamId, 8 字节)。
    if (t.method_get_return_type && t.type_get_name)
    {
        void* rt = t.method_get_return_type(mi);
        const std::string rt_name = cstr_or(t.type_get_name(rt), "(null)");
        void* rt_obj = (rt && t.type_get_object) ? t.type_get_object(rt) : nullptr;
        void* rt_cls = (rt_obj && t.class_from_system_type) ? t.class_from_system_type(rt_obj) : nullptr;

        bool have_vt = false, is_vt = false;
        if (rt_cls && t.class_is_valuetype)
        {
            is_vt = t.class_is_valuetype(rt_cls);
            have_vt = true;
        }
        bool have_sz = false;
        unsigned sz = 0;
        if (rt_cls && t.class_value_size)
        {
            sz = (unsigned)t.class_value_size(rt_cls, nullptr);
            have_sz = true;
        }

        log_line("[steamhack] Lobby.get_Id 返回类型=" + rt_name +
                 " 值类型=" + (have_vt ? (is_vt ? "是" : "否") : "未能判定") +
                 (have_sz ? (" 尺寸=" + std::to_string(sz) + " 字节")
                          : std::string(" 尺寸=(il2cpp_class_value_size 导出缺失, 未核对)")));

        if (have_vt && is_vt && have_sz && sz > 8)
        {
            log_line("[steamhack] Lobby.get_Id 返回结构体 " + std::to_string(sz) +
                     " 字节(>8) —— x64 ABI 下走**隐藏返回缓冲区**, 用 RAX=0 返回会写错位置, "
                     "**不挂钩**(fail-safe; 由方案C 让 hasValue=false 来避免走到这里)");
            return false;
        }
        if (have_vt && !is_vt)
        {
            log_line("[steamhack] Lobby.get_Id 返回的是**引用类型**(" + rt_name +
                     ") —— 返回 0 会变成 null 引用(语义不对, 可能引出别的异常), **不挂钩**(fail-safe)");
            return false;
        }
        if (!have_sz)
            log_line("[steamhack] Lobby.get_Id: 未能核对返回结构体尺寸 —— 按"
                     "“SteamId 是 8 字节值类型、走 RAX 返回”的预期继续(这是方案B 唯一的 ABI 假设)");
    }
    else
    {
        log_line("[steamhack] Lobby.get_Id: 无法核对返回类型(缺 il2cpp_method_get_return_type / "
                 "il2cpp_type_get_name) —— 按“SteamId 是 8 字节值类型、走 RAX 返回”的预期继续");
    }

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_LobbyGetId, (LPVOID*)&g_real_lid);
    if (st != MH_OK)
    {
        log_line("[steamhack] 方案B: MH_CreateHook(Lobby.get_Id) 失败: " + std::to_string((int)st) +
                 " (target=" + hex_str((uintptr_t)target) + ") —— 不挂钩");
        return false;
    }
    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] 方案B: MH_EnableHook(Lobby.get_Id) 失败: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_lid_target = target;
    g_lid_method = mi;
    g_lid_hooked = true;
    log_line("[steamhack] 方案B: Lobby.get_Id inline hook 已启用 (恒返回 SteamId 零值 -> steamLobbyId=0)");
    return true;
}

// 安装方案B 的全部 hook。返回实际装上的数量(0~3);
// *out_expected 回传预期数量(Id 是**属性**时 3, 是字段/不存在时 2)。
int install_lobby_methods(const cesium_safe::il2cpp_tbl& t, int* out_expected)
{
    int installed = 0;
    if (out_expected) *out_expected = 2;   // SetPublic + SetJoinable

    std::string image;
    std::string ns;
    void* cls = resolve_lobby_class(t, &image, &ns);
    if (!cls)
    {
        log_line("[steamhack] 方案B: 未能解析 Steamworks.Data.Lobby 类 —— 跳过 Lobby 方法无害化"
                 "(fail-safe, 行为保持当前状态)");
        return 0;
    }

    const bool cls_is_vt = (t.class_is_valuetype ? t.class_is_valuetype(cls) : false);
    log_line("[steamhack] 方案B: 采用类 Lobby image=" + image + " ns=\"" + ns +
             "\" Il2CppClass=" + hex_str((uintptr_t)cls) + " 类名=" + class_name_of(t, cls) +
             " 值类型=" + (cls_is_vt ? "是(struct)" : "否(class)"));

    // 需求: 字段名列表 + 含 Id/Public/Join 的方法名列表, 各上限 24 条
    dump_lobby_members(t, cls);

    if (install_lobby_noop(t, cls, "SetPublic", 0, (void*)&Hook_LobbySetPublic,
                           (LPVOID*)&g_real_lsp, &g_lsp_target))
    {
        g_lsp_hooked = true;
        installed++;
    }
    if (install_lobby_noop(t, cls, "SetJoinable", 1, (void*)&Hook_LobbySetJoinable,
                           (LPVOID*)&g_real_lsj, &g_lsj_target))
    {
        g_lsj_hooked = true;
        installed++;
    }

    bool id_is_property = false;
    if (install_lobby_id(t, cls, &id_is_property)) installed++;
    if (out_expected) *out_expected = 2 + (id_is_property ? 1 : 0);

    return installed;
}

// =====================================================================================
// 阶段5: LobbyQuery.RequestAsync() -> 结果为空 Lobby[] 的已完成 Task<Lobby[]>
// =====================================================================================
//
// 故障(实机实测, 6 条 NRE):
//     NullReferenceException
//     GameLogic.RoomLogic.OnExitRoomS2CServerCallBack (party.protocol.ExitRoomS2C model, ...)
//     Core.Net.NetManager.Update ()
//     Steamworks.Data.LobbyQuery.RequestAsync ()          <- NRE 真正抛出点
// 对应 decomp_full\GameLogic\RoomLogic.cs:381-401(退房/解散)与 :545-573(被踢)同构:
//     if (model.Dissolve || model.PlayerId == self)                       // :381
//     {
//         LobbyQuery lobbyList = SteamMatchmaking.LobbyList;              // :383
//         Lobby[] array = await ((LobbyQuery)(ref lobbyList)).RequestAsync();  // :384 <- 这里抛
//         if (array != null && array.Length > 0) { ...Leave()... }        // :385
//     }
//     roomController.UpdateRoomByExit(...);                               // :398 <- 被跳过
//     if (self || model.Dissolve) ClearRoomInfo();                        // :401 <- 被跳过
//
// ★ 对文档里那行"未实测"的更正(有实测数据了, 据此更正):
//   此前文档写的是"`SteamMatchmaking.LobbyList` 属性/查询无判空, Steam 未初始化时行为**未实测**,
//   最坏是访问违例"。实测把这一点确定了: **NRE 抛在 await 的 RequestAsync() 上, 不是 LobbyList
//   取值本身**(栈顶就是 Steamworks.Data.LobbyQuery.RequestAsync)。危害也不是"最坏是访问违例",
//   而是 **await 之后的代码全部不执行** —— 本地房间状态不清、被踢提示不弹、不返回房间列表页。
//
// 修法: 挂钩 RequestAsync() -> 恒返回一个**预建好的、结果为长度 0 的 Lobby[] 的已完成 Task<Lobby[]>**。
//   于是 array 是长度 0 的数组 -> (array != null && array.Length > 0) 为 false -> 跳过 Steam 大厅
//   清理 -> :398 / :401 / :567-573 正常执行。
//
// 影响面为零: 全仓 grep 确认全游戏**只有这 2 处**调用 LobbyQuery.RequestAsync
//   (RoomLogic.cs:384 与 :554), 所以挂钩这一个方法即可, 对其它 Steam 行为零影响。
//
// 与阶段2/方案C 的关系:
//   阶段2 处理的是 `Task<Lobby?>` —— 泛型参数是 **Nullable<Lobby> 值类型**, 于是踩到
//   il2cpp_runtime_invoke 的编组 bug, 需要方案C 的 direct ABI + hasValue 规避。
//   阶段5 的泛型参数是 **引用类型 Lobby[]**, 不涉及 Nullable 装箱: 要传的实参就是
//   "一个长度 0 的 Lobby[] 数组对象指针"。所以这里**复用方案C 已验证的 direct 原生 ABI 直调
//   ctor 机制**(pod_call_lobby_query_ctor 与 pod_call_task_ctor_direct 是同一套调用约定),
//   只把"指向全零栈缓冲区的指针"换成"数组对象指针"。
//
// 失败一律 fail-safe(**只记日志, 不挂钩**): 解析不到类/方法、methodPointer 为空、ctor 参数被
//   判定为值类型、探针校验不通过、MinHook 失败 —— 都只是"回到当前那 6 条 NRE",
//   绝不让安装期崩游戏(前面几轮的硬要求)。

// RequestAsync 是 LobbyQuery 的实例方法且无参数, 原生签名 = (LobbyQuery* __this, const MethodInfo* method)。
// 第一个参数是指向该值的指针 —— 与"LobbyQuery 到底是 struct 还是 class"无关(两种情况下都是一个指针),
// 本模块**不解引用它**。多余/缺失的寄存器参数对"恒返回同一个指针"的逻辑没有任何影响。
using Fn_LobbyQueryRequestAsync = void* (*)(void* this_ptr, void* method_info);

Fn_LobbyQueryRequestAsync g_real_lq_request = nullptr;   // MinHook trampoline(保留备用)
void* g_lq_target = nullptr;      // RequestAsync 的原始 methodPointer
void* g_lq_method = nullptr;      // RequestAsync 的 MethodInfo*(诊断/日志)
bool  g_lq_hooked = false;
std::atomic<long> g_lq_hits{ 0 };

// ---- GC root(设计说明, 重要) ----
// 这个 Task 只构造**一次**, 之后每一次 RequestAsync 调用都返回**同一个对象**(见下)。
// 它必须是 GC root, 否则被回收之后再被我们返回给托管代码就是 use-after-free(运行期崩溃)。
//   * g_lq_task / g_lq_empty_array 是**模块级全局变量**(静态存储期) —— Boehm 会扫描已加载模块的
//     可写数据段, 正常情况下这足以当根。
//   * **额外**再用 il2cpp_gchandle_new 显式注册成根(该导出存在时): "Boehm 到底扫不扫我们这张
//     DLL 的 .data"不是本模块能保证的事, 而一旦不是, 后果是崩溃而不是"功能失效"。显式句柄是
//     确定性的保证。导出缺失时只用全局变量, 并在日志里如实标注。
// 为什么复用是安全的: Task<TResult> 是不可变对象, get_Result() 恒返回同一个空数组; 代价只是常驻
// 一个小对象对(GC 永不回收它 —— 这是刻意的)。
void* g_lq_task = nullptr;              // Task<Lobby[]>(已完成, m_result = 长度 0 的 Lobby[])
void* g_lq_empty_array = nullptr;       // 长度 0 的 Lobby[](被上面的 Task 引用)
uint32_t g_lq_task_gchandle = 0;        // il2cpp_gchandle_new 的句柄(0 = 未持有/导出缺失/失败)
void* g_lq_task_class = nullptr;        // Task<Lobby[]> 的 Il2CppClass*(诊断)
void* g_lq_array_class = nullptr;       // Lobby[] 的 Il2CppClass*(探针的类型校验用)

// 恒返回**预建好的同一个** Task 对象; 该对象在安装期已构造 + 校验通过(g_lq_task 非空才会挂钩)。
void* Hook_LobbyQueryRequestAsync(void* this_ptr, void* method_info)
{
    // 只在首次拦截时记一行(原子计数, 不刷屏)。
    // **这行日志是否存在是判断本 hook 是否真的被调用的唯一依据** —— 阶段3 的教训:
    // "安装成功"只说明 MH_CreateHook 返回了 MH_OK, 并不代表它会被调用。
    if (g_lq_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] LobbyQuery.RequestAsync 首次被拦截: this=" +
                 hex_str(reinterpret_cast<uintptr_t>(this_ptr)) +
                 " (LobbyQuery 按引用传参, 本模块不解引用) 第2寄存器=" +
                 hex_str(reinterpret_cast<uintptr_t>(method_info)) +
                 " -> 恒返回预建好的 Task<Lobby[]>(结果 = 长度 0 的 Lobby[]); "
                 "游戏侧 array != null && array.Length > 0 为 false -> 跳过 Steam 大厅清理, "
                 "await 之后的 UpdateRoomByExit / ClearRoomInfo 正常执行");
    return g_lq_task;
}

// Steamworks.NET 里 LobbyQuery / Lobby 的命名空间是 "Steamworks.Data"; 其余为兜底。
const char* kSteamworksDataNamespaces[] = { "Steamworks.Data", "Steamworks", "", "Steamworks.NET" };

// 在域内全部镜像里找 ns.className。label 只用于日志前缀。
// 与 resolve_lobby_class(方案B)同一套路, 但**独立成一份** —— 不改动方案B 那份已实机验证过的代码,
// 同时让日志前缀是 "阶段5" 而不是 "方案B", 便于排障。
void* resolve_steamworks_class(const cesium_safe::il2cpp_tbl& t, const char* label,
                               const char* class_name,
                               std::string* out_image, std::string* out_ns)
{
    struct { const char* n; const void* p; } need[] = {
        { "domain_get",            (const void*)t.domain_get },
        { "domain_get_assemblies", (const void*)t.domain_get_assemblies },
        { "assembly_get_image",    (const void*)t.assembly_get_image },
        { "image_get_name",        (const void*)t.image_get_name },
        { "class_from_name",       (const void*)t.class_from_name },
    };
    std::string missing;
    for (auto& n : need) if (!n.p) { if (!missing.empty()) missing += ", "; missing += n.n; }
    if (!missing.empty())
    {
        log_line(std::string("[steamhack] ") + label + ": IL2CPP 函数表缺失(" + missing +
                 ") —— 无法解析 " + class_name + ", 不挂钩(fail-safe)");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line(std::string("[steamhack] ") + label + ": il2cpp domain 为空 —— 解析不到 " + class_name);
        return nullptr;
    }
    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line(std::string("[steamhack] ") + label + ": domain_get_assemblies 返回空");
        return nullptr;
    }

    void* chosen = nullptr;
    std::string chosen_image;
    std::string chosen_ns;
    std::string all;
    for (size_t i = 0; i < count; i++)
    {
        if (!assemblies[i]) continue;
        void* image = t.assembly_get_image(assemblies[i]);
        if (!image) continue;
        const std::string iname = cstr_or(t.image_get_name(image), "(null)");
        if (!all.empty()) all += ", ";
        all += iname;

        for (const char* ns : kSteamworksDataNamespaces)
        {
            void* cls = t.class_from_name(image, ns, class_name);
            if (!cls) continue;
            const bool is_vt = (t.class_is_valuetype ? t.class_is_valuetype(cls) : false);
            log_line(std::string("[steamhack] ") + label + ": 命中类 " + class_name + ": image=" + iname +
                     " ns=\"" + std::string(ns) + "\" Il2CppClass=" + hex_str((uintptr_t)cls) +
                     " 类名=" + class_name_of(t, cls) +
                     " 值类型=" + (is_vt ? "是" : "否"));
            const bool better = (!chosen) ||
                                (chosen_image.find("Steamworks") == std::string::npos &&
                                 iname.find("Steamworks") != std::string::npos);
            if (better) { chosen = cls; chosen_image = iname; chosen_ns = ns; }
            break;   // 同一个 image 不必再试其他命名空间
        }
    }

    if (!chosen)
    {
        log_line(std::string("[steamhack] ") + label + ": 未找到类 " + class_name +
                 "(候选 ns: \"Steamworks.Data\" / \"Steamworks\" / 全局 / \"Steamworks.NET\")");
        log_line(std::string("[steamhack] ") + label + ": 域内程序集 " + std::to_string(count) + " 个: " + all);
    }
    if (out_image) *out_image = chosen_image;
    if (out_ns) *out_ns = chosen_ns;
    return chosen;
}

// 【危险调用 #6】按原生 ABI 直调 Task<Lobby[]>::.ctor(Lobby[])。只允许 POD + __try 兜底。
//
// 与 pod_call_task_ctor_direct(方案C)是**同一套调用约定**, 唯一区别是第二个实参:
//   方案C: "指向 256B 全零栈缓冲区的指针" = 空 Nullable<Lobby>(值类型按引用传参);
//   阶段5: "长度 0 的 Lobby[] 对象指针"     = 引用类型, 指针本身就是实参。
// 两种情况在 Windows x64 ABI 下第二个寄存器参数都是一个指针(实参按指针大小传递), 所以调用方式一致。
// 第三个参数仍按 IL2CPP 生成代码的惯例补上 MethodInfo*:
//   void Task_1_ctor(Task_1_t* __this, T_t result, const MethodInfo* method)
struct PodLobbyQueryCtor
{
    void* object;            // 构造好的 Task 对象(nullptr = 失败)
    unsigned long seh_code;  // 捕获到的 SEH 异常码(0 = 未捕获)
    int   alloc_ok;          // il2cpp_object_new 是否成功
    int   called;            // 是否真的把 ctor 当原生函数调用过
    int   skipped;           // 1 = 未尝试(缺 object_new / ctor_ptr / task_cls)
    int   arg_was_null;      // 1 = 降级路径: 实参传的是 nullptr(而不是空数组)
};

void pod_call_lobby_query_ctor(void* object_new_fn, void* task_cls, void* ctor_ptr,
                               void* ctor_mi, void* arg, PodLobbyQueryCtor* out)
{
    out->object = nullptr;
    out->seh_code = 0;
    out->alloc_ok = 0;
    out->called = 0;
    out->skipped = 0;
    out->arg_was_null = (arg == nullptr) ? 1 : 0;

    if (!object_new_fn || !task_cls || !ctor_ptr)
    {
        out->skipped = 1;
        return;
    }

    __try
    {
        typedef void* (*ObjNewFn)(void*);
        void* obj = ((ObjNewFn)object_new_fn)(task_cls);
        if (!obj) return;
        out->alloc_ok = 1;

        // null 实参的语义是"结果为空引用": 游戏侧 array != null 为 false, 同样跳过 Steam 大厅清理。
        // 所以降级路径也是安全的(只是日志里会被明确标注)。
        typedef void (*CtorFn)(void* this_obj, void* p_arg, void* method_info);
        out->called = 1;
        ((CtorFn)ctor_ptr)(obj, arg, ctor_mi);

        out->object = obj;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // 捕获访问违例等 SEH: 只作废这一次调用, 进程继续(安装期 -> 不挂钩)。
        out->seh_code = GetExceptionCode();
        out->object = nullptr;
    }
}

// 【危险调用 #7】造一个长度 0 的元素数组。POD + __try。
// **注意参数语义**: il2cpp_array_new(klass, len) 的第 1 个参数是**元素类型**(Lobby),
// 不是数组类型(Lobby[]) —— 收数组类型的是 il2cpp_array_new_specific。传错会得到 Lobby[][]。
void pod_array_new_empty(void* array_new_fn, void* element_cls, void** out_arr, unsigned long* out_seh)
{
    *out_arr = nullptr;
    *out_seh = 0;
    if (!array_new_fn || !element_cls) return;

    __try
    {
        typedef void* (*ArrayNewFn)(void*, size_t);
        *out_arr = ((ArrayNewFn)array_new_fn)(element_cls, 0);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *out_seh = GetExceptionCode();
        *out_arr = nullptr;
    }
}

// 【危险调用 #8】读 IL2CPP 数组的元素个数(探针校验 + 诊断)。POD + __try。
// 优先用**权威导出** il2cpp_array_length; 缺失时退化为按数组布局读 max_length:
//   x64 IL2CPP 数组 = Il2CppObject(16) + bounds(8) + max_length -> 数据起点 32;
//   il2cpp_array_object_header_size() 返回的正是"数据起点", max_length 紧邻它之前。
// 这里只读低 4 字节并与 0 比较 —— 无论 max_length 是 4 字节还是 8 字节, "是否等于 0" 都成立。
struct PodArrayLength
{
    int length;              // -1 = 读不到
    int source;              // 0 未读 / 1 il2cpp_array_length / 2 布局偏移
    unsigned long seh_code;  // 捕获到的 SEH 异常码(0 = 未捕获)
};

void pod_array_length(const cesium_safe::il2cpp_tbl* t, void* arr, PodArrayLength* out)
{
    out->length = -1;
    out->source = 0;
    out->seh_code = 0;
    if (!arr) return;

    __try
    {
        if (t->array_length)
        {
            out->length = (int)t->array_length(arr);
            out->source = 1;
            return;
        }
        if (t->array_object_header_size)
        {
            const size_t hs = t->array_object_header_size();
            if (hs >= 16)
            {
                uint32_t v = 0;
                memcpy(&v, reinterpret_cast<const unsigned char*>(arr) + hs - 8, sizeof(v));
                out->length = (int)v;
                out->source = 2;
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        out->seh_code = GetExceptionCode();
        out->length = -1;
        out->source = 0;
    }
}

// 【危险调用 #9】il2cpp_gchandle_new(obj, pinned=false): 把预建好的 Task 显式注册成 GC root。
// 返回 0 视为"未成功"(日志里如实标注), 那时只靠模块级全局变量作根。POD + __try。
void pod_gchandle_new(void* gchandle_new_fn, void* obj, uint32_t* out_handle, unsigned long* out_seh)
{
    *out_handle = 0;
    *out_seh = 0;
    if (!gchandle_new_fn || !obj) return;

    __try
    {
        typedef uint32_t (*GcHandleNewFn)(void*, bool);
        *out_handle = ((GcHandleNewFn)gchandle_new_fn)(obj, false);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *out_seh = GetExceptionCode();
        *out_handle = 0;
    }
}

// 探针: 校验预建好的 Task 是 "IsCompleted == true 且 get_Result() 是**长度 0** 的 Lobby[]"。
// 返回 true = 校验通过(可以挂钩); false = 不可信(**不挂钩**, 宁可不挂钩也不给游戏一个错误对象)。
// 复用方案C 的 pod_runtime_invoke(已带 __try 兜底)与 boxed_byte。
bool probe_lobby_query_task(const cesium_safe::il2cpp_tbl& t, void* task, void* task_cls,
                            void* array_cls, int* out_len, int* out_len_src)
{
    if (out_len) *out_len = -1;
    if (out_len_src) *out_len_src = 0;

    if (!task || !t.runtime_invoke)
    {
        log_line("[steamhack] 阶段5[探针]: 缺 il2cpp_runtime_invoke 或任务对象为空 —— "
                 "跳过完成态/结果校验(仅按构造成功放行)");
        return true;
    }

    void* cls = t.object_get_class ? t.object_get_class(task) : nullptr;
    if (!cls) cls = task_cls;
    if (!cls)
    {
        log_line("[steamhack] 阶段5[探针]: 判不出任务对象的类 —— 跳过校验");
        return true;
    }

    void* args[1] = { nullptr };

    // ---- 1) 完成态: IsCompleted 必须为 true(否则游戏 await 会永久挂起) ----
    void* get_completed = t.class_get_method_from_name
                              ? t.class_get_method_from_name(cls, "get_IsCompleted", 0)
                              : nullptr;
    if (!get_completed) get_completed = find_method_up_chain(t, cls, "get_IsCompleted");
    if (!get_completed || !method_pointer_of(get_completed))
    {
        log_line("[steamhack] 阶段5[探针]: 找不到可用的 IsCompleted getter —— 跳过完成态校验");
    }
    else
    {
        void* exc = nullptr;
        void* boxed = nullptr;
        unsigned long seh = 0;
        pod_runtime_invoke((void*)t.runtime_invoke, get_completed, task, args, &boxed, &exc, &seh);
        if (seh || exc || !boxed)
        {
            log_line(std::string("[steamhack] 阶段5[探针]: IsCompleted 调用失败") +
                     (seh ? ("(SEH " + hex_code_str(seh) + ")") : exc ? "(托管异常)" : "(返回空)") +
                     " —— 校验失败, 不挂钩");
            return false;
        }
        const bool completed = boxed_byte(t, boxed) != 0;
        log_line("[steamhack] 阶段5[探针]: IsCompleted=" + std::string(completed ? "true" : "false"));
        if (!completed)
        {
            log_line("[steamhack] 阶段5[探针]: 任务不是 RanToCompletion —— 不挂钩"
                     "(否则游戏 await 会永久挂起)");
            return false;
        }
    }

    // ---- 2) get_Result() 必须是长度 0 的 Lobby[] ----
    // 已完成 -> get_Result 不会阻塞, 可以安全调用。
    void* get_result = t.class_get_method_from_name
                           ? t.class_get_method_from_name(cls, "get_Result", 0)
                           : nullptr;
    if (!get_result || !method_pointer_of(get_result))
    {
        log_line("[steamhack] 阶段5[探针]: 任务类上拿不到 get_Result() —— 无法校验结果, 判失败, 不挂钩");
        return false;
    }
    void* rexc = nullptr;
    void* rv = nullptr;
    unsigned long rseh = 0;
    pod_runtime_invoke((void*)t.runtime_invoke, get_result, task, args, &rv, &rexc, &rseh);
    if (rseh || rexc)
    {
        log_line(std::string("[steamhack] 阶段5[探针]: get_Result() 调用失败") +
                 (rseh ? ("(SEH " + hex_code_str(rseh) + ")") : "(托管异常)") + " —— 不挂钩");
        return false;
    }
    if (!rv)
    {
        // 语义上 null 也能让游戏跳过(array != null 为 false), 但我们的目标是"长度 0 的数组";
        // 拿到 null 说明 ctor 没把结果存进去(或实参被降级成 nullptr) —— 按"宁可不挂钩"处理。
        log_line("[steamhack] 阶段5[探针]: get_Result() 返回 null(不是长度 0 的 Lobby[]) —— "
                 "校验失败, 不挂钩(说明 ctor 没把数组存进结果)");
        return false;
    }

    // 结果对象的类是否是 Lobby[](只在两边都解析得出时判定)
    bool class_checked = false;
    bool class_is_array = false;
    if (t.object_get_class)
    {
        void* rcls = t.object_get_class(rv);
        class_checked = true;
        class_is_array = (rcls != nullptr && array_cls != nullptr && rcls == array_cls);
        log_line("[steamhack] 阶段5[探针]: get_Result() 非空 对象=" + hex_str((uintptr_t)rv) +
                 " 类=" + class_name_of(t, rcls) + " 与 Lobby[] 的 Il2CppClass 一致=" +
                 (array_cls ? (class_is_array ? "是" : "否") : "(未解析到 Lobby[], 跳过该判定)"));
    }

    // ---- 3) 元素个数必须是 0 ----
    PodArrayLength al;
    pod_array_length(&t, rv, &al);
    if (al.seh_code)
    {
        log_line("[steamhack] 阶段5[探针]: 读数组长度触发 SEH " + hex_code_str(al.seh_code) +
                 " —— 已被 __try/__except 捕获; 未能读取长度, 只按'非 NULL'放行");
    }
    else if (al.length >= 0)
    {
        log_line("[steamhack] 阶段5[探针]: 结果数组长度=" + std::to_string(al.length) + " (来源: " +
                 (al.source == 1 ? "il2cpp_array_length(权威导出)"
                                 : "数组布局偏移(il2cpp_array_object_header_size - 8)") + ")");
    }
    else
    {
        log_line("[steamhack] 阶段5[探针]: 未能读取数组长度(il2cpp_array_length 与 "
                 "il2cpp_array_object_header_size 都不可用) —— 只校验'非 NULL'");
    }
    if (out_len) *out_len = al.length;
    if (out_len_src) *out_len_src = al.source;

    if (al.length >= 0 && al.length != 0)
    {
        log_line("[steamhack] 阶段5[探针]: 结果数组长度不为 0 —— 校验失败, 不挂钩"
                 "(否则游戏会拿这个数组去 Leave() 真实大厅)");
        return false;
    }
    if (class_checked && array_cls && !class_is_array)
    {
        log_line("[steamhack] 阶段5[探针]: 结果对象的类不是 Lobby[] —— 校验失败, 不挂钩");
        return false;
    }

    log_line("[steamhack] 阶段5[探针]: 校验通过 —— 已完成 + get_Result() 是长度 0 的 Lobby[]"
             "(游戏侧 array != null && array.Length > 0 为 false, 跳过 Steam 大厅清理)");
    return true;
}

// 在 cls 上按**参数类型**精确挑出 ".ctor(1)"。
//
// 为什么不能只用 class_get_method_from_name(cls, ".ctor", 1):
//   System.Threading.Tasks.Task`1 上存在**多个** 1 参数构造函数 ——
//       internal Task(TResult result)              <- 我们要的这个(造出来就是 RanToCompletion)
//       public   Task(Func<TResult> function)      <- 名字/参数个数都匹配, 但不是我们要的
//       public   Task(Func<object, TResult> function) 等
//   class_get_method_from_name 返回的是"第一个 名字+参数个数 匹配"的那个, 未必是 Task(TResult)。
//   方案C(阶段2/4)实测拿到的确实是对的(探针 IsCompleted=true 通过), 但那是元数据顺序的运气;
//   这里**显式**按"参数类型 == Lobby[] 的 Il2CppClass"筛一遍。
// 筛不出来绝不硬来: 返回 nullptr 由调用方退回 class_get_method_from_name, 那条结果**仍要过探针校验**
//   —— 拿错 ctor 时造出来的任务 IsCompleted=false, 探针会拦下 -> 不挂钩(而不是给游戏一个坏对象)。
void* find_task_ctor_with_arg(const cesium_safe::il2cpp_tbl& t, void* cls, void* expected_arg_cls,
                              std::string* out_param_name, int* out_candidates)
{
    if (out_candidates) *out_candidates = 0;
    if (out_param_name) *out_param_name = "(未知)";
    if (!cls || !t.class_get_methods || !t.method_get_name || !t.method_get_param_count ||
        !t.method_get_param || !t.type_get_object || !t.class_from_system_type)
        return nullptr;

    void* found = nullptr;
    void* iter = nullptr;
    for (;;)
    {
        void* m = t.class_get_methods(cls, &iter);
        if (!m) break;
        const char* n = t.method_get_name(m);
        if (!n || strcmp(n, ".ctor") != 0) continue;
        if (t.method_get_param_count(m) != 1) continue;
        if (out_candidates) (*out_candidates)++;

        void* pt = t.method_get_param(m, 0);
        if (!pt) continue;
        void* pto = t.type_get_object(pt);
        void* pcls = pto ? t.class_from_system_type(pto) : nullptr;
        const std::string pn = t.type_get_name ? cstr_or(t.type_get_name(pt), "(null)")
                                               : std::string("(null)");

        if (expected_arg_cls && pcls == expected_arg_cls)
        {
            found = m;
            if (out_param_name) *out_param_name = pn;
            log_line("[steamhack] 阶段5: **已按参数类型精确命中** 类..ctor(1): 参数类型全名=" + pn +
                     " Il2CppClass=" + hex_str((uintptr_t)pcls) + " (与 Lobby[] 一致) MethodInfo=" +
                     hex_str((uintptr_t)m));
            break;
        }
        log_line("[steamhack] 阶段5: 枚举到一个 1 参数 .ctor 但**参数类型不匹配** —— 参数类型全名=" + pn +
                 " Il2CppClass=" + hex_str((uintptr_t)pcls) + " (期望 Lobby[]=" +
                 hex_str((uintptr_t)expected_arg_cls) + ")");
    }
    return found;
}

// 安装阶段5 hook。返回 true = 已挂上。
// 顺序严格遵循"**先把返回值造出来并校验通过, 再挂钩**":
//   解析 LobbyQuery / RequestAsync / Task<Lobby[]> / Lobby / .ctor(1) -> 造长度 0 的 Lobby[]
//   -> direct 直调 ctor 造 Task -> 探针校验 -> 记 GC root -> MH_CreateHook。
// 任何一步失败都**只记日志、不挂钩**(fail-safe)。
bool install_lobby_query_hook(const cesium_safe::il2cpp_tbl& t)
{
    log_line("[steamhack] 阶段5: 安装 LobbyQuery.RequestAsync 兜底"
             "(退房/解散/被踢时 await 之后的代码不再被 NRE 跳过) ...");

    // ---- 依赖导出: 缺任何一个都做不了, 直接放弃该 hook(游戏行为与原版一致) ----
    struct { const char* n; const void* p; } need[] = {
        { "domain_get",                 (const void*)t.domain_get },
        { "domain_get_assemblies",      (const void*)t.domain_get_assemblies },
        { "assembly_get_image",         (const void*)t.assembly_get_image },
        { "image_get_name",             (const void*)t.image_get_name },
        { "class_from_name",            (const void*)t.class_from_name },
        { "class_get_method_from_name", (const void*)t.class_get_method_from_name },
        { "method_get_return_type",     (const void*)t.method_get_return_type },
        { "type_get_object",            (const void*)t.type_get_object },
        { "class_from_system_type",     (const void*)t.class_from_system_type },
        { "object_new",                 (const void*)t.object_new },
        { "array_new",                  (const void*)t.array_new },
        { "runtime_invoke",             (const void*)t.runtime_invoke },
    };
    std::string missing;
    for (auto& n : need) if (!n.p) { if (!missing.empty()) missing += ", "; missing += n.n; }
    if (!missing.empty())
    {
        log_line("[steamhack] 阶段5: IL2CPP 导出缺失(" + missing + ") —— 不挂钩(fail-safe)");
        return false;
    }

    // ---- 1) 解析 Steamworks.Data.LobbyQuery ----
    std::string lq_image;
    std::string lq_ns;
    void* lq_cls = resolve_steamworks_class(t, "阶段5", "LobbyQuery", &lq_image, &lq_ns);
    if (!lq_cls) return false;
    const bool lq_is_vt = t.class_is_valuetype ? t.class_is_valuetype(lq_cls) : false;
    log_line("[steamhack] 阶段5: LobbyQuery 采用 image=" + lq_image + " ns=\"" + lq_ns + "\" 值类型=" +
             (lq_is_vt ? "是(struct —— 与反编译里 `(ref lobbyList)` 的写法一致)"
                       : "否(class —— 反编译里的 `(ref ...)` 也可作用于类, 不影响本 hook)"));

    // ---- 2) 解析 RequestAsync(0 参数)并取 methodPointer ----
    void* mi = t.class_get_method_from_name(lq_cls, "RequestAsync", 0);
    if (!mi)
    {
        log_line("[steamhack] 阶段5: 未命中 LobbyQuery.RequestAsync(0 参数) —— 不挂钩(fail-safe)");
        dump_class_methods(t, lq_cls, "阶段5.LobbyQuery");
        return false;
    }
    void* target = method_pointer_of(mi);
    const uint32_t pc = t.method_get_param_count ? t.method_get_param_count(mi) : 0;
    log_line("[steamhack] 阶段5: 命中 LobbyQuery.RequestAsync: image=" + lq_image + " ns=\"" + lq_ns +
             "\" MethodInfo=" + hex_str((uintptr_t)mi) + " methodPointer=" + hex_str((uintptr_t)target) +
             " (" + rva_of(target) + ") 参数个数=" + std::to_string(pc));
    if (!target)
    {
        log_line("[steamhack] 阶段5: RequestAsync 的 methodPointer 为空(可能不是 AOT 方法) —— "
                 "**只记日志, 不挂钩**(fail-safe)");
        return false;
    }

    // ---- 3) 返回类型 -> Task<Lobby[]> 的 Il2CppClass(需求: 日志打印解析到的返回类型全名) ----
    void* ret = t.method_get_return_type(mi);
    if (!ret)
    {
        log_line("[steamhack] 阶段5: method_get_return_type 返回空 —— 不挂钩(fail-safe)");
        return false;
    }
    const std::string ret_name =
        t.type_get_name ? cstr_or(t.type_get_name(ret), "(null)") : std::string("(type_get_name 缺失)");
    void* tobj = t.type_get_object(ret);
    void* task_cls = tobj ? t.class_from_system_type(tobj) : nullptr;
    log_line("[steamhack] 阶段5: 返回值解析 **返回类型全名 = " + ret_name + "**" +
             " Il2CppType=" + hex_str((uintptr_t)ret) + " System.Type=" + hex_str((uintptr_t)tobj) +
             " Il2CppClass=" + hex_str((uintptr_t)task_cls) + " 类名=" + class_name_of(t, task_cls));
    if (!task_cls)
    {
        log_line("[steamhack] 阶段5: class_from_system_type(" + ret_name + ") 返回空 —— 不挂钩(fail-safe)");
        return false;
    }
    {
        // 期望形如 System.Threading.Tasks.Task`1<Steamworks.Data.Lobby[]>。
        // 只做"看起来对"的提示(不作硬门槛 —— 硬门槛会因命名拼写差异误杀), 不符时明确写在日志里。
        const bool looks_task = (ret_name.find("Task") != std::string::npos);
        const bool looks_lobby = (ret_name.find("Lobby") != std::string::npos);
        log_line("[steamhack] 阶段5: 返回类型自检 含\"Task\"=" + std::string(looks_task ? "是" : "否") +
                 " 含\"Lobby\"" + "=" + (looks_lobby ? "是" : "否") +
                 " (期望 System.Threading.Tasks.Task`1<Steamworks.Data.Lobby[]>)" +
                 (looks_task && looks_lobby ? " —— 与预期一致"
                                            : " —— **与预期不符, 仍按实际类型继续, 但请核对上面那行**"));
    }

    // ---- 4) 解析 Steamworks.Data.Lobby(空数组的元素类型) ----
    std::string lobby_image;
    std::string lobby_ns;
    void* lobby_cls = resolve_steamworks_class(t, "阶段5", "Lobby", &lobby_image, &lobby_ns);
    void* array_cls = nullptr;
    if (lobby_cls && t.array_class_get)
    {
        array_cls = t.array_class_get(lobby_cls, 1);
        log_line("[steamhack] 阶段5: Lobby 采用 image=" + lobby_image + " ns=\"" + lobby_ns +
                 "\" Il2CppClass=" + hex_str((uintptr_t)lobby_cls) +
                 " -> Lobby[] Il2CppClass=" + hex_str((uintptr_t)array_cls));
    }
    if (!lobby_cls)
    {
        log_line("[steamhack] 阶段5: 未解析到 Steamworks.Data.Lobby —— 无法构造长度 0 的数组, "
                 "不挂钩(fail-safe; 不用 nullptr 硬来, 因为那会让探针判不出结果类型)");
        return false;
    }

    // ---- 5) Task<Lobby[]>::.ctor(1) + 参数类型判定 ----
    // 先按参数类型精确筛(Task<T> 上有多个 1 参数 ctor), 筛不到再退回名字+个数匹配。
    int ctor_candidates = 0;
    std::string ctor_pick_name;
    void* ctor = nullptr;
    if (array_cls)
    {
        ctor = find_task_ctor_with_arg(t, task_cls, array_cls, &ctor_pick_name, &ctor_candidates);
        log_line("[steamhack] 阶段5: 类上共枚举到 " + std::to_string(ctor_candidates) +
                 " 个 1 参数 .ctor(按参数类型筛选后命中=" + std::string(ctor ? "是" : "否") + ")");
    }
    if (!ctor)
    {
        ctor = t.class_get_method_from_name(task_cls, ".ctor", 1);
        log_line("[steamhack] 阶段5: 未按参数类型精确筛出 .ctor(1)(Lobby[]) —— 退回 "
                 "class_get_method_from_name(类, \".ctor\", 1); 该结果**仍要过探针校验**, "
                 "若拿到的不是 Task<TResult>(TResult), 会因 IsCompleted=false 被拦下(fail-safe)");
    }
    void* ctor_ptr = method_pointer_of(ctor);
    log_line("[steamhack] 阶段5: 采用的 类..ctor(1) 命中=" + std::string(ctor ? "是" : "否") +
             " MethodInfo=" + hex_str((uintptr_t)ctor) +
             " methodPointer=" + hex_str((uintptr_t)ctor_ptr));
    if (!ctor || !ctor_ptr)
    {
        log_line("[steamhack] 阶段5: 拿不到 类..ctor(1) 的 methodPointer —— 不挂钩(fail-safe)");
        return false;
    }

    bool param_is_vt = false;
    bool param_determined = false;
    std::string param_name = "(未知)";
    if (t.method_get_param_count && t.method_get_param)
    {
        const uint32_t cpc = t.method_get_param_count(ctor);
        log_line("[steamhack] 阶段5: ctor 参数个数=" + std::to_string(cpc));
        if (cpc == 1)
        {
            void* pt = t.method_get_param(ctor, 0);
            if (pt && t.type_get_name) param_name = cstr_or(t.type_get_name(pt), "(null)");
            if (pt && t.type_get_object && t.class_from_system_type && t.class_is_valuetype)
            {
                void* pto = t.type_get_object(pt);
                void* pcls = pto ? t.class_from_system_type(pto) : nullptr;
                if (pcls)
                {
                    param_is_vt = t.class_is_valuetype(pcls);
                    param_determined = true;
                    if (array_cls)
                        log_line("[steamhack] 阶段5: ctor 参数类 Il2CppClass=" + hex_str((uintptr_t)pcls) +
                                 " 与解析到的 Lobby[] 一致=" + ((pcls == array_cls) ? "是" : "否"));
                }
            }
        }
    }
    log_line("[steamhack] 阶段5: ctor 参数类型全名=" + param_name + " 值类型=" +
             (param_determined ? (param_is_vt ? "**是**(不符合预期)" : "否(引用类型, 符合预期)")
                               : "未知(无法判定, 不冒进)"));
    if (param_determined && param_is_vt)
    {
        // 需求: 参数被判定为值类型就**不挂钩** —— 给 Task<Lobby[]> 的 ctor 传"数组指针"是错的,
        // 用零值装箱更会给出一个错误的数组。宁可不挂钩, 也不要给游戏一个错误对象。
        log_line("[steamhack] 阶段5: ctor 参数被判定为**值类型**(不符合预期 —— 泛型参数应为引用类型 "
                 "Lobby[]) —— 不用零值装箱(那会给出错误的数组), **不挂钩**(fail-safe)");
        return false;
    }

    // ---- 6) 造长度 0 的 Lobby[] ----
    // il2cpp_array_new 的第 1 个参数是**元素类型**(Lobby), 不是数组类(那是 il2cpp_array_new_specific),
    // 所以传 lobby_cls, 得到的才是 Lobby[](传 array_cls 会得到 Lobby[][])。
    void* empty_array = nullptr;
    unsigned long an_seh = 0;
    pod_array_new_empty((void*)t.array_new, lobby_cls, &empty_array, &an_seh);
    if (an_seh)
        log_line("[steamhack] 阶段5: il2cpp_array_new(Lobby, 0) 触发 SEH " + hex_code_str(an_seh) +
                 " —— 已被 __try/__except 捕获");

    if (empty_array)
    {
        PodArrayLength al0;
        pod_array_length(&t, empty_array, &al0);
        log_line("[steamhack] 阶段5: 已构造空数组 Lobby[0] 对象=" + hex_str((uintptr_t)empty_array) +
                 " 元素个数=" + (al0.length >= 0 ? std::to_string(al0.length) : std::string("(读不到)")) +
                 " (来源: " + (al0.source == 1 ? "il2cpp_array_length"
                                               : al0.source == 2 ? "数组布局偏移" : "无") + ")");
    }
    else
    {
        // 降级: 传 nullptr。调用方有 `array != null` 判空, 语义安全 —— 但探针会因此拿到 null 结果
        // 而判定失败(见 probe_lobby_query_task), 所以最终**不会挂钩**; 这里明确记下走到了哪一步。
        log_line("[steamhack] 阶段5: **降级路径** —— 未能构造长度 0 的 Lobby[](il2cpp_array_new 调用"
                 "失败/返回空)。按需求本可退化为给 ctor 传 nullptr(调用方有 `array != null` 判空, "
                 "语义安全), 但探针拿不到数组就无法校验结果类型, 因此预期会走到'探针失败 -> 不挂钩'。");
    }

    // ---- 7) direct 直调 ctor: 原生 ABI, 绕过 il2cpp_runtime_invoke ----
    PodLobbyQueryCtor dr;
    pod_call_lobby_query_ctor((void*)t.object_new, task_cls, ctor_ptr, ctor, empty_array, &dr);
    {
        std::string s = "[steamhack] 阶段5[direct]: 原生 ABI 直调 Task<Lobby[]>::.ctor(Lobby[]), "
                        "绕过 il2cpp_runtime_invoke: ";
        s += "ctor methodPointer=" + hex_str((uintptr_t)ctor_ptr);
        s += " 目标类=" + class_name_of(t, task_cls);
        s += " il2cpp_object_new=" + std::string(dr.alloc_ok ? "成功" : "失败");
        s += " 实参=" + (dr.arg_was_null
                             ? std::string("nullptr(降级路径: 没传空数组)")
                             : ("长度 0 的 Lobby[] 对象指针 " + hex_str((uintptr_t)empty_array)));
        if (dr.seh_code)
            s += " | 原生调用触发 SEH 异常 " + hex_code_str(dr.seh_code) +
                 " —— 已被 __try/__except 捕获, **构造失败**";
        else if (!dr.called)
            s += " | 未执行调用(缺 object_new / ctor methodPointer / 目标类)";
        else
            s += std::string(" | 原生调用完成, 返回对象=") +
                 (dr.object ? hex_str((uintptr_t)dr.object) : std::string("(空)"));
        log_line(s);
    }
    if (!dr.object)
    {
        log_line("[steamhack] 阶段5: 未能构造出 Task<Lobby[]> 对象 —— 不挂钩(fail-safe)");
        return false;
    }

    // ---- 8) 探针校验(安装期, 全程 SEH 兜底) ----
    int arr_len = -1;
    int arr_len_src = 0;
    if (!probe_lobby_query_task(t, dr.object, task_cls, array_cls, &arr_len, &arr_len_src))
    {
        log_line("[steamhack] 阶段5: 探针校验未通过 —— **不挂钩**"
                 "(宁可不挂钩, 也不给游戏一个错误的返回值)");
        return false;
    }

    // ---- 9) 记 GC root + 缓存(所有调用复用同一个对象) ----
    g_lq_task = dr.object;
    g_lq_empty_array = empty_array;
    g_lq_task_class = task_cls;
    g_lq_array_class = array_cls;

    unsigned long gh_seh = 0;
    uint32_t gh = 0;
    if (t.gchandle_new)
    {
        pod_gchandle_new((void*)t.gchandle_new, dr.object, &gh, &gh_seh);
        if (gh_seh)
            log_line("[steamhack] 阶段5[GC root]: il2cpp_gchandle_new 触发 SEH " + hex_code_str(gh_seh) +
                     " —— 已被 __try/__except 捕获, 本次仅靠模块级全局变量作根");
        else
            g_lq_task_gchandle = gh;
    }
    log_line("[steamhack] 阶段5[GC root]: 预建好的 Task 持于模块级全局 g_lq_task=" +
             hex_str((uintptr_t)g_lq_task) + " / g_lq_empty_array=" + hex_str((uintptr_t)g_lq_empty_array) +
             " (静态存储期, Boehm 会扫描已加载模块的可写数据段)" +
             (g_lq_task_gchandle ? (" + **显式** il2cpp_gchandle_new 句柄=" +
                                    std::to_string(g_lq_task_gchandle) + " (pinned=false)")
                                 : std::string(" ; il2cpp_gchandle_new ") +
                                       (t.gchandle_new ? "返回 0(未成功)" : "导出缺失") +
                                       " —— 只靠全局变量, 如实标注"));
    log_line("[steamhack] 阶段5: 该 Task **只构造一次, 所有 RequestAsync 调用复用同一个实例** "
             "(Task<T> 不可变, get_Result() 恒返回同一个长度 0 的数组)");

    // ---- 10) 挂钩 ----
    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_LobbyQueryRequestAsync, (LPVOID*)&g_real_lq_request);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHook(LobbyQuery.RequestAsync) 失败: " + std::to_string((int)st) +
                 " (target=" + hex_str((uintptr_t)target) + ") —— 本次不挂钩(fail-safe)");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(LobbyQuery.RequestAsync) 失败: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_lq_target = target;
    g_lq_method = mi;
    g_lq_hooked = true;
    log_line("[steamhack] LobbyQuery.RequestAsync inline hook 已启用 "
             "(恒返回预建好的 Task<Lobby[]>, 结果 = 长度 0 的 Lobby[])");
    return true;
}

// 通用辅助(定义在下面, 两个 hook 处理器都要用)
void* make_default_completed_task(void* hooked_mi);

void* Hook_CreateLobbyAsync(int32_t max_members)
{
    // 只在首次拦截时记录入参(原子计数, 不刷屏)
    if (g_cl_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] CreateLobbyAsync 首次被拦截: maxMembers=" + std::to_string(max_members) +
                 " -> 返回已完成的空 Task(不执行真实 Steam 大厅逻辑, 游戏侧 LobbyId 保持 0)");
    return make_default_completed_task(g_cl_method);
}

void* Hook_JoinLobbyAsync(void* steam_id_ref)
{
    if (g_jl_hits.fetch_add(1, std::memory_order_relaxed) == 0)
    {
        // **故意不解引用**: SteamId 是值类型。IL2CPP 生成的 AOT C++ 通常把值类型参数按"指向该值的
        // 指针"传递, 但本模块无法在运行期确认这一点 —— 万一它其实是按值放在寄存器里, 解引用就是
        // 直接访问违例(崩游戏)。这个参数对本 hook 的逻辑毫无作用(一律返回空 Task), 所以只记原始
        // 寄存器值: 宁可少一条信息, 不可多一个崩溃点。(对比 CreateLobbyAsync 的 int32 参数:
        // 基元类型按值传, 所以那边 maxMembers 的日志是可靠的。)
        const uintptr_t raw = reinterpret_cast<uintptr_t>(steam_id_ref);
        log_line("[steamhack] JoinLobbyAsync 首次被拦截: steamId 原始寄存器值=" +
                 (steam_id_ref ? hex_str(raw) : std::string("(null)")) +
                 " (值类型; 按 IL2CPP AOT 约定应是指向 SteamId 的指针, 本模块不解引用)"
                 " -> 返回已完成的空 Task(不执行真实 Steam 大厅逻辑)");
    }
    return make_default_completed_task(g_jl_method);
}

// 传入被挂钩方法的 MethodInfo*(= 任务描述里的 hookedMi), 返回"已完成且结果为 null"的默认返回值。
// 返回值类型由该方法**自己**推导(resolve_task_factory), 所以对 Task<Lobby?> 以外的返回类型
// 也自动适配 —— 不硬编码 Lobby。
// runtime_invoke 失败时记日志并返回 nullptr(不让托管异常穿到游戏里)。
void* make_default_completed_task(void* hooked_mi)
{
    TaskFactory* f = nullptr;
    if (hooked_mi && hooked_mi == g_cl_method) f = &g_factory_cl;
    else if (hooked_mi && hooked_mi == g_jl_method) f = &g_factory_jl;

    if (!f || !g_t_ready)
    {
        // 理论到不了这里(hook 只在安装成功后才存在)。防御性记录一次。
        static std::atomic<long> once{ 0 };
        if (once.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] make_default_completed_task: 未登记的 MethodInfo 或函数表未就绪 -> 返回 nullptr");
        return nullptr;
    }

    if (!f->resolved)   // 防御: 正常情况下安装期已解析 + 探测成功
        resolve_task_factory(g_t, *f, hooked_mi, f->label);

    if (!f->ok || !f->factory)
    {
        if (f->failures.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] " + std::string(f->label) +
                     ": 默认返回值工厂不可用 -> 本次返回 nullptr(游戏侧会收到空 Task 而 NRE)");
        return nullptr;
    }

    // ---- 按安装期探测成功的路径造返回值 ----
    // 方案C(direct): 原生 ABI 直调 ctor 的 methodPointer, 绕过 il2cpp_runtime_invoke 的编组。
    // 其余情况(invoke): 仍然走 runtime_invoke(含 FromResult / CompletedTask / 回退)。
    void* exc = nullptr;
    void* r = nullptr;

    if (f->ctor_path == CPATH_DIRECT)
    {
        PodDirectCtor dr;
        r = run_direct_ctor(g_t, *f, &dr);
        // 运行期只记首次详细日志(不刷屏); 失败也只在首次记录, 避免每次点创建房间都刷一条。
        if (f->runtime_logged.fetch_add(1, std::memory_order_relaxed) == 0)
            log_direct_attempt(g_t, *f, dr, r, "运行期首次");
        if (!r)
        {
            if (f->failures.fetch_add(1, std::memory_order_relaxed) == 0)
                log_line("[steamhack] " + std::string(f->label) +
                         "[direct]: 原生 ABI 直调造默认返回值失败(见上一条) -> 本次返回 nullptr"
                         "(不让异常穿到游戏里)");
            return nullptr;
        }
        return r;
    }

    r = invoke_task_factory(g_t, *f, &exc);
    if (!r)
    {
        if (f->failures.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] " + std::string(f->label) + "[invoke]: runtime_invoke 造默认返回值失败" +
                     (exc ? "(托管异常)" : "(返回空)") + " -> 本次返回 nullptr(不让异常穿到游戏里)");
        return nullptr;
    }
    return r;
}

} // namespace

// ---------- 对外接口 ----------

bool steamhack_install(const cesium_safe::il2cpp_tbl& t,
                       bool bypass_awake,
                       bool bypass_restart_check,
                       bool bypass_matchmaking,
                       bool bypass_lobby_hasvalue,
                       const std::string& task_ctor_mode,
                       bool bypass_lobby_methods,
                       bool bypass_lobby_query)
{
    if (g_installed) return g_hook_count > 0;
    g_installed = true;

    // 两个 hook 处理器要用函数表, 这里保存副本(见 g_t 的说明)。
    g_t = t;
    g_t_ready = true;

    // ---- 方案C 模式解析(必须在阶段2 解析工厂之前定下来) ----
    {
        bool unknown = false;
        g_ctor_mode = parse_ctor_mode(task_ctor_mode, &unknown);
        g_ctor_mode_name = ctor_mode_name(g_ctor_mode);
        if (unknown)
            log_line("[steamhack] steamBypassTaskCtorMode=\"" + task_ctor_mode +
                     "\" 不是合法取值(auto/direct/invoke) —— 按默认 \"auto\" 处理");
        else
            log_line("[steamhack] steamBypassTaskCtorMode=\"" + std::string(g_ctor_mode_name) + "\"");
    }

    log_line("[steamhack] 安装 Steam 绕过: 阶段1 自杀门(Awake->no-op) + 阶段2 大厅匹配(返回已完成的空 Task)"
             " + 阶段3 Nullable<Lobby>.get_HasValue->false(实测作废, 保留但无效)"
             " + 方案C Task<T>..ctor(T) 原生 ABI 直调"
             " + 方案B Lobby.SetPublic/SetJoinable/Id 无害化"
             " + 阶段5 LobbyQuery.RequestAsync->结果为空 Lobby[] 的已完成 Task");

    int hooks = 0;
    int stage1 = 0;
    int stage2 = 0;
    int stage1_expected = 0;
    int stage2_expected = 0;
    int lobby_installed = 0;
    int lobby_expected = 0;
    int stage5 = 0;
    int stage5_expected = 0;

    // ---- 阶段1: Steam 自杀门(受 steamBypassEnabled / steamBypassRestartCheck 控制) ----
    if (bypass_awake)
    {
        stage1_expected++;
        if (install_awake(t)) { hooks++; stage1++; }
    }
    else
    {
        log_line("[steamhack] steamBypassEnabled=false, 跳过 SteamManager.Awake no-op(非 Steam 启动会退出游戏)");
    }

    if (bypass_awake && bypass_restart_check)
    {
        stage1_expected++;
        if (install_restart_check()) { hooks++; stage1++; }
    }
    else if (bypass_awake)
    {
        log_line("[steamhack] steamBypassRestartCheck=false, 跳过 RestartAppIfNecessary 保险");
    }
    else
    {
        log_line("[steamhack] steamBypassEnabled=false, 跳过 RestartAppIfNecessary 保险(仅在该开关为 true 时有意义)");
    }

    // ---- 阶段2: Steam 大厅匹配(只受 steamBypassMatchmaking 控制) ----
    // 修"Steam 全关时点创建房间/加入房间毫无反应": 真实 CreateLobbyAsync 会抛 NRE,
    // 这个 async 异常把建房流程吃掉。这里改为返回"已完成且结果为 null"的 Task<Lobby?>。
    if (bypass_matchmaking)
    {
        stage2_expected += 2;
        log_line("[steamhack] 阶段2: 安装大厅匹配绕过 (CreateLobbyAsync/JoinLobbyAsync -> 已完成的空 Task) ...");
        if (install_lobby_hook(t, "CreateLobbyAsync", 1, (void*)&Hook_CreateLobbyAsync,
                               (LPVOID*)&g_real_create_lobby, &g_cl_target, &g_cl_method, g_factory_cl))
        {
            hooks++; stage2++; g_cl_hooked = true;
        }
        if (install_lobby_hook(t, "JoinLobbyAsync", 1, (void*)&Hook_JoinLobbyAsync,
                               (LPVOID*)&g_real_join_lobby, &g_jl_target, &g_jl_method, g_factory_jl))
        {
            hooks++; stage2++; g_jl_hooked = true;
        }

        // ---- 阶段3: Nullable<Lobby>.get_HasValue -> 恒 false(**已实测作废, 保留但无效**) ----
        // 实机结论: 这个 hook 的"首次被拦截"那行**从未出现** —— HybridCLR 解释器把
        // Nullable<T>.HasValue 当**内建指令**内联处理, 不走 MethodInfo->methodPointer。
        // 所以 hook 留着无害, 但它**不是解法**; 真正的解法是方案C + 方案B。这里明确记一行。
        log_line("[steamhack] 阶段3 说明(实测): Nullable<Lobby>.get_HasValue 这条路线**已作废** —— "
                 "HybridCLR 解释器把 Nullable.HasValue 当内建指令内联, 该 hook 在实机日志里"
                 "从未出现“首次被拦截”。hook 保留(无害, 万一将来真走 AOT 调用还能兜住), "
                 "但它**不是**解法: 解法是方案C(steamBypassTaskCtorMode) + 方案B(steamBypassLobbyMethods)。");
        if (bypass_lobby_hasvalue)
        {
            stage2_expected++;
            log_line("[steamhack] 阶段3: 安装 Nullable<Lobby>.get_HasValue 挂钩(恒返回 false; "
                     "预期不被命中, 仅作无害保留) ...");
            TaskFactory* src = nullptr;
            if (g_cl_hooked)      src = &g_factory_cl;
            else if (g_jl_hooked) src = &g_factory_jl;

            if (!src)
            {
                log_line("[steamhack] 阶段3: 阶段2 两个大厅 hook 都没装上, 没有可复用的 Nullable 类 "
                         "—— 跳过 get_HasValue 挂钩(fail-safe, 游戏行为保持当前状态)");
            }
            else if (install_hasvalue_hook(t, *src))
            {
                hooks++; stage2++;
            }
            else
            {
                log_line("[steamhack] 阶段3: get_HasValue 未挂上 —— 不影响方案C/方案B 生效");
            }
        }
        else
        {
            log_line("[steamhack] steamBypassLobbyHasValue=false, 跳过 Nullable<Lobby>.get_HasValue 挂钩"
                     "(该 hook 本就不被命中, 跳过无副作用)");
        }

        // ---- 方案B(安全网): Lobby.SetPublic / SetJoinable / Id 无害化 ----
        // 必须放在阶段2 之后(与阶段3 同样的上下文依赖: 阶段2 没装就没有空 Task 可返回,
        // 游戏根本走不到 Lobby 那几个成员)。预期 2 或 3 个(Id 是属性才 +1)。
        if (bypass_lobby_methods)
        {
            log_line("[steamhack] 方案B: 安装 Lobby.SetPublic/SetJoinable/Id 无害化挂钩"
                     "(安全网: hasValue 若仍为 true 时兜住建房流程) ...");
            int exp = 0;
            const int got = install_lobby_methods(t, &exp);
            lobby_installed = got;
            lobby_expected = exp;
            hooks += got;
        }
        else
        {
            log_line("[steamhack] steamBypassLobbyMethods=false, 跳过方案B"
                     "(Lobby.SetPublic/SetJoinable/Id 不挂钩 —— hasValue 若仍为 true, "
                     "点创建房间仍会在 SetPublic 处抛 NRE)");
        }
    }
    else
    {
        log_line("[steamhack] steamBypassMatchmaking=false, 跳过大厅匹配绕过 + 阶段3 的 get_HasValue 挂钩 + "
                 "方案B 的 Lobby 方法无害化(创建/加入房间仍会因 async 异常而无反应)");
    }

    // ---- 阶段5: LobbyQuery.RequestAsync -> 结果为空 Lobby[] 的已完成 Task ----
    // 修"退房/解散/被踢后本地房间状态不清、被踢提示不弹、不返回房间列表页":
    // 实机实测 6 条 NRE 抛在 `await ((LobbyQuery)(ref lobbyList)).RequestAsync()` 上,
    // 让 RoomLogic.cs:398/401 与 :567-573 全部被跳过(详见文件下半部分 "阶段5" 一节)。
    // **只受 steamBypassLobbyQuery 控制**, 与阶段1/阶段2/阶段3/方案B 互不影响
    // (它不依赖阶段2 的动态返回值工厂, 自己解析 LobbyQuery + Lobby + Task<Lobby[]> 并单独造 Task)。
    if (bypass_lobby_query)
    {
        stage5_expected = 1;
        if (install_lobby_query_hook(t)) { hooks++; stage5++; g_lq_hooked = true; }
    }
    else
    {
        log_line("[steamhack] steamBypassLobbyQuery=false, 跳过阶段5(LobbyQuery.RequestAsync 不挂钩 —— "
                 "退房/解散/被踢时 await 之后的 UpdateRoomByExit/ClearRoomInfo 仍会被 NRE 跳过)");
    }

    g_hook_count = hooks;
    const int expected = stage1_expected + stage2_expected + lobby_expected + stage5_expected;
    if (hooks == 0)
        log_line("[steamhack] 未安装任何 hook —— 游戏行为与原版一致");
    else
        log_line("[steamhack] 完成: 已启用 " + std::to_string(hooks) + "/" + std::to_string(expected) +
                 " 个 hook (阶段1 自杀门 " + std::to_string(stage1) + "/" + std::to_string(stage1_expected) +
                 ", 阶段2+3 大厅匹配 " + std::to_string(stage2) + "/" + std::to_string(stage2_expected) +
                 ", 阶段5 LobbyQuery.RequestAsync " + std::to_string(stage5) + "/" +
                 std::to_string(stage5_expected) +
                 ", 方案B Lobby 方法 " + std::to_string(lobby_installed) + "/" + std::to_string(lobby_expected) +
                 ")。**定版配置(方案B 默认关)预期 = 6/6**: 阶段1 2 个 + 阶段2 2 个 + 阶段3 1 个 + 阶段5 1 个; "
                 "方案B 打开时总数变 8 或 9(Lobby.get_Id 只有 Id 是属性时才多 1 个)");
    return hooks > 0;
}

int steamhack_hook_count()
{
    return g_hook_count;
}

void steamhack_uninstall()
{
    if (!g_mh_ready) return;

    if (g_awake_hooked && g_awake_target)
    {
        MH_DisableHook(g_awake_target);
        MH_RemoveHook(g_awake_target);
        g_awake_hooked = false;
        g_awake_target = nullptr;
    }
    if (g_restart_hooked && g_restart_target)
    {
        MH_DisableHook(g_restart_target);
        MH_RemoveHook(g_restart_target);
        g_restart_hooked = false;
        g_restart_target = nullptr;
    }
    // 阶段2 的两个大厅 hook
    if (g_cl_hooked && g_cl_target)
    {
        MH_DisableHook(g_cl_target);
        MH_RemoveHook(g_cl_target);
        g_cl_hooked = false;
        g_cl_target = nullptr;
    }
    if (g_jl_hooked && g_jl_target)
    {
        MH_DisableHook(g_jl_target);
        MH_RemoveHook(g_jl_target);
        g_jl_hooked = false;
        g_jl_target = nullptr;
    }
    // 阶段3 的 Nullable<Lobby>.get_HasValue hook(实测不被命中, 但装了就要摘干净)
    if (g_hv_hooked && g_hv_target)
    {
        MH_DisableHook(g_hv_target);
        MH_RemoveHook(g_hv_target);
        g_hv_hooked = false;
        g_hv_target = nullptr;
        g_hv_method = nullptr;
    }

    // 方案B 的三个 hook(Lobby.SetPublic / Lobby.SetJoinable / Lobby.get_Id)
    if (g_lsp_hooked && g_lsp_target)
    {
        MH_DisableHook(g_lsp_target);
        MH_RemoveHook(g_lsp_target);
        g_lsp_hooked = false;
        g_lsp_target = nullptr;
        g_real_lsp = nullptr;
    }
    if (g_lsj_hooked && g_lsj_target)
    {
        MH_DisableHook(g_lsj_target);
        MH_RemoveHook(g_lsj_target);
        g_lsj_hooked = false;
        g_lsj_target = nullptr;
        g_real_lsj = nullptr;
    }
    if (g_lid_hooked && g_lid_target)
    {
        MH_DisableHook(g_lid_target);
        MH_RemoveHook(g_lid_target);
        g_lid_hooked = false;
        g_lid_target = nullptr;
        g_lid_method = nullptr;
        g_real_lid = nullptr;
    }

    // 阶段5 的 LobbyQuery.RequestAsync hook
    if (g_lq_hooked && g_lq_target)
    {
        MH_DisableHook(g_lq_target);
        MH_RemoveHook(g_lq_target);
        g_lq_hooked = false;
        g_lq_target = nullptr;
        g_lq_method = nullptr;
        g_real_lq_request = nullptr;
        // 注意: 预建好的 Task(g_lq_task / g_lq_empty_array)与它的 gchandle **不释放** ——
        // 本模块没有 il2cpp_gchandle_free, 而且此时进程正在退出; 留着比"摘掉 hook 后仍有
        // 一个悬空句柄"更安全。这里只是把引用留作日志/诊断用。
    }

    // 注意: 绝不调用 MH_Uninitialize() —— MinHook 由 speedhack.cpp 拥有(见 steamhack.h),
    // 卸载它会把 speedhack 的 hook 一起废掉。这里只摘掉自己创建的那几个。
    g_mh_ready = false;
    g_installed = false;
    g_hook_count = 0;
}
