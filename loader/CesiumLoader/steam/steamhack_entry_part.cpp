// Internal implementation fragment. Included by steamhack.cpp in source order.
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
                     "\" is not a valid value (auto/direct/invoke) - using the default \"auto\"");
        else
            log_line("[steamhack] steamBypassTaskCtorMode=\"" + std::string(g_ctor_mode_name) + "\"");
    }

    log_line("[steamhack] installing Steam bypass: stage 1 self-kill gate (Awake->no-op) + stage 2 lobby matchmaking (returns a completed empty Task)"
             " + stage 3 Nullable<Lobby>.get_HasValue->false (dead in practice, kept but ineffective)"
             " + plan C Task<T>..ctor(T) native ABI direct call"
             " + plan B Lobby.SetPublic/SetJoinable/Id neutralized"
             " + stage 5 LobbyQuery.RequestAsync -> completed Task with an empty Lobby[] result");

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
        log_line("[steamhack] steamBypassEnabled=false, skipping SteamManager.Awake no-op (non-Steam start would exit the game)");
    }

    if (bypass_awake && bypass_restart_check)
    {
        stage1_expected++;
        if (install_restart_check()) { hooks++; stage1++; }
    }
    else if (bypass_awake)
    {
        log_line("[steamhack] steamBypassRestartCheck=false, skipping the RestartAppIfNecessary safety net");
    }
    else
    {
        log_line("[steamhack] steamBypassEnabled=false, skipping the RestartAppIfNecessary safety net (only meaningful when this switch is true)");
    }

    // ---- 阶段2: Steam 大厅匹配(只受 steamBypassMatchmaking 控制) ----
    // 修"Steam 全关时点创建房间/加入房间毫无反应": 真实 CreateLobbyAsync 会抛 NRE,
    // 这个 async 异常把建房流程吃掉。这里改为返回"已完成且结果为 null"的 Task<Lobby?>。
    if (bypass_matchmaking)
    {
        stage2_expected += 2;
        log_line("[steamhack] stage 2: installing lobby matchmaking bypass (CreateLobbyAsync/JoinLobbyAsync -> completed empty Task) ...");
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
        log_line("[steamhack] stage 3 note (observed in game): the Nullable<Lobby>.get_HasValue route is **dead** - "
                 "the HybridCLR interpreter inlines Nullable.HasValue as a builtin, so in real sessions this hook"
                 "never showed \"first hit\". The hook is kept (harmless; it would still catch a future AOT call), "
                 "but it is **not** the fix: the fix is plan C (steamBypassTaskCtorMode) + plan B (steamBypassLobbyMethods).");
        if (bypass_lobby_hasvalue)
        {
            stage2_expected++;
            log_line("[steamhack] stage 3: installing the Nullable<Lobby>.get_HasValue hook (always false; "
                     "expected not to be hit, kept as harmless) ...");
            TaskFactory* src = nullptr;
            if (g_cl_hooked)      src = &g_factory_cl;
            else if (g_jl_hooked) src = &g_factory_jl;

            if (!src)
            {
                log_line("[steamhack] stage 3: neither stage-2 lobby hook installed, no reusable Nullable class "
                         "- skipping the get_HasValue hook (fail-safe, behavior unchanged)");
            }
            else if (install_hasvalue_hook(t, *src))
            {
                hooks++; stage2++;
            }
            else
            {
                log_line("[steamhack] stage 3: get_HasValue not hooked - plan C/plan B still work");
            }
        }
        else
        {
            log_line("[steamhack] steamBypassLobbyHasValue=false, skipping the Nullable<Lobby>.get_HasValue hook"
                     " (that hook is never hit anyway; skipping is side-effect free)");
        }

        // ---- 方案B(安全网): Lobby.SetPublic / SetJoinable / Id 无害化 ----
        // 必须放在阶段2 之后(与阶段3 同样的上下文依赖: 阶段2 没装就没有空 Task 可返回,
        // 游戏根本走不到 Lobby 那几个成员)。预期 2 或 3 个(Id 是属性才 +1)。
        if (bypass_lobby_methods)
        {
            log_line("[steamhack] plan B: installing harmless hooks for Lobby.SetPublic/SetJoinable/Id"
                     " (safety net: catches the create-room flow if hasValue is still true) ...");
            int exp = 0;
            const int got = install_lobby_methods(t, &exp);
            lobby_installed = got;
            lobby_expected = exp;
            hooks += got;
        }
        else
        {
            log_line("[steamhack] steamBypassLobbyMethods=false, skipping plan B"
                     " (Lobby.SetPublic/SetJoinable/Id not hooked - if hasValue is still true, "
                     "clicking create-room would still NRE at SetPublic)");
        }
    }
    else
    {
        log_line("[steamhack] steamBypassMatchmaking=false, skipping lobby matchmaking bypass + stage 3 get_HasValue hook + "
                 "plan B Lobby method neutralization (create/join room still silently fails due to the async exception)");
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
        log_line("[steamhack] steamBypassLobbyQuery=false, skipping stage 5 (LobbyQuery.RequestAsync not hooked - "
                 "on leave/disband/kick, UpdateRoomByExit/ClearRoomInfo after await would still be skipped by an NRE)");
    }

    g_hook_count = hooks;
    const int expected = stage1_expected + stage2_expected + lobby_expected + stage5_expected;
    if (hooks == 0)
        log_line("[steamhack] no hook installed - game behaves as vanilla");
    else
        log_line("[steamhack] done: enabled " + std::to_string(hooks) + "/" + std::to_string(expected) +
                 " hooks (stage 1 self-kill gate " + std::to_string(stage1) + "/" + std::to_string(stage1_expected) +
                 ", stage 2+3 lobby matchmaking " + std::to_string(stage2) + "/" + std::to_string(stage2_expected) +
                 ", stage 5 LobbyQuery.RequestAsync " + std::to_string(stage5) + "/" +
                 std::to_string(stage5_expected) +
                 ", plan B Lobby methods " + std::to_string(lobby_installed) + "/" + std::to_string(lobby_expected) +
                 "). **Release config (plan B off by default) expects 6/6**: stage 1 x2 + stage 2 x2 + stage 3 x1 + stage 5 x1; "
                 "with plan B on the total becomes 8 or 9 (one more only if Lobby.get_Id is a property)");
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
