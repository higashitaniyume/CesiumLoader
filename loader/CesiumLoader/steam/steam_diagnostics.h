#pragma once

#include <cstdint>
#include <string>
#include <windows.h>

namespace cesium::steam::diagnostics
{
std::string hex(uintptr_t value);
std::string module_offset(HMODULE module, const void* address);
std::string gameassembly_rva(const void* address);
}
