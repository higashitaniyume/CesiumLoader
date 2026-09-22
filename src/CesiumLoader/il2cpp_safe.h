// il2cpp_safe.h - IL2CPP 互操作安全封装
//
// 背景: 原生层直接调用 GameAssembly.dll 的 il2cpp_* 导出, 任何函数指针为空、
// 返回值为空、或托管侧抛异常, 都会导致原生崩溃(0xC0000005 等), 拖垮整个游戏进程。
// 本模块把所有 il2cpp 互操作点收敛成"安全调用":
//   - 函数指针空检查 (get_il2cpp 校验失败则全部拒绝)
//   - 参数/返回值空检查
//   - runtime_invoke 异常转译成可读错误消息
//   - 调用带日志, 失败可追踪
//
// 设计: 纯函数 + 函数指针参数, 与 loader.cpp 的 Il2Cpp 函数表解耦。
// loader.cpp 通过 il2cpp_tbl 结构(见下)传入实际函数指针。
// 本文件是纯标准库, 不依赖 il2cpp 类型(全部用 void*), 可独立测试。

#pragma once

#include <string>
#include <vector>
#include <cstdint>
#include <cstdio>

namespace cesium_safe
{

// loader.cpp 的 Il2Cpp 函数表子集(安全封装需要的那部分)
struct il2cpp_tbl
{
    void* (*domain_get)() = nullptr;
    void* (*assembly_get_image)(void*) = nullptr;
    void* (*image_get_assembly)(void*) = nullptr;
    void* (*class_from_name)(void*, const char*, const char*) = nullptr;
    void* (*class_get_method_from_name)(void*, const char*, int) = nullptr;
    void* (*class_get_methods)(void*, void**) = nullptr;
    const char* (*method_get_name)(void*) = nullptr;
    void* (*method_get_param)(void*, uint32_t) = nullptr;
    uint32_t (*method_get_param_count)(void*) = nullptr;
    void* (*runtime_invoke)(void*, void*, void**, void**) = nullptr;
    void* (*thread_attach)(void*) = nullptr;
    void** (*domain_get_assemblies)(void*, size_t*) = nullptr;
    const char* (*image_get_name)(void*) = nullptr;
    void* (*get_corlib)() = nullptr;
    const char* (*type_get_name)(void*) = nullptr;
    void* (*array_class_get)(void*, uint32_t) = nullptr;
    void* (*array_new)(void*, size_t) = nullptr;
    size_t (*array_object_header_size)() = nullptr;
    void* (*exception_get_message)(void*) = nullptr;
    const wchar_t* (*string_chars)(void*) = nullptr;

    // ---- Steam 大厅绕过(steamhack.cpp 第二阶段)新增 ----
    // 用途: 从"被挂钩方法自己的 MethodInfo"推导返回类型, 再据此造一个"已完成、结果为 null"
    // 的 Task<T>(详见 steamhack.cpp 的 make_default_completed_task)。
    // 这些**不是**必需项: 是否存在于各版本导出表中不确定, 缺失只记日志(缺失时对应功能
    // 自行放弃, 绝不崩)。因此刻意不加入 complete() 判定, 以免影响既有调用方。
    void* (*method_get_return_type)(void*) = nullptr;          // il2cpp_method_get_return_type
    void* (*type_get_object)(void*) = nullptr;                 // il2cpp_type_get_object
    void* (*class_from_system_type)(void*) = nullptr;          // il2cpp_class_from_system_type
    void* (*object_new)(void*) = nullptr;                      // il2cpp_object_new
    // 把**值类型参数**装箱: runtime_invoke 对值类型参数要求传装箱对象, 传 null 会在
    // il2cpp_field_get_value_object 里解引用空指针(实测 0xC0000005)。空 Nullable 装箱
    // 按 .NET 语义可能返回 null, 因此调用点必须再用 object_new 兜底。
    void* (*value_box)(void*, void*) = nullptr;                // il2cpp_value_box(klass, data)
    void* (*class_get_property_from_name)(void*, const char*) = nullptr;   // il2cpp_class_get_property_from_name
    void* (*property_get_get_method)(void*) = nullptr;         // il2cpp_property_get_get_method
    void* (*object_get_class)(void*) = nullptr;                // il2cpp_object_get_class
    const char* (*class_get_name)(void*) = nullptr;            // il2cpp_class_get_name
    bool (*class_is_valuetype)(void*) = nullptr;               // il2cpp_class_is_valuetype
    void* (*object_unbox)(void*) = nullptr;                    // il2cpp_object_unbox
    void* (*class_get_parent)(void*) = nullptr;                // il2cpp_class_get_parent

