// ConfigParseTest - 验证 config.cpp 的 doorstop_config.json 极简解析器
// 编译: cl /EHsc /std:c++17 /I src\CesiumLoader ConfigParseTest.cpp src\CesiumLoader\config.cpp src\CesiumLoader\console.cpp
// (需要 loader.cpp 的符号, 但 config.cpp 只依赖 log_line —— 用最小桩代替)
#include <cstdio>
#include <string>
#include <windows.h>

// ---- loader.h 所需符号的最小桩 ----
#include <io.h>
#include <fcntl.h>

static HANDLE g_console_handle = INVALID_HANDLE_VALUE;
void console_write(const char*) {}
void console_write_w(const wchar_t*, size_t) {}
void log_line(const char*) {}
void log_line(const std::string&) {}
void log_line(const std::wstring&) {}
std::wstring loader_root() { return L"."; }
std::wstring bootstrap_dir() { return L"."; }
std::wstring mods_dir() { return L"."; }
std::wstring sdk_dir() { return L"."; }
std::wstring logs_dir() { return L"."; }
std::wstring config_path() { return L"."; }
void maybe_start_boot() {}
void* real_version_handle() { return nullptr; }
void* real_version_fn(const char*) { return nullptr; }
bool boot_il2cpp_and_load_mods() { return false; }

#include "config.h"

static int g_fail = 0;
static void check(bool ok, const char* what) {
    printf("%s %s\n", ok ? "[PASS]" : "[FAIL]", what);
    if (!ok) g_fail++;
}

int main()
{
    // 测试 1: 完整配置
    {
        LoaderConfig c = load_config(L"test_full.json");
        check(c.enabled == true, "full: enabled");
        check(c.bootstrapAssembly == "CesiumLoader.Bootstrap.dll", "full: bootstrapAssembly");
        check(c.bootstrapType == "CesiumLoader.Bootstrap.Bootstrap", "full: bootstrapType");
        check(c.bootstrapMethod == "Main", "full: bootstrapMethod");
        check(c.gameAssemblyTimeoutSec == 60, "full: gameAssemblyTimeoutSec");
        check(c.domainTimeoutSec == 30, "full: domainTimeoutSec");
        check(c.hybridclrTimeoutSec == 60, "full: hybridclrTimeoutSec");
        check(c.consoleEnabled == true, "full: consoleEnabled");
        check(c.consoleTopmost == true, "full: consoleTopmost");
        check(c.forwardActivityLog == true, "full: forwardActivityLog");
    }
    // 测试 2: 缺失文件 → 全默认
    {
        LoaderConfig c = load_config(L"nonexistent.json");
        check(c.enabled == true, "missing: enabled default true");
        check(c.gameAssemblyTimeoutSec == 60, "missing: timeout default 60");
        check(c.consoleEnabled == true, "missing: console default true");
    }
    // 测试 3: disabled 配置
    {
        LoaderConfig c = load_config(L"test_disabled.json");
        check(c.enabled == false, "disabled: enabled=false");
        check(c.consoleEnabled == false, "disabled: consoleEnabled=false");
        check(c.forwardActivityLog == false, "disabled: forwardActivityLog=false");
    }
    // 测试 4: 损坏 JSON → 默认值不崩溃
    {
        LoaderConfig c = load_config(L"test_broken.json");
        check(c.enabled == true, "broken: enabled default");
        check(c.bootstrapAssembly == "CesiumLoader.Bootstrap.dll", "broken: assembly default");
    }
    printf(g_fail == 0 ? "全部通过\n" : "%d 项失败\n", g_fail);
    return g_fail == 0 ? 0 : 1;
}
