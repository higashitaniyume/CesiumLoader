// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- CreateLobbyAsync / JoinLobbyAsync 处理器 + 通用默认返回值 ----------

// 通用辅助(定义在下面, 两个 hook 处理器都要用)
void* make_default_completed_task(void* hooked_mi);

void* Hook_CreateLobbyAsync(int32_t max_members)
{
    // 只在首次拦截时记录入参(原子计数, 不刷屏)
    if (g_cl_hits.fetch_add(1, std::memory_order_relaxed) == 0)
        log_line("[steamhack] CreateLobbyAsync intercepted on first hit: maxMembers=" + std::to_string(max_members) +
                 " -> returning a completed empty Task (no real Steam lobby logic; game keeps LobbyId 0)");
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
        log_line("[steamhack] JoinLobbyAsync intercepted on first hit: raw steamId register value=" +
                 (steam_id_ref ? cesium::steam::diagnostics::hex(raw) : std::string("(null)")) +
                 " (value type; per the IL2CPP AOT convention this is a pointer to SteamId; this module does not dereference it)"
                 " -> returning a completed empty Task (no real Steam lobby logic)");
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
            log_line("[steamhack] make_default_completed_task: unregistered MethodInfo or function table not ready -> returning nullptr");
        return nullptr;
    }

    if (!f->resolved)   // 防御: 正常情况下安装期已解析 + 探测成功
        resolve_task_factory(g_t, *f, hooked_mi, f->label);

    if (!f->ok || !f->factory)
    {
        if (f->failures.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] " + std::string(f->label) +
                     ": default-return factory unavailable -> returning nullptr this time (the game gets a null Task and NREs)");
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
            log_direct_attempt(g_t, *f, dr, r, "first at runtime");
        if (!r)
        {
            if (f->failures.fetch_add(1, std::memory_order_relaxed) == 0)
                log_line("[steamhack] " + std::string(f->label) +
                         "[direct]: native ABI default-return construction failed (see the previous line) -> returning nullptr this time"
                         " (no exception leaks into the game)");
            return nullptr;
        }
        return r;
    }

    r = invoke_task_factory(g_t, *f, &exc);
    if (!r)
    {
        if (f->failures.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] " + std::string(f->label) + "[invoke]: runtime_invoke failed to build a default return value" +
                     (exc ? "(managed exception)" : "(returned null)") + " -> returning nullptr this time (no exception leaks into the game)");
        return nullptr;
    }
    return r;
}

} // namespace
