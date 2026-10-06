// Internal implementation fragment. Included by steamhack.cpp in source order.
namespace
{

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
                         " ns=\"" + h.ns + "\" MethodInfo=" + cesium::steam::diagnostics::hex((uintptr_t)m) +
                         " methodPointer=" + cesium::steam::diagnostics::hex(*reinterpret_cast<uintptr_t*>(m)) +
                         " (" + cesium::steam::diagnostics::gameassembly_rva(*reinterpret_cast<void**>(m)) + ")");
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
                 " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ")");
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
    log_line("[steamhack] SteamAPI_RestartAppIfNecessary = " + cesium::steam::diagnostics::hex((uintptr_t)target) +
             " (" + cesium::steam::diagnostics::module_offset(steam, target) + " steam_api64.dll)");

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
