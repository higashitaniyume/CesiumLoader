// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- 默认返回值工厂解析(FromResult / ctor / CompletedTask) ----------

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
        log_line(std::string("[steamhack] ") + label + ": IL2CPP exports missing (" + missing +
                 ") - cannot build a default return value, not hooked");
        return false;
    }

    // ---- 返回类型 -> System.Type -> Il2CppClass ----
    void* ret = t.method_get_return_type(hooked_mi);
    if (!ret)
    {
        log_line(std::string("[steamhack] ") + label + ": method_get_return_type returned null - not hooked");
        return false;
    }
    f.ret_type = ret;
    f.ret_type_name = t.type_get_name ? cstr_or(t.type_get_name(ret), "(null)") : std::string("(type_get_name missing)");

    void* tobj = t.type_get_object(ret);
    if (!tobj)
    {
        log_line(std::string("[steamhack] ") + label + ": type_get_object(" + f.ret_type_name + ") returned null - not hooked");
        return false;
    }
    void* cls = t.class_from_system_type(tobj);
    if (!cls)
    {
        log_line(std::string("[steamhack] ") + label + ": class_from_system_type returned null - not hooked");
        return false;
    }
    f.ret_class = cls;

    log_line(std::string("[steamhack] ") + label + ": return value resolved, type=" + f.ret_type_name +
             " Il2CppType=" + cesium::steam::diagnostics::hex((uintptr_t)ret) +
             " System.Type=" + cesium::steam::diagnostics::hex((uintptr_t)tobj) +
             " Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) +
             " className=" + class_name_of(t, cls));

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
    log_line(std::string("[steamhack] ") + label + ": generic check typeNameContains`=" + (by_type_name ? "yes" : "no") +
             " classNameContains`=" + (by_class_name ? "yes" : "no") +
             " hasGetResult=" + (by_get_result ? "yes" : "no") +
             " -> " + (cls_is_generic ? "Task<T> (generic)" : "non-generic Task"));

    // ---- 候选 A: cls.FromResult(1) ----
    void* cand = t.class_get_method_from_name(cls, "FromResult", 1);
    if (cand)
    {
        void* cand_ret = t.method_get_return_type(cand);
        void* cand_ptr = method_pointer_of(cand);
        const bool ret_match = (cand_ret == f.ret_type);
        log_line(std::string("[steamhack] ") + label + ": cls.FromResult resolve hit=yes MethodInfo=" +
                 cesium::steam::diagnostics::hex((uintptr_t)cand) + " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)cand_ptr) +
                 " returnTypePtrMatchesBody=" + (ret_match ? "yes" : "no"));
        if (ret_match && cand_ptr)
        {
            f.factory = cand;
            f.kind = TF_FROM_RESULT;
        }
        else
        {
            log_line(std::string("[steamhack] ") + label +
                     ": FromResult validation failed (return type pointer mismatch or null methodPointer) - "
                     "treated as an open generic definition on the base type; not used, falling back to the constructor");
        }
    }
    else
    {
        log_line(std::string("[steamhack] ") + label +
                 ": cls.FromResult resolve hit=no (FromResult is declared on the non-generic Task, as expected)");
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
            log_line(std::string("[steamhack] ") + label + ": using constructor class..ctor(1) MethodInfo=" +
                     cesium::steam::diagnostics::hex((uintptr_t)ctor) + " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)ctor_ptr) +
                     " + il2cpp_object_new - equivalent to FromResult (the Mono source comment says "
                     "\"Construct a pre-completed Task<TResult>\")");
            log_line(std::string("[steamhack] ") + label +
                     ": plan C availability - the methodPointer above is the entry for the native ABI direct call; "
                     "steamBypassTaskCtorMode=\"" + std::string(g_ctor_mode_name) + "\"");
        }
        else
        {
            log_line(std::string("[steamhack] ") + label + ": class..ctor(1) resolve hit=" +
                     (ctor ? "yes" : "no") +
                     (ctor && !ctor_ptr ? " but methodPointer is null" : "") +
                     (t.object_new ? "" : " and il2cpp_object_new export is missing") + " - that fallback is unavailable");
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
                     ": return type is a non-generic Task, using the Task.CompletedTask getter MethodInfo=" +
                     cesium::steam::diagnostics::hex((uintptr_t)getter));
        }
        else
        {
            log_line(std::string("[steamhack] ") + label + ": Task.CompletedTask resolve hit=no");
        }
    }
    else if (!f.factory)
    {
        log_line(std::string("[steamhack] ") + label +
                 ": return type is a generic Task<T> - using Task.CompletedTask is explicitly forbidden (type confusion), candidate skipped");
    }

    if (!f.factory)
    {
        log_line(std::string("[steamhack] ") + label + ": all three construction paths failed - not hooked (game behaves as vanilla)");
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
        log_line(std::string("[steamhack] ") + label + ": factory paramCount=" + std::to_string(pc));
        if (pc == 1)
        {
            void* pt = t.method_get_param(f.factory, 0);
            const std::string ptn = (pt && t.type_get_name) ? cstr_or(t.type_get_name(pt), "(null)")
                                                            : std::string("(unknown)");
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
            log_line(std::string("[steamhack] ") + label + ": factory param type=" + ptn +
                     " isValueType=" + (determined ? (is_vt ? "yes" : "no") : "unknown (cannot determine)"));
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
                log_line(std::string("[steamhack] ") + label + ": factory param class pcls=" + cesium::steam::diagnostics::hex((uintptr_t)pcls) +
                         " className=" + f.param_class_name +
                         " (stage 3 will use it to resolve Nullable.get_HasValue)" +
                         " classNameConfirmsNullable=" + (f.param_is_nullable ? "yes" : "no"));
            }

            if (!determined)
            {
                log_line(std::string("[steamhack] ") + label +
                         ": cannot tell whether the factory param is a value type (missing type_get_object / class_from_system_type / "
                         "class_is_valuetype) - not attempting, not hooked");
                return false;
            }
            if (is_vt && !t.value_box && !t.object_new)
            {
                log_line(std::string("[steamhack] ") + label +
                         ": factory param is a value type but both il2cpp_value_box and il2cpp_object_new are missing - "
                         "cannot box (passing null crashed the game last round), not hooked");
                return false;
            }
            if (is_vt)
            {
                log_line(std::string("[steamhack] ") + label + ": factory param is a value type (" +
                         (ptn.find("Nullable") != std::string::npos ? "empty Nullable" : "non-Nullable") +
                         ") - will box with a 256B zero-initialized buffer: il2cpp_value_box" +
                         (t.value_box ? "(available)" : "(export missing)") +
                         (t.object_new ? " + il2cpp_object_new fallback (when boxing an empty Nullable returns NULL)" : "(no fallback)"));
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
                 "[invoke]: steamBypassTaskCtorMode=\"invoke\" - runtime_invoke only (no direct attempt)");
        probe = try_build_and_validate(t, f, CPATH_INVOKE);
        if (probe) f.ctor_path = CPATH_INVOKE;
    }
    else if (direct_possible)
    {
        log_line(std::string("[steamhack] ") + label +
                 "[direct]: trying plan C - native ABI direct Task<T>..ctor(T) (steamBypassTaskCtorMode=\"" +
                 std::string(g_ctor_mode_name) + "\") ...");
        probe = try_build_and_validate(t, f, CPATH_DIRECT);
        if (probe)
        {
            f.ctor_path = CPATH_DIRECT;
        }
        else if (g_ctor_mode == CTOR_MODE_AUTO)
        {
            log_line(std::string("[steamhack] ") + label +
                     "[auto]: the direct path failed validation (see the hasValue verdict above) - "
                     "**automatically falling back to runtime_invoke**, see the next line");
            probe = try_build_and_validate(t, f, CPATH_INVOKE);
            if (probe)
            {
                f.ctor_path = CPATH_INVOKE;
                log_line(std::string("[steamhack] ") + label +
                         "[auto]: fell back to the invoke path and passed validation - on this path hasValue may still be judged "
                         "true, plan B (Lobby.SetPublic/SetJoinable/Id hooked as harmless) should then catch it");
            }
        }
        else
        {
            log_line(std::string("[steamhack] ") + label +
                     "[direct]: steamBypassTaskCtorMode=\"direct\" and direct validation failed - "
                     "not falling back to invoke per config, not hooked (fail-safe)");
        }
    }
    else
    {
        const std::string why = (f.kind != TF_CTOR_OBJECT_NEW)
                                    ? std::string("factory is not class..ctor(1)")
                                    : (!f.ctor_ptr ? std::string("ctor methodPointer is null")
                                                   : std::string("il2cpp_object_new export missing"));
        if (g_ctor_mode == CTOR_MODE_DIRECT)
        {
            log_line(std::string("[steamhack] ") + label + "[direct]: " + why +
                     " - direct path not applicable and the mode is \"direct\" -> not hooked (fail-safe)");
            return false;
        }
        log_line(std::string("[steamhack] ") + label + "[invoke]: " + why +
                 " - direct not applicable, using runtime_invoke");
        probe = try_build_and_validate(t, f, CPATH_INVOKE);
        if (probe) f.ctor_path = CPATH_INVOKE;
    }

    if (!probe)
    {
        log_line(std::string("[steamhack] ") + label +
                 ": probe ultimately failed (neither direct nor invoke passed) - not hooked (game behaves as vanilla)");
        dump_class_methods(t, f.ret_class, label);
        return false;
    }

    log_line(std::string("[steamhack] ") + label + ": probe succeeded - path used = " +
             ctor_path_name(f.ctor_path) +
             " (steamBypassTaskCtorMode=\"" + std::string(g_ctor_mode_name) + "\")");

    // 注意: 探测对象到此丢弃(不保存裸指针、不建 GC 根) —— 命中路径每次都会重新造一个新的。
    f.ok = true;
    return true;
}
