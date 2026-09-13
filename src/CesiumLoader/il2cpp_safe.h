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
        // 转译异常消息(ClassName: Message)
        std::string msg = "托管异常";
        if (t.exception_get_message && t.string_chars)
        {
            void* exc_msg = t.exception_get_message(exc);
            if (exc_msg)
            {
                const wchar_t* w = t.string_chars(exc_msg);
                if (w)
                {
                    // 宽字符 → UTF-8 (小工具)
                    std::string utf8;
                    for (; *w; w++)
                    {
                        uint32_t cp = *w;
                        if (cp < 0x80) utf8.push_back((char)cp);
                        else if (cp < 0x800) { utf8.push_back((char)(0xC0 | (cp >> 6))); utf8.push_back((char)(0x80 | (cp & 0x3F))); }
                        else { utf8.push_back((char)(0xE0 | (cp >> 12))); utf8.push_back((char)(0x80 | ((cp >> 6) & 0x3F))); utf8.push_back((char)(0x80 | (cp & 0x3F))); }
                    }
                    msg = utf8;
                }
            }
        }
        set_error(where, msg);
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
