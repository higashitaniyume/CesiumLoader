// loader_internal.h - runtime 目录的内部接口(只给加载器自己的实现文件用)
//
// 拆分后的落点(对外仍然只看到 platform/loader.h, 这里的东西一律不导出):
//   loader.cpp            引导流程: 配置 / 控制台 / 变速 / Steam 绕过调用 / 环境变量 / mod 编排
//   il2cpp_boot.cpp       IL2CPP 导出解析 + 域就绪 + HybridCLR 检测 + Assembly.Load(byte[]) + 入口调用
//   activity_forward.cpp  activity-mod-<会话>.log -> 控制台 转发线程
//
// IL2CPP 桥的类型别名与函数表放在全局作用域: platform/loader.h 里已有 `struct Il2Cpp;`
// 前置声明, 三个实现文件共用同一份定义, 不再各自重复。

#pragma once

#include "runtime/il2cpp_safe.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <cstdint>
#include <string>
#include <vector>

// ---------- IL2CPP 导出函数类型 ----------

using Il2CppDomain = void;
using Il2CppAssembly = void;
using Il2CppClass = void;
using Il2CppMethod = void;
using Il2CppObject = void;
using Il2CppException = void;
using Il2CppThread = void;
using Il2CppString = void;

struct Il2Cpp
{
    Il2CppDomain* (*domain_get)() = nullptr;
    void* (*assembly_get_image)(Il2CppAssembly*) = nullptr;
    Il2CppAssembly* (*image_get_assembly)(void*) = nullptr;
    Il2CppClass* (*class_from_name)(void*, const char*, const char*) = nullptr;
    Il2CppMethod* (*class_get_method_from_name)(Il2CppClass*, const char*, int) = nullptr;
    Il2CppMethod* (*class_get_methods)(Il2CppClass*, void**) = nullptr;
    const char* (*method_get_name)(Il2CppMethod*) = nullptr;
    void* (*method_get_param)(Il2CppMethod*, uint32_t) = nullptr;
    uint32_t (*method_get_param_count)(Il2CppMethod*) = nullptr;
    Il2CppObject* (*runtime_invoke)(Il2CppMethod*, Il2CppObject*, void**, Il2CppException**) = nullptr;
    Il2CppThread* (*thread_attach)(Il2CppDomain*) = nullptr;
    Il2CppAssembly** (*domain_get_assemblies)(Il2CppDomain*, size_t*) = nullptr;
    const char* (*image_get_name)(void*) = nullptr;
    void* (*get_corlib)() = nullptr;
    const char* (*type_get_name)(void*) = nullptr;
    Il2CppClass* (*array_class_get)(Il2CppClass*, uint32_t) = nullptr;
    Il2CppObject* (*array_new)(Il2CppClass*, size_t) = nullptr;
    size_t (*array_object_header_size)() = nullptr;
    Il2CppString* (*exception_get_message)(Il2CppException*) = nullptr;
    const wchar_t* (*string_chars)(Il2CppString*) = nullptr;
    // Steam 大厅绕过(steamhack.cpp 第二阶段)新增: 从返回值 MethodInfo 推导闭合泛型类并造默认返回值。
    // 全部**可选**: 不在下面的 ok 必需项里, 缺失只记日志, 由调用方自行放弃该功能。
    void* (*method_get_return_type)(const Il2CppMethod*) = nullptr;
    Il2CppObject* (*type_get_object)(const void*) = nullptr;
    Il2CppClass* (*class_from_system_type)(Il2CppObject*) = nullptr;
    Il2CppObject* (*object_new)(Il2CppClass*) = nullptr;
    Il2CppObject* (*value_box)(Il2CppClass*, void*) = nullptr;
    void* (*class_get_property_from_name)(Il2CppClass*, const char*) = nullptr;
    Il2CppMethod* (*property_get_get_method)(void*) = nullptr;
    Il2CppClass* (*object_get_class)(Il2CppObject*) = nullptr;
    const char* (*class_get_name)(Il2CppClass*) = nullptr;
    bool (*class_is_valuetype)(const Il2CppClass*) = nullptr;
    void* (*object_unbox)(Il2CppObject*) = nullptr;
    Il2CppClass* (*class_get_parent)(Il2CppClass*) = nullptr;
    // 阶段3(steamhack.cpp): Nullable<Lobby>.get_HasValue 挂钩 + hasValue 字段诊断。
    // 同样全部可选, 缺失只记日志。
    void* (*class_get_field_from_name)(Il2CppClass*, const char*) = nullptr;
    size_t (*field_get_offset)(void*) = nullptr;
    void (*field_get_value)(void*, void*, void*) = nullptr;
    Il2CppObject* (*field_get_value_object)(void*, Il2CppObject*) = nullptr;
    // 方案B(steamhack.cpp): Lobby 类字段枚举 + Id 是字段还是属性的判定 + get_Id 返回值尺寸核对。
    void* (*class_get_fields)(Il2CppClass*, void**) = nullptr;
    const char* (*field_get_name)(void*) = nullptr;
    uint32_t (*class_value_size)(Il2CppClass*, uint32_t*) = nullptr;
    // 阶段5(steamhack.cpp): LobbyQuery.RequestAsync 兜底 —— 读空数组的元素个数 + 显式 GC root。
    // 同样全部可选, 缺失只记日志。
    uint32_t (*array_length)(Il2CppObject*) = nullptr;
    uint32_t (*gchandle_new)(Il2CppObject*, bool) = nullptr;
};

namespace cesium::runtime::boot
{

// 解析 GameAssembly.dll 里的 il2cpp_* 导出并填充函数表(关键导出缺失 = 失败)。
bool resolve_il2cpp(HMODULE game_assembly);

// 域内入口(引导线程只直接用到这两个; 其余都包在下面的辅助函数里)。
Il2CppDomain* domain_get();
void thread_attach(Il2CppDomain* domain);

// 等待 GameAssembly.dll 加载(超时返回 nullptr)。
HMODULE wait_game_assembly(DWORD timeout_secs);

// 安全封装用的函数表(resolve_il2cpp 成功后可用; steamhack_install 需要它)。
const cesium_safe::il2cpp_tbl& safe_il2cpp();

// 游戏版本 = Unity 的 Application.version(bundleVersion, 例如 "3.3.0"); 取不到返回空串。
std::string application_version(Il2CppDomain* domain);

// 等待 HybridCLR 热更就绪(AstralParty.Runtime 出现在域内), 超时返回 false。
bool wait_hybridclr(Il2CppDomain* domain, DWORD timeout_secs);

// 调用 System.Reflection.Assembly.Load(byte[]), 返回加载后的程序集(nullptr = 失败)。
Il2CppAssembly* load_assembly_bytes(Il2CppDomain* domain, const std::vector<uint8_t>& bytes,
                                    std::string& err_msg);

// 调用入口 {entry_type}.{entry_method}(无参数静态方法)。
bool run_entry(Il2CppAssembly* asm_, const char* entry_type, const char* entry_method,
               std::string& err_msg);

} // namespace cesium::runtime::boot

namespace cesium::runtime::forward
{

// 启动 activity 日志转发线程(常驻; 是否真正转发由 forwardActivityLog 开关控制)。
// 返回 false = 线程创建失败(旧实现此时只丢弃参数, 不额外记日志)。
bool start_activity_forward_thread();

} // namespace cesium::runtime::forward
