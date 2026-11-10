#pragma once
#include "config/config.h"
#include <string>
#if __has_include("build_info.generated.h")
#include "build_info.generated.h"
#endif
#ifndef CESIUM_BUILD_COMMIT
#define CESIUM_BUILD_COMMIT "unknown"
#endif

namespace cesium {
inline std::wstring console_title(bool debug_shell = false)
{
    const std::string version = config::LoaderConfig::loaderVersion;
    const std::string commit = CESIUM_BUILD_COMMIT;
    return L"CesiumLoader v" + std::wstring(version.begin(), version.end()) + L" (" +
        std::wstring(commit.begin(), commit.end()) + L")" + (debug_shell ? L" Debug Shell" : L" Console");
}
}
