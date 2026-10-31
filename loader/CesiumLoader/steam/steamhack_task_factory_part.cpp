// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- 返回值工厂调用 + 深度探针校验 ----------

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
                                   ? std::string("unknown")
                                   : (r.param_is_valuetype ? std::string("yes") : std::string("no"));
        log_line("[steamhack] " + std::string(f.label) + ": boxing params, factory paramCount=" +
                 std::to_string(r.param_count) + " isValueType=" + vt + " path=" + box_path_desc(r.box_path, r));

        // ---- 额外诊断(需求核心): 直接读出"我们刚装箱并传给工厂的那个 Nullable<Lobby>"
        //      的 hasValue 字段真实值 —— 把"传进去的到底是空 Nullable 还是有值 Nullable"
        //      从推理变成事实。读不到就如实记"未能读取"。
        if (r.param_is_valuetype == 1)
        {
            if (f.param_class && r.boxed_arg)
            {
                PodNullableHasValue hv;
                pod_read_nullable_hasvalue(&t, f.param_class, r.boxed_arg, 1, &hv);
                log_line("[steamhack] " + std::string(f.label) + ": boxed argument hasValue diagnostic (class=" +
                         f.param_class_name + " boxedObject=" + cesium::steam::diagnostics::hex((uintptr_t)r.boxed_arg) + ") -> " +
                         hasvalue_desc(hv, true));
            }
            else
            {
                log_line("[steamhack] " + std::string(f.label) +
                         ": could not read hasValue (factory param class unresolved or no boxed object)");
            }
        }
    }

    // ---- SEH 兜底: 记日志 + 判定失败(安装期 -> 不挂钩; 运行期 -> 本次返回 nullptr) ----
    if (r.seh_code)
    {
        if (f.failures.fetch_add(1, std::memory_order_relaxed) == 0)
            log_line("[steamhack] " + std::string(f.label) + ": runtime_invoke raised SEH " +
                     hex_code_str(r.seh_code) + " - captured by __try/__except, this call is void"
                     " (failure at install time -> not hooked, game behaves as vanilla)");
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
                     ": il2cpp_object_new failed to allocate the Task object (or object_new export missing) -> failing this time");
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
                 ": no usable IsCompleted getter - skipping the completion check (accept on object type only)");
        return true;
    }

    void* exc = nullptr;
    void* boxed = nullptr;
    unsigned long seh = 0;
    pod_runtime_invoke((void*)t.runtime_invoke, get_completed, probe, args, &boxed, &exc, &seh);
    if (seh)
    {
        log_line("[steamhack] " + tag + ": IsCompleted call raised SEH " +
                 hex_code_str(seh) + " - captured by __try/__except, check failed -> not hooked");
        return false;
    }
    if (exc || !boxed)
    {
        log_line("[steamhack] " + tag + ": IsCompleted call failed" +
                 (exc ? "(managed exception)" : "(returned null)") + " - not hooked");
        return false;
    }
    const bool completed = boxed_byte(t, boxed) != 0;
    log_line("[steamhack] " + tag + ": probe object IsCompleted=" +
             (completed ? "true" : "false") +
             (t.object_unbox ? " (boxed value read via il2cpp_object_unbox)"
                             : " (boxed value read via Il2CppObject header +16)"));
    if (!completed)
    {
        log_line("[steamhack] " + tag +
                 ": probe task is not RanToCompletion - not hooked (otherwise the game's await would hang forever)");
        return false;
    }

    // ---- 1.5) 额外诊断(需求核心): 读样本 Task 的 m_result 里那个 Nullable<Lobby> 的 hasValue ----
    // 放在这里而不是最后: 这样即使这个 Task<T> 上查不到 get_Result()(下面的提前 return),
    // hasValue 字段诊断也一定已经写进日志了。
    const int hv = log_sample_task_hasvalue(t, probe, cls, f, tag.c_str());

    // ---- 1.6) 结论行(需求: 要能明确看出这次 direct 到底把 hasValue 变成了什么) ----
    log_line("[steamhack] " + tag + ": hasValue verdict = " +
             (hv == 0 ? "**false** (empty Nullable - exactly what we want; the game skips the if block)"
                      : hv == 1 ? "**true** (non-empty - the opposite of what we want; the game enters the if block and calls Lobby.SetPublic)"
                                : "undetermined (unreadable)"));

    if (strict_nullable && hv == 1)
    {
        log_line("[steamhack] " + tag +
                 ": strict validation failed - the Task built by this path still has hasValue=true (plan C's ABI assumption does not hold)");
        return false;
    }

    // ---- 2) 结果必须"无值" ----
    // 已完成 -> get_Result 不会阻塞, 可以安全调用(若未完成就调它会永久等待, 所以上面必须先查完成态)。
    void* get_result = t.class_get_method_from_name ? t.class_get_method_from_name(cls, "get_Result", 0) : nullptr;
    if (!get_result || !method_pointer_of(get_result))
    {
        log_line("[steamhack] " + tag +
                 ": no get_Result on the class (normal for a non-generic Task) - skipping result checks");
        return true;
    }
    void* rexc = nullptr;
    void* rv = nullptr;
    unsigned long rseh = 0;
    pod_runtime_invoke((void*)t.runtime_invoke, get_result, probe, args, &rv, &rexc, &rseh);
    if (rseh)
    {
        log_line("[steamhack] " + tag + ": get_Result() raised SEH " +
                 hex_code_str(rseh) + " - captured by __try/__except, check failed -> not hooked");
        return false;
    }
    if (rexc)
    {
        log_line("[steamhack] " + tag +
                 ": get_Result() threw a managed exception (task is Faulted/Canceled) - not hooked");
        return false;
    }
    // 注意: 我们传进去的结果参数就是 null, 所以这里**不可能**是真值:
    //   rv == nullptr -> 空 Nullable / 引用类型 null(CLR 语义);
    //   rv != nullptr -> IL2CPP 把"无值 Nullable"装箱成了对象(装箱实现差异), 语义仍是"无值"。
    // 两种情况都安全, 只记录实际观测到哪一种。
    log_line("[steamhack] " + tag + ": probe object get_Result()=" +
             (rv ? ("boxed object " + cesium::steam::diagnostics::hex((uintptr_t)rv) + "(boxing difference of an empty Nullable, still no value)")
                 : std::string("null (no value)")) +
             " - validation passed: completed + result is null");
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
             ": methods in that class containing Lobby/Async/Task/From/Result (max 20): " +
             (names.empty() ? std::string("(none)") : names));
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
    log_line(std::string("[steamhack] ") + label + ": all methods of that class (max 24): " +
             (names.empty() ? std::string("(none, or class_get_methods/method_get_name missing)") : names));
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
        log_direct_attempt(t, f, dr, probe, "install-time probe");
    }
    else
    {
        void* exc = nullptr;
        probe = invoke_task_factory(t, f, &exc);
        log_line(std::string("[steamhack] ") + f.label + "[invoke]: runtime_invoke built a Task, " +
                 (probe ? ("done, object=" + cesium::steam::diagnostics::hex((uintptr_t)probe))
                        : ("failed (" + std::string(exc ? "threw a managed exception" : "returned null") + ")")));
    }

    if (!probe) return nullptr;

    if (t.object_get_class)
    {
        void* pcls = t.object_get_class(probe);
        const bool same = (pcls == f.ret_class);
        log_line(std::string("[steamhack] ") + f.label + "[" + path + "]: probe object=" +
                 cesium::steam::diagnostics::hex((uintptr_t)probe) + " class=" + class_name_of(t, pcls) +
                 " matchesExpectedReturnType=" + (same ? "yes" : "no"));
        if (!same && f.kind != TF_COMPLETED_TASK)
        {
            log_line(std::string("[steamhack] ") + f.label + "[" + path + "]" +
                     ": object type differs from the expected return type - this path fails (avoids handing the wrong type to the game)");
            return nullptr;
        }
        if (!same)
            log_line(std::string("[steamhack] ") + f.label + "[" + path + "]" +
                     ": class differs, but the return type is a non-generic Task (CompletedTask may be an internal subclass), accepting");
    }
    else
    {
        log_line(std::string("[steamhack] ") + f.label + "[" + path + "]: probe object=" +
                 cesium::steam::diagnostics::hex((uintptr_t)probe) + " (object_get_class missing, skipping type check)");
    }

    // 深度校验: 造出来的任务必须真的"已完成"且"结果为 null"。
    // direct 路径额外要求"hasValue 为 false(判定得出时)", 见函数注释。
    const bool strict_nullable = (path_kind == CPATH_DIRECT);
    if (!probe_task_completed_and_null(t, probe, f, f.label, path, strict_nullable)) return nullptr;
    return probe;
}
