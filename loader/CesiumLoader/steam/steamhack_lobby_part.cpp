// Internal implementation fragment. Included by steamhack.cpp in source order.
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
        return "il2cpp_value_box (256B zero-initialized stack buffer) succeeded -> boxed object " + cesium::steam::diagnostics::hex((uintptr_t)r.boxed_arg);
    case BOX_OBJNEW_AFTER_NULL:
        return "il2cpp_value_box returned NULL (the .NET boxing semantics of an empty Nullable) -> falling back to il2cpp_object_new -> " +
               cesium::steam::diagnostics::hex((uintptr_t)r.boxed_arg);
    case BOX_OBJNEW_NO_EXPORT:
        return "il2cpp_value_box export missing -> falling back to il2cpp_object_new -> " + cesium::steam::diagnostics::hex((uintptr_t)r.boxed_arg);
    case BOX_FAILED_UNKNOWN:
        return "cannot tell whether the param is a value type (missing method_get_param/type_get_object/class_from_system_type/"
               "class_is_valuetype) -> failing this time (never risk passing null)";
    case BOX_FAILED_ALL:
        return "value-type param, but neither il2cpp_value_box nor il2cpp_object_new produced a boxed object -> failing this time";
    default:
        return (r.param_count >= 1) ? std::string("reference-type param -> args[0]=nullptr (legal)")
                                    : std::string("factory takes 0 params -> no argument needed");
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
        log_line(tag + "not applicable - the factory is not class..ctor(1) (or ctor methodPointer / il2cpp_object_new is missing), "
                      "direct path was not attempted");
        return;
    }

    std::string s = tag + "native ABI direct Task<T>..ctor(T), bypassing il2cpp_runtime_invoke: ";
    s += "ctor methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)f.ctor_ptr);
    s += " targetClass=" + class_name_of(t, f.ret_class);
    s += " il2cpp_object_new=" + std::string(r.alloc_ok ? "ok" : "failed");
    s += " arg=points to " + std::to_string(r.buffer_bytes) + " byte all-zero stack buffer (";
    s += r.buf_first16_zero ? "first 16B confirmed zero -> an empty Nullable was passed (hasValue=false)"
                            : "first 16B non-zero (unexpected, should not happen)";
    s += ")";

    if (r.seh_code)
        s += " | native call raised SEH " + hex_code_str(r.seh_code) +
             " - captured by __try/__except, **this path is void**";
    else if (!r.called)
        s += " | call not executed (see above)";
    else
        s += std::string(" | native call done, object=") +
             (obj ? cesium::steam::diagnostics::hex((uintptr_t)obj) : std::string("(empty)"));
    log_line(s);
}