    // ---- 阶段3: Nullable<Lobby>.get_HasValue 挂钩 + hasValue 字段诊断新增 ----
    // 用途 1: 解析 System.Nullable`1<Steamworks.Data.Lobby>::get_HasValue() 以便 inline hook
    //         (恒返回 false —— Steam 不存在时任何 Lobby? 都视同无值)。
    // 用途 2: **诊断**: 直接读出装箱 Nullable<Lobby> 的 hasValue 字段真实值, 把"Task 里到底
    //         存的是有值还是无值"从推理变成事实(见 steamhack.cpp 的 pod_read_nullable_hasvalue)。
    // 与上面那批一样全部**可选**: 缺失只记日志, 相关能力自行放弃, 绝不崩。
    void* (*class_get_field_from_name)(void*, const char*) = nullptr;  // il2cpp_class_get_field_from_name
    size_t (*field_get_offset)(void*) = nullptr;                       // il2cpp_field_get_offset
    void (*field_get_value)(void*, void*, void*) = nullptr;            // il2cpp_field_get_value(field, obj, out)
    void* (*field_get_value_object)(void*, void*) = nullptr;           // il2cpp_field_get_value_object(field, obj) -> 装箱值

    // ---- 方案B: Steamworks.Data.Lobby 的字段/方法名诊断 + get_Id 返回值尺寸核对 ----
    // 用途 1: 列出 Lobby 类的字段名(需求: 字段名列表上限 24 条)。
    // 用途 2: 判定 Lobby.Id 到底是字段还是属性(两边的 API 都试), 并把结论写进日志。
    // 用途 3: 核对 get_Id 的返回结构体尺寸 —— x64 ABI 下 >8 字节的结构体走**隐藏返回缓冲区**,
    //         那时"RAX 返回 0"是错的(会写错位置), 必须据此拒绝挂钩而不是硬来。
    // 同样全部**可选**: 缺失只记日志, 相关能力自行放弃, 绝不崩。
    void* (*class_get_fields)(void*, void**) = nullptr;        // il2cpp_class_get_fields(klass, &iter)
    const char* (*field_get_name)(void*) = nullptr;            // il2cpp_field_get_name(field)
    uint32_t (*class_value_size)(void*, uint32_t*) = nullptr;  // il2cpp_class_value_size(klass, &align)

    // ---- 阶段5: Steamworks.Data.LobbyQuery.RequestAsync 兜底新增 ----
    // 用途 1: 读"长度为 0 的 Lobby[]"的元素个数, 作为安装期探针的**权威**校验。
    //         缺失时退化为按数组布局读 max_length(见 steamhack.cpp 的 pod_array_length) —— 所以它
    //         **不是**必需项, 缺失只是日志里少一条权威来源。
    // 用途 2: 把预建好的 Task<Lobby[]> **显式**注册成 GC root。模块级全局变量(静态存储期)理论上
    //         也能当根(Boehm 会扫描已加载模块的可写数据段), 但"Boehm 到底扫不扫我们这张 DLL 的
    //         .data"不是本模块能保证的事; 而该 Task 会被**所有** RequestAsync 调用复用, 一旦被回收
    //         就是 use-after-free(运行期崩溃, 不是功能失效)。所以这里多上一道显式保证。
    //         缺失时只用全局变量, 并在日志里如实标注。
    // 与上面几批一样全部**可选**: 缺失只记日志, 相关能力自行降级, 绝不崩。
    uint32_t (*array_length)(void*) = nullptr;                 // il2cpp_array_length(array) -> 元素个数
    uint32_t (*gchandle_new)(void*, bool) = nullptr;           // il2cpp_gchandle_new(obj, pinned) -> 句柄

