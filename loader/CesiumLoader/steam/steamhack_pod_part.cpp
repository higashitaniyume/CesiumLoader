// Internal implementation fragment. Included by steamhack.cpp in source order.
// ---------- POD + SEH 安全边界(装箱 / 原生 ABI 直调 / 字段读取 / 元数据解析) ----------

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
