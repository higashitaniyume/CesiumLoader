// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- 装箱路径 / 原生 ABI 直调 / hasValue 诊断 ----------
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