    // 关键导出是否齐全(与 get_il2cpp 的校验一致)
    bool complete() const
    {
        return domain_get && assembly_get_image && image_get_assembly &&
               class_from_name && class_get_method_from_name && class_get_methods &&
               method_get_name && method_get_param && method_get_param_count &&
               runtime_invoke && thread_attach && domain_get_assemblies &&
               image_get_name && get_corlib && type_get_name &&
               array_class_get && array_new && array_object_header_size;
    }
};

// 运行时错误信息(最近一次安全调用的错误; 空 = 无错误)
extern std::string g_last_error;

inline void set_error(const char* where, const std::string& msg)
{
    g_last_error = std::string(where) + ": " + msg;
}

/// 安全地枚举域内全部程序集 image 名。
/// 任一环节空指针都会中止并返回空(错误记录在 g_last_error)。
inline std::vector<std::string> safe_list_assembly_names(const il2cpp_tbl& t)
{
    std::vector<std::string> names;
    if (!t.domain_get_assemblies || !t.assembly_get_image || !t.image_get_name)
    {
        set_error("list_assemblies", "函数指针缺失");
        return names;
    }
    size_t count = 0;
    void** p = t.domain_get_assemblies(t.domain_get(), &count);
    if (!p)
    {
        set_error("list_assemblies", "domain_get_assemblies 返回空");
        return names;
    }
    for (size_t i = 0; i < count; i++)
    {
        void* asm_ = p[i];
        if (!asm_) continue;
        void* image = t.assembly_get_image(asm_);
        if (!image) continue;
        const char* n = t.image_get_name(image);
        if (!n) continue;
        names.emplace_back(n);
    }
    return names;
}

/// 宽字符 → UTF-8(小工具; 不依赖任何运行时)
inline std::string wide_to_utf8(const wchar_t* w)
{
    std::string out;
    for (; w && *w; ++w)
    {
        uint32_t cp = (uint32_t)*w;
        if (cp < 0x80) out.push_back((char)cp);
        else if (cp < 0x800)
        {
            out.push_back((char)(0xC0 | (cp >> 6)));
            out.push_back((char)(0x80 | (cp & 0x3F)));
        }
        else
        {
            out.push_back((char)(0xE0 | (cp >> 12)));
            out.push_back((char)(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back((char)(0x80 | (cp & 0x3F)));
        }
    }
    return out;
}

/// 调用一个无参"返回 string"的实例方法, 取出结果(失败返回空串)。
inline std::string invoke_string(const il2cpp_tbl& t, void* method, void* self)
{
    if (!method || !t.runtime_invoke || !t.string_chars) return std::string();
    void* exc = nullptr;
    void* s = t.runtime_invoke(method, self, nullptr, &exc);
    if (exc || !s) return std::string();
    return wide_to_utf8(t.string_chars(s));
}

/// 把托管异常转成可读文本(尽力而为, 绝不抛)。
///
/// 为什么要这么绕: **il2cpp_exception_get_message 并不是 IL2CPP 的标准导出**, 很多游戏
/// (包括本游戏)根本没有它 —— 只靠它就只会得到一句无信息的"托管异常"。
/// 所以这里依次尝试:
///   1) il2cpp_exception_get_message(有就用)
///   2) 反射调用托管 `System.Exception.get_Message`(基类实现直接读 _message 字段)
///   3) `Object.ToString()` 兜底
///   4) 至少给出异常**类型名**(object_get_class + class_get_name 是标准导出)
inline std::string exception_text(const il2cpp_tbl& t, void* exc)
{
    if (!exc) return "托管异常";

    // 异常类型名(标准导出, 一定有)
    std::string type_name;
    if (t.object_get_class && t.class_get_name)
    {
        void* cls = t.object_get_class(exc);
        if (cls)
        {
            const char* n = t.class_get_name(cls);
            if (n) type_name = n;
        }
    }

    // 消息: 先试原生导出
    std::string detail;
    if (t.exception_get_message && t.string_chars)
    {
        void* s = t.exception_get_message(exc);
        if (s) detail = wide_to_utf8(t.string_chars(s));
    }

    // 再试托管 Exception.Message / Object.ToString
    if (detail.empty() && t.get_corlib && t.class_from_name && t.class_get_method_from_name)
    {
        void* corlib = t.get_corlib();
        if (corlib)
        {
            void* exc_cls = t.class_from_name(corlib, "System", "Exception");
            if (exc_cls)
            {
                detail = invoke_string(t, t.class_get_method_from_name(exc_cls, "get_Message", 0), exc);
                if (detail.empty())
                {
                    void* obj_cls = t.class_from_name(corlib, "System", "Object");
                    if (obj_cls)
                        detail = invoke_string(t, t.class_get_method_from_name(obj_cls, "ToString", 0), exc);
                }
            }
        }
    }

    if (!type_name.empty() && !detail.empty()) return type_name + ": " + detail;
    if (!type_name.empty()) return type_name + " (消息取不到)";
    if (!detail.empty()) return detail;
    return "托管异常(类型名与消息都取不到)";
}

/// 安全地调用一个静态方法(instance=nullptr), 异常转译成错误消息。
/// 返回托管返回值(可为空); 失败返回 nullptr, g_last_error 记录原因。
inline void* safe_invoke_static(const il2cpp_tbl& t,
                                void* method, void* obj,
                                void** args,
                                const char* where)
{
    if (!t.runtime_invoke)
    {
        set_error(where, "runtime_invoke 函数指针缺失");
        return nullptr;
    }
    if (!method)
    {
        set_error(where, "method 为空");
        return nullptr;
    }
    void* exc = nullptr;
    void* result = t.runtime_invoke(method, obj, args, &exc);
    if (exc)
    {
        set_error(where, exception_text(t, exc));
        return nullptr;
    }
    return result;
}

/// 安全调用 Assembly.Load(byte[]) 思路的基元: 调用静态方法并检查结果。
/// 返回 false = 失败(错误在 g_last_error)。
inline bool safe_invoke_ok(const il2cpp_tbl& t,
                           void* method, void* obj, void** args,
                           const char* where)
{
    void* r = safe_invoke_static(t, method, obj, args, where);
    if (!r && !g_last_error.empty()) return false;
    return true;
}

} // namespace cesium_safe
