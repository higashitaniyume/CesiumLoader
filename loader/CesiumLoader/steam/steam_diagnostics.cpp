#include "steam/steam_diagnostics.h"

#include <fmt/format.h>

namespace cesium::steam::diagnostics
{
std::string hex(uintptr_t value)
{
    return fmt::format("0x{:X}", static_cast<unsigned long long>(value));
}

std::string module_offset(HMODULE module, const void* address)
{
    if (!module) return "(模块未加载)";
    const uintptr_t base = reinterpret_cast<uintptr_t>(module);
    const uintptr_t value = reinterpret_cast<uintptr_t>(address);
    if (value < base || value - base > 0x40000000ull) return "(不在该模块内)";
    return "+" + hex(value - base);
}

std::string gameassembly_rva(const void* address)
{
    HMODULE module = GetModuleHandleW(L"GameAssembly.dll");
    if (!module) return "(GameAssembly.dll 未加载)";
    return "RVA " + module_offset(module, address);
}
}
