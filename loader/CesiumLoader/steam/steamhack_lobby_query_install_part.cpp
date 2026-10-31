// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- 阶段5: 探针校验 + ctor 精确筛选 + 安装 ----------

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
        log_line("[steamhack] stage 5[probe]: il2cpp_runtime_invoke missing or task object is null - "
                 "skipping completion/result checks (accept on successful construction)");
        return true;
    }

    void* cls = t.object_get_class ? t.object_get_class(task) : nullptr;
    if (!cls) cls = task_cls;
    if (!cls)
    {
        log_line("[steamhack] stage 5[probe]: cannot determine the task object's class - skipping checks");
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
        log_line("[steamhack] stage 5[probe]: no usable IsCompleted getter - skipping the completion check");
    }
    else
    {
        void* exc = nullptr;
        void* boxed = nullptr;
        unsigned long seh = 0;
        pod_runtime_invoke((void*)t.runtime_invoke, get_completed, task, args, &boxed, &exc, &seh);
        if (seh || exc || !boxed)
        {
            log_line(std::string("[steamhack] stage 5[probe]: IsCompleted call failed") +
                     (seh ? ("(SEH " + hex_code_str(seh) + ")") : exc ? "(managed exception)" : "(returned null)") +
                     " - validation failed, not hooked");
            return false;
        }
        const bool completed = boxed_byte(t, boxed) != 0;
        log_line("[steamhack] stage 5[probe]: IsCompleted=" + std::string(completed ? "true" : "false"));
        if (!completed)
        {
            log_line("[steamhack] stage 5[probe]: task is not RanToCompletion - not hooked"
                     " (otherwise the game's await would hang forever)");
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
        log_line("[steamhack] stage 5[probe]: no get_Result() on the task class - cannot validate the result, treating as failure, not hooked");
        return false;
    }
    void* rexc = nullptr;
    void* rv = nullptr;
    unsigned long rseh = 0;
    pod_runtime_invoke((void*)t.runtime_invoke, get_result, task, args, &rv, &rexc, &rseh);
    if (rseh || rexc)
    {
        log_line(std::string("[steamhack] stage 5[probe]: get_Result() call failed") +
                 (rseh ? ("(SEH " + hex_code_str(rseh) + ")") : "(managed exception)") + " - not hooked");
        return false;
    }
    if (!rv)
    {
        // 语义上 null 也能让游戏跳过(array != null 为 false), 但我们的目标是"长度 0 的数组";
        // 拿到 null 说明 ctor 没把结果存进去(或实参被降级成 nullptr) —— 按"宁可不挂钩"处理。
        log_line("[steamhack] stage 5[probe]: get_Result() returned null (not an empty Lobby[]) - "
                 "validation failed, not hooked (ctor did not store the array in the result)");
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
        log_line("[steamhack] stage 5[probe]: get_Result() non-null, object=" + cesium::steam::diagnostics::hex((uintptr_t)rv) +
                 " class=" + class_name_of(t, rcls) + " matchesLobbyArrayIl2CppClass=" +
                 (array_cls ? (class_is_array ? "yes" : "no") : "(Lobby[] unresolved, skipping this check)"));
    }

    // ---- 3) 元素个数必须是 0 ----
    PodArrayLength al;
    pod_array_length(&t, rv, &al);
    if (al.seh_code)
    {
        log_line("[steamhack] stage 5[probe]: reading array length raised SEH " + hex_code_str(al.seh_code) +
                 " - captured by __try/__except; length unreadable, accepted on non-NULL only");
    }
    else if (al.length >= 0)
    {
        log_line("[steamhack] stage 5[probe]: result array length=" + std::to_string(al.length) + " (source: " +
                 (al.source == 1 ? "il2cpp_array_length (authoritative export)"
                                 : "array layout offset (il2cpp_array_object_header_size - 8)") + ")");
    }
    else
    {
        log_line("[steamhack] stage 5[probe]: could not read the array length (il2cpp_array_length and "
                 "il2cpp_array_object_header_size unavailable) - validating non-NULL only");
    }
    if (out_len) *out_len = al.length;
    if (out_len_src) *out_len_src = al.source;

    if (al.length >= 0 && al.length != 0)
    {
        log_line("[steamhack] stage 5[probe]: result array length is not 0 - validation failed, not hooked"
                 " (otherwise the game would Leave() a real lobby with this array)");
        return false;
    }
    if (class_checked && array_cls && !class_is_array)
    {
        log_line("[steamhack] stage 5[probe]: result object class is not Lobby[] - validation failed, not hooked");
        return false;
    }

    log_line("[steamhack] stage 5[probe]: validation passed - completed + get_Result() is an empty Lobby[]"
             " (the game's array != null && array.Length > 0 is false, so Steam lobby cleanup is skipped)");
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
    if (out_param_name) *out_param_name = "(unknown)";
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
            log_line("[steamhack] stage 5: **exact param-type hit** for class..ctor(1): param full type name=" + pn +
                     " Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)pcls) + " (matches Lobby[]) MethodInfo=" +
                     cesium::steam::diagnostics::hex((uintptr_t)m));
            break;
        }
        log_line("[steamhack] stage 5: found a 1-param .ctor but the **param type does not match** - param full type name=" + pn +
                 " Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)pcls) + " (expected Lobby[]=" +
                 cesium::steam::diagnostics::hex((uintptr_t)expected_arg_cls) + ")");
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
    log_line("[steamhack] stage 5: installing the LobbyQuery.RequestAsync fallback"
             " (after leave/disband/kick, code after await is no longer skipped by NREs) ...");

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
        log_line("[steamhack] stage 5: IL2CPP exports missing (" + missing + ") - not hooked (fail-safe)");
        return false;
    }

    // ---- 1) 解析 Steamworks.Data.LobbyQuery ----
    std::string lq_image;
    std::string lq_ns;
    void* lq_cls = resolve_steamworks_class(t, "stage 5", "LobbyQuery", &lq_image, &lq_ns);
    if (!lq_cls) return false;
    const bool lq_is_vt = t.class_is_valuetype ? t.class_is_valuetype(lq_cls) : false;
    log_line("[steamhack] stage 5: LobbyQuery uses image=" + lq_image + " ns=\"" + lq_ns + "\" isValueType=" +
             (lq_is_vt ? "yes (struct - consistent with `(ref lobbyList)` in the decompiled code)"
                       : "no (class - `(ref ...)` in the decompiled code also works on classes, does not affect this hook)"));

    // ---- 2) 解析 RequestAsync(0 参数)并取 methodPointer ----
    void* mi = t.class_get_method_from_name(lq_cls, "RequestAsync", 0);
    if (!mi)
    {
        log_line("[steamhack] stage 5: no hit for LobbyQuery.RequestAsync (0 params) - not hooked (fail-safe)");
        dump_class_methods(t, lq_cls, "stage5.LobbyQuery");
        return false;
    }
    void* target = method_pointer_of(mi);
    const uint32_t pc = t.method_get_param_count ? t.method_get_param_count(mi) : 0;
    log_line("[steamhack] stage 5: hit LobbyQuery.RequestAsync: image=" + lq_image + " ns=\"" + lq_ns +
             "\" MethodInfo=" + cesium::steam::diagnostics::hex((uintptr_t)mi) + " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)target) +
             " (" + cesium::steam::diagnostics::gameassembly_rva(target) + ") paramCount=" + std::to_string(pc));
    if (!target)
    {
        log_line("[steamhack] stage 5: RequestAsync methodPointer is null (possibly not an AOT method) - "
                 "**log only, no hook** (fail-safe)");
        return false;
    }

    // ---- 3) 返回类型 -> Task<Lobby[]> 的 Il2CppClass(需求: 日志打印解析到的返回类型全名) ----
    void* ret = t.method_get_return_type(mi);
    if (!ret)
    {
        log_line("[steamhack] stage 5: method_get_return_type returned null - not hooked (fail-safe)");
        return false;
    }
    const std::string ret_name =
        t.type_get_name ? cstr_or(t.type_get_name(ret), "(null)") : std::string("(type_get_name missing)");
    void* tobj = t.type_get_object(ret);
    void* task_cls = tobj ? t.class_from_system_type(tobj) : nullptr;
    log_line("[steamhack] stage 5: return value resolved, **full type name = " + ret_name + "**" +
             " Il2CppType=" + cesium::steam::diagnostics::hex((uintptr_t)ret) + " System.Type=" + cesium::steam::diagnostics::hex((uintptr_t)tobj) +
             " Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)task_cls) + " className=" + class_name_of(t, task_cls));
    if (!task_cls)
    {
        log_line("[steamhack] stage 5: class_from_system_type(" + ret_name + ") returned null - not hooked (fail-safe)");
        return false;
    }
    {
        // 期望形如 System.Threading.Tasks.Task`1<Steamworks.Data.Lobby[]>。
        // 只做"看起来对"的提示(不作硬门槛 —— 硬门槛会因命名拼写差异误杀), 不符时明确写在日志里。
        const bool looks_task = (ret_name.find("Task") != std::string::npos);
        const bool looks_lobby = (ret_name.find("Lobby") != std::string::npos);
        log_line("[steamhack] stage 5: return type self-check contains \"Task\"=" + std::string(looks_task ? "yes" : "no") +
                 " contains \"Lobby\"" + "=" + (looks_lobby ? "yes" : "no") +
                 " (expected System.Threading.Tasks.Task`1<Steamworks.Data.Lobby[]>)" +
                 (looks_task && looks_lobby ? " - matches expectation"
                                            : " - **unexpected; continuing with the actual type, but check the line above**"));
    }

    // ---- 4) 解析 Steamworks.Data.Lobby(空数组的元素类型) ----
    std::string lobby_image;
    std::string lobby_ns;
    void* lobby_cls = resolve_steamworks_class(t, "stage 5", "Lobby", &lobby_image, &lobby_ns);
    void* array_cls = nullptr;
    if (lobby_cls && t.array_class_get)
    {
        array_cls = t.array_class_get(lobby_cls, 1);
        log_line("[steamhack] stage 5: Lobby uses image=" + lobby_image + " ns=\"" + lobby_ns +
                 "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)lobby_cls) +
                 " -> Lobby[] Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)array_cls));
    }
    if (!lobby_cls)
    {
        log_line("[steamhack] stage 5: Steamworks.Data.Lobby unresolved - cannot build an empty array, "
                 "not hooked (fail-safe; not forcing nullptr, which would make the probe unable to determine the result type)");
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
        log_line("[steamhack] stage 5: enumerated " + std::to_string(ctor_candidates) +
                 " single-parameter .ctor(s) (type-filtered hit=" + std::string(ctor ? "yes" : "no") + ")");
    }
    if (!ctor)
    {
        ctor = t.class_get_method_from_name(task_cls, ".ctor", 1);
        log_line("[steamhack] stage 5: no exact .ctor(1)(Lobby[]) by param type - falling back to "
                 "class_get_method_from_name(class, \".ctor\", 1); that result **still goes through the probe**, "
                 "if it is not Task<TResult>(TResult) it is rejected by IsCompleted=false (fail-safe)");
    }
    void* ctor_ptr = method_pointer_of(ctor);
    log_line("[steamhack] stage 5: chosen class..ctor(1) hit=" + std::string(ctor ? "yes" : "no") +
             " MethodInfo=" + cesium::steam::diagnostics::hex((uintptr_t)ctor) +
             " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)ctor_ptr));
    if (!ctor || !ctor_ptr)
    {
        log_line("[steamhack] stage 5: no methodPointer for class..ctor(1) - not hooked (fail-safe)");
        return false;
    }

    bool param_is_vt = false;
    bool param_determined = false;
    std::string param_name = "(unknown)";
    if (t.method_get_param_count && t.method_get_param)
    {
        const uint32_t cpc = t.method_get_param_count(ctor);
        log_line("[steamhack] stage 5: ctor paramCount=" + std::to_string(cpc));
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
                        log_line("[steamhack] stage 5: ctor param Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)pcls) +
                                 " matchesResolvedLobbyArray=" + ((pcls == array_cls) ? "yes" : "no"));
                }
            }
        }
    }
    log_line("[steamhack] stage 5: ctor param full type name=" + param_name + " isValueType=" +
             (param_determined ? (param_is_vt ? "**yes** (unexpected)" : "no (reference type, as expected)")
                               : "unknown (cannot determine, not attempting)"));
    if (param_determined && param_is_vt)
    {
        // 需求: 参数被判定为值类型就**不挂钩** —— 给 Task<Lobby[]> 的 ctor 传"数组指针"是错的,
        // 用零值装箱更会给出一个错误的数组。宁可不挂钩, 也不要给游戏一个错误对象。
        log_line("[steamhack] stage 5: ctor param judged a **value type** (unexpected - the generic param should be a reference type "
                 "Lobby[]) - not boxing a zero value (that would give a wrong array), **not hooked** (fail-safe)");
        return false;
    }

    // ---- 6) 造长度 0 的 Lobby[] ----
    // il2cpp_array_new 的第 1 个参数是**元素类型**(Lobby), 不是数组类(那是 il2cpp_array_new_specific),
    // 所以传 lobby_cls, 得到的才是 Lobby[](传 array_cls 会得到 Lobby[][])。
    void* empty_array = nullptr;
    unsigned long an_seh = 0;
    pod_array_new_empty((void*)t.array_new, lobby_cls, &empty_array, &an_seh);
    if (an_seh)
        log_line("[steamhack] stage 5: il2cpp_array_new(Lobby, 0) raised SEH " + hex_code_str(an_seh) +
                 " - captured by __try/__except");

    if (empty_array)
    {
        PodArrayLength al0;
        pod_array_length(&t, empty_array, &al0);
        log_line("[steamhack] stage 5: built empty array Lobby[0] object=" + cesium::steam::diagnostics::hex((uintptr_t)empty_array) +
                 " length=" + (al0.length >= 0 ? std::to_string(al0.length) : std::string("(unreadable)")) +
                 " (source: " + (al0.source == 1 ? "il2cpp_array_length"
                                               : al0.source == 2 ? "array layout offset" : "none") + ")");
    }
    else
    {
        // 降级: 传 nullptr。调用方有 `array != null` 判空, 语义安全 —— 但探针会因此拿到 null 结果
        // 而判定失败(见 probe_lobby_query_task), 所以最终**不会挂钩**; 这里明确记下走到了哪一步。
        log_line("[steamhack] stage 5: **degraded path** - could not build an empty Lobby[] (il2cpp_array_new"
                 "failed/null). Per the requirement we could degrade to passing nullptr to the ctor (the caller checks `array != null`, "
                 "semantically safe), but without the array the probe cannot verify the result type, so this is expected to end in \"probe failed -> not hooked\".");
    }

    // ---- 7) direct 直调 ctor: 原生 ABI, 绕过 il2cpp_runtime_invoke ----
    PodLobbyQueryCtor dr;
    pod_call_lobby_query_ctor((void*)t.object_new, task_cls, ctor_ptr, ctor, empty_array, &dr);
    {
        std::string s = "[steamhack] stage 5[direct]: native ABI direct call Task<Lobby[]>::.ctor(Lobby[]), "
                        "bypassing il2cpp_runtime_invoke: ";
        s += "ctor methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)ctor_ptr);
        s += " targetClass=" + class_name_of(t, task_cls);
        s += " il2cpp_object_new=" + std::string(dr.alloc_ok ? "ok" : "failed");
        s += " arg=" + (dr.arg_was_null
                             ? std::string("nullptr (degraded path: empty array not passed)")
                             : ("pointer to an empty Lobby[] object " + cesium::steam::diagnostics::hex((uintptr_t)empty_array)));
        if (dr.seh_code)
            s += " | native call raised SEH " + hex_code_str(dr.seh_code) +
                 " - captured by __try/__except, **construction failed**";
        else if (!dr.called)
            s += " | call not executed (missing object_new / ctor methodPointer / target class)";
        else
            s += std::string(" | native call done, object=") +
                 (dr.object ? cesium::steam::diagnostics::hex((uintptr_t)dr.object) : std::string("(empty)"));
        log_line(s);
    }
    if (!dr.object)
    {
        log_line("[steamhack] stage 5: could not construct a Task<Lobby[]> object - not hooked (fail-safe)");
        return false;
    }

    // ---- 8) 探针校验(安装期, 全程 SEH 兜底) ----
    int arr_len = -1;
    int arr_len_src = 0;
    if (!probe_lobby_query_task(t, dr.object, task_cls, array_cls, &arr_len, &arr_len_src))
    {
        log_line("[steamhack] stage 5: probe validation failed - **not hooked**"
                 " (rather not hook than give the game a wrong return value)");
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
            log_line("[steamhack] stage 5[GC root]: il2cpp_gchandle_new raised SEH " + hex_code_str(gh_seh) +
                     " - captured by __try/__except; relying on the module-level global as root only");
        else
            g_lq_task_gchandle = gh;
    }
    log_line("[steamhack] stage 5[GC root]: prebuilt Task held in module global g_lq_task=" +
             cesium::steam::diagnostics::hex((uintptr_t)g_lq_task) + " / g_lq_empty_array=" + cesium::steam::diagnostics::hex((uintptr_t)g_lq_empty_array) +
             " (static storage; Boehm scans writable data of loaded modules)" +
             (g_lq_task_gchandle ? (" + **explicit** il2cpp_gchandle_new handle=" +
                                    std::to_string(g_lq_task_gchandle) + " (pinned=false)")
                                 : std::string(" ; il2cpp_gchandle_new ") +
                                       (t.gchandle_new ? "returned 0 (not successful)" : "export missing") +
                                       " - relying on the global only, noted as is"));
    log_line("[steamhack] stage 5: that Task is **built once and reused by every RequestAsync call** "
             "(Task<T> is immutable; get_Result() always returns the same empty array)");

    // ---- 10) 挂钩 ----
    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, (LPVOID)&Hook_LobbyQueryRequestAsync, (LPVOID*)&g_real_lq_request);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHook(LobbyQuery.RequestAsync) failed: " + std::to_string((int)st) +
                 " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ") - not hooked this time (fail-safe)");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(LobbyQuery.RequestAsync) failed: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_lq_target = target;
    g_lq_method = mi;
    g_lq_hooked = true;
    log_line("[steamhack] LobbyQuery.RequestAsync inline hook enabled "
             "(always returns the prebuilt Task<Lobby[]>, result = empty Lobby[])");
    return true;
}