// hasValue 读取结果 -> 可读描述。**必须写明"读到没有"**: 字段找不到 / 导出缺失 / SEH 异常
// 都要如实写出来(需求原文: 做不到就说明做不到, 不要硬来)。
std::string hasvalue_desc(const PodNullableHasValue& h, bool boxed)
{
    if (h.seh_code)
        return "read raised SEH " + hex_code_str(h.seh_code) +
               " - captured by __try/__except, could not read hasValue";
    if (!h.field)
        return "could not read hasValue (the class has neither \"hasValue\" nor \"has_value\", or "
               "il2cpp_class_get_field_from_name export missing)";

    std::string s = "hasValue field FieldInfo=" + cesium::steam::diagnostics::hex((uintptr_t)h.field) +
                    " name=" + std::string(h.field_by == 1 ? "\"hasValue\"" : "\"has_value\"");
    s += h.have_offset ? (" offset=" + std::to_string(h.offset))
                       : std::string(" offset=(il2cpp_field_get_offset export missing, unavailable)");
    s += h.have_direct ? (" directOffsetRead=" + std::string(h.direct_value ? "true" : "false"))
                       : std::string(" directOffsetRead=(not available)");
    s += h.have_fieldget ? (" il2cpp_field_get_value=" + std::string(h.fieldget_value ? "true" : "false"))
                         : std::string(" il2cpp_field_get_value=(export missing or unavailable)");
    if (boxed)
        s += " (data pointer source: " +
             std::string(h.data_from_unbox == 1 ? "il2cpp_object_unbox"
                                                : (h.data_from_unbox == 2 ? "fallback via Il2CppObject header +16"
                                                                          : "not available")) +
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
                 ": could not read hasValue (factory param class unresolved; no Nullable class available)");
        return -1;
    }

    // hasValue 在 Nullable 结构里的内部偏移(权威值来自 il2cpp_field_get_offset)
    PodFieldOffset inner;
    pod_nullable_hasvalue_field(&t, f.param_class, &inner);

    PodTaskResultProbe tr;
    pod_probe_task_result_nullable(&t, task_cls, probe, f.param_class,
                                   inner.have_offset ? inner.offset : 0ULL,
                                   inner.have_offset, &tr);

    std::string msg = std::string("[steamhack] ") + label + ": sample Task's m_result(" +
                      f.param_class_name + ") hasValue diagnostic -> ";
    if (tr.seh_code)
    {
        msg += "read raised SEH " + hex_code_str(tr.seh_code) +
               " - captured by __try/__except, could not read hasValue";
    }
    else if (!tr.found)
    {
        msg += "could not read hasValue (m_result not found on the Task class, or "
               "il2cpp_class_get_field_from_name export missing)";
    }
    else
    {
        msg += "m_result FieldInfo=" + cesium::steam::diagnostics::hex((uintptr_t)tr.field);
        msg += tr.have_offset ? (" offset=" + std::to_string(tr.offset))
                              : std::string(" offset=(il2cpp_field_get_offset export missing)");
        if (tr.value_object_called)
        {
            if (tr.value_object_is_null)
                msg += " | il2cpp_field_get_value_object -> NULL (boxing semantics of an empty Nullable => hasValue=false)";
            else if (tr.have_inner)
                msg += " | il2cpp_field_get_value_object -> boxed object, read hasValue=" +
                       std::string(tr.inner_value ? "true" : "false");
            else
                msg += " | il2cpp_field_get_value_object -> boxed object, but its hasValue field could not be read";
        }
        else
        {
            msg += " | il2cpp_field_get_value_object export missing, authoritative path not used";
        }
        if (tr.have_direct)
            msg += std::string(" | secondary(direct offset read, assuming reference field offset includes the 16B header)=") +
                   (tr.direct_value ? "true" : "false");
        else
            msg += " | secondary(direct offset read)=(not available)";
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
        log_line("[steamhack] IL2CPP function table missing: " + missing + " - cannot resolve SteamMatchmaking");
        return nullptr;
    }

    void* domain = t.domain_get();
    if (!domain)
    {
        log_line("[steamhack] il2cpp domain is null, cannot resolve SteamMatchmaking");
        return nullptr;
    }

    size_t count = 0;
    void** assemblies = t.domain_get_assemblies(domain, &count);
    if (!assemblies)
    {
        log_line("[steamhack] domain_get_assemblies returned null");
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
            log_line("[steamhack] class SteamMatchmaking hit: image=" + image_names[i] +
                     " ns=\"" + std::string(ns) + "\" Il2CppClass=" + cesium::steam::diagnostics::hex((uintptr_t)cls) +
                     " className=" + class_name_of(t, cls));
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
        log_line("[steamhack] class SteamMatchmaking not found (candidate ns: \"Steamworks\" / global / \"Steamworks.NET\")"
                 " - stage 2 cannot be installed");
        log_line("[steamhack] domain assemblies " + std::to_string(image_names.size()) + " items: " + all_images);
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
        log_line("[steamhack] no hit for SteamMatchmaking." + std::string(method_name) + "(" +
                 std::to_string(arg_count) + " params): image=" + image + " ns=\"" + ns + "\"");
        dump_class_methods(t, cls, method_name);
        return false;
    }

    void* target = method_pointer_of(mi);
    const uint32_t pc = t.method_get_param_count ? t.method_get_param_count(mi) : (uint32_t)arg_count;
    log_line("[steamhack] hit SteamMatchmaking." + std::string(method_name) + ": image=" + image +
             " ns=\"" + ns + "\" MethodInfo=" + cesium::steam::diagnostics::hex((uintptr_t)mi) +
             " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)target) + " (" + cesium::steam::diagnostics::gameassembly_rva(target) + ")" +
             " paramCount=" + std::to_string(pc));
    if (!target)
    {
        log_line("[steamhack] SteamMatchmaking." + std::string(method_name) +
                 " methodPointer is null (possibly not an AOT method) - skipping this hook");
        return false;
    }

    if (!resolve_task_factory(t, f, mi, method_name)) return false;

    if (!ensure_minhook()) return false;

    MH_STATUS st = MH_CreateHook(target, detour, real_out);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_CreateHook(" + std::string(method_name) + ") failed: " +
                 std::to_string((int)st) + " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ")");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(" + std::string(method_name) + ") failed: " +
                 std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    if (target_out) *target_out = target;
    if (method_out) *method_out = mi;
    log_line("[steamhack] SteamMatchmaking." + std::string(method_name) +
             " inline hook enabled (always returns a completed empty Task)");
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
    log_line("[steamhack] " + f.param_class_name + ": get_HasValue target " +
             cesium::steam::diagnostics::hex((uintptr_t)target) + " RVA logged above; "
             "shared-generic scope **not determined** (metadata resolution on an open generic can trip an il2cpp assertion that cannot be caught, "
             "so it is skipped per fail-safe) - if this method is shared across Nullable<T> types, \"always false\" applies to them too");
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
                 ": factory param class (Nullable) unresolved, cannot resolve get_HasValue - not hooked (fail-safe)");
        return nullptr;
    }
    if (!t.class_get_method_from_name)
    {
        log_line("[steamhack] " + std::string(f.label) +
                 ": il2cpp_class_get_method_from_name export missing - cannot resolve get_HasValue, not hooked (fail-safe)");
        return nullptr;
    }

    // 【危险调用 #5】解析包在 POD + SEH 里(需求 #7): 违例只会导致"本次解析失败", 不会崩安装期。
    PodHasValueMethod r;
    pod_resolve_hasvalue_method(&t, f.param_class, &r);
    if (r.seh_code)
    {
        log_line("[steamhack] " + std::string(f.label) + ": resolve " + f.param_class_name +
                 "::get_HasValue() raised SEH " + hex_code_str(r.seh_code) +
                 " - captured by __try/__except, not hooked this time (fail-safe)");
        return nullptr;
    }
    if (!r.mi)
    {
        log_line("[steamhack] " + std::string(f.label) + ": class " + f.param_class_name +
                 " no get_HasValue() (0 params) hit - not hooked (fail-safe)");
        dump_all_methods(t, f.param_class, "Nullable.get_HasValue");
        return nullptr;
    }

    log_line("[steamhack] " + std::string(f.label) + ": hit " + f.param_class_name +
             "::get_HasValue() MethodInfo=" + cesium::steam::diagnostics::hex((uintptr_t)r.mi) +
             " methodPointer=" + cesium::steam::diagnostics::hex((uintptr_t)r.ptr) + " (" + cesium::steam::diagnostics::gameassembly_rva(r.ptr) + ")");

    if (!r.ptr)
    {
        log_line("[steamhack] " + std::string(f.label) +
                 ": get_HasValue methodPointer is null (possibly not AOT-compiled) - **log only, no hook** (fail-safe)");
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
        log_line("[steamhack] MH_CreateHook(Nullable.get_HasValue) failed: " + std::to_string((int)st) +
                 " (target=" + cesium::steam::diagnostics::hex((uintptr_t)target) + ") - not hooked this time (fail-safe)");
        return false;
    }

    st = MH_EnableHook(target);
    if (st != MH_OK)
    {
        log_line("[steamhack] MH_EnableHook(Nullable.get_HasValue) failed: " + std::to_string((int)st));
        MH_RemoveHook(target);
        return false;
    }

    g_hv_target = target;
    g_hv_method = mi;
    g_hv_hooked = true;
    f.hasvalue_ok = true;
    log_line("[steamhack] " + f.param_class_name +
             "::get_HasValue inline hook enabled (always false: any Lobby? counts as no value; "
             "game keeps LobbyId 0 -> RequestCreateC2S is used)");
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
