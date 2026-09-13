// modmeta_test.cpp - 依赖解析/版本协商单元测试 (纯 C++, 不依赖游戏/il2cpp)
// 编译: cl /nologo /std:c++17 /EHsc /utf-8 modmeta_test.cpp ..\..\src\CesiumLoader\modmeta.cpp /Fe:modmeta_test.exe
//   (/utf-8: 源文件含中文注释, 无此标志在 GBK 代码页下可能解析错位)
#include "../../src/CesiumLoader/modmeta.h"
#include <windows.h>
#include <cstdio>
#include <string>
#include <vector>
#include <map>
#include <filesystem>
#include <fstream>

static int g_fail = 0;
static void check(bool cond, const char* what)
{
    if (!cond) { printf("  [FAIL] %s\n", what); g_fail++; }
    else { printf("  [PASS] %s\n", what); }
}

static std::string join(const std::vector<std::string>& v)
{
    std::string s;
    for (size_t i = 0; i < v.size(); i++)
    {
        if (i) s += ",";
        s += v[i];
    }
    return s;
}

int main()
{
    printf("=== sidecar 解析 ===\n");
    {
        auto m = cesium::parse_sidecar(
            "{\"id\":\"ModB\",\"name\":\"B\",\"version\":\"1.2.0\",\"sdkVersion\":\"2.0.0\","
            "\"dependencies\":[{\"id\":\"ModA\",\"minVersion\":\"1.0.0\"}]}");
        check(m.hasSidecar, "解析成功");
        check(m.id == "ModB", "id=ModB");
        check(m.version == "1.2.0", "version=1.2.0");
        check(m.sdkVersion == "2.0.0", "sdkVersion=2.0.0");
        check(m.deps.size() == 1 && m.deps[0].id == "ModA" && m.deps[0].minVersion == "1.0.0",
            "deps=[ModA>=1.0.0]");
    }
    {
        auto m = cesium::parse_sidecar("");   // 空
        check(!m.hasSidecar, "空 sidecar = 无声明");
    }
    {
        // 旧格式(无 id)回退 name
        auto m = cesium::parse_sidecar("{\"name\":\"LegacyMod\",\"version\":\"1.0.0\"}");
        check(m.hasSidecar && m.id == "LegacyMod", "旧格式 id 回退 name");
    }

    printf("=== semver 比较 ===\n");
    check(cesium::semver_compare("1.0.0", "1.0.0") == 0, "1.0.0 == 1.0.0");
    check(cesium::semver_compare("2.0.0", "1.9.9") > 0, "2.0.0 > 1.9.9");
    check(cesium::semver_compare("1.0.1", "1.0.0") > 0, "1.0.1 > 1.0.0");
    check(cesium::semver_compare("1.1.0", "1.10.0") < 0, "1.1.0 < 1.10.0");
    check(cesium::semver_compare("2.0.0", "2.0.0-beta") == 0, "忽略预发布后缀");

    printf("=== 依赖拓扑排序 ===\n");
    {
        // A 无依赖, B 依赖 A, C 依赖 B → 顺序 A,B,C
        std::vector<std::string> stems = {"C", "A", "B"};
        std::map<std::string, cesium::ModMeta> metas;
        metas["B"] = cesium::parse_sidecar(
            "{\"id\":\"B\",\"version\":\"1.0.0\",\"dependencies\":[{\"id\":\"A\"}]}");
        metas["C"] = cesium::parse_sidecar(
            "{\"id\":\"C\",\"version\":\"1.0.0\",\"dependencies\":[{\"id\":\"B\"}]}");
        std::vector<std::string> rejected;
        auto order = cesium::sort_mods_by_deps(stems, metas, "2.0.0", &rejected);
        check(join(order) == "A,B,C", ("顺序 A,B,C (实际 " + join(order) + ")").c_str());
        check(rejected.empty(), "无拒绝");
    }
    {
        // 缺失依赖: B 依赖 Missing → B 被拒, A 保留
        std::vector<std::string> stems = {"A", "B"};
        std::map<std::string, cesium::ModMeta> metas;
        metas["B"] = cesium::parse_sidecar(
            "{\"id\":\"B\",\"version\":\"1.0.0\",\"dependencies\":[{\"id\":\"Missing\"}]}");
        std::vector<std::string> rejected;
        auto order = cesium::sort_mods_by_deps(stems, metas, "2.0.0", &rejected);
        check(join(order) == "A", ("A 保留 (实际 " + join(order) + ")").c_str());
        check(rejected.size() == 1 && rejected[0] == "B", "B 被拒");
    }
    {
        // 循环依赖: A↔B → 两者都被拒
        std::vector<std::string> stems = {"A", "B"};
        std::map<std::string, cesium::ModMeta> metas;
        metas["A"] = cesium::parse_sidecar(
            "{\"id\":\"A\",\"version\":\"1.0.0\",\"dependencies\":[{\"id\":\"B\"}]}");
        metas["B"] = cesium::parse_sidecar(
            "{\"id\":\"B\",\"version\":\"1.0.0\",\"dependencies\":[{\"id\":\"A\"}]}");
        std::vector<std::string> rejected;
        auto order = cesium::sort_mods_by_deps(stems, metas, "2.0.0", &rejected);
        check(order.empty(), "循环依赖全部拒绝");
    }
    {
        // SDK 版本协商: mod 要求 3.0.0 > 当前 2.0.0 → 拒绝
        std::vector<std::string> stems = {"NewMod"};
        std::map<std::string, cesium::ModMeta> metas;
        metas["NewMod"] = cesium::parse_sidecar(
            "{\"id\":\"NewMod\",\"version\":\"1.0.0\",\"sdkVersion\":\"3.0.0\"}");
        std::vector<std::string> rejected;
        auto order = cesium::sort_mods_by_deps(stems, metas, "2.0.0", &rejected);
        check(order.empty(), "SDK 版本不符拒绝");
        check(rejected.size() == 1 && rejected[0] == "NewMod", "NewMod 被拒");
    }
    {
        // 依赖版本过低: B 要求 A>=2.0, 但 A 是 1.0 → B 拒绝
        std::vector<std::string> stems = {"A", "B"};
        std::map<std::string, cesium::ModMeta> metas;
        metas["A"] = cesium::parse_sidecar("{\"id\":\"A\",\"version\":\"1.0.0\"}");
        metas["B"] = cesium::parse_sidecar(
            "{\"id\":\"B\",\"version\":\"1.0.0\",\"dependencies\":[{\"id\":\"A\",\"minVersion\":\"2.0.0\"}]}");
        std::vector<std::string> rejected;
        auto order = cesium::sort_mods_by_deps(stems, metas, "2.0.0", &rejected);
        check(join(order) == "A", ("A 保留 (实际 " + join(order) + ")").c_str());
        check(rejected.size() == 1 && rejected[0] == "B", "B 版本过低被拒");
    }
    {
        // 无 sidecar 的 mod 照常加载
        std::vector<std::string> stems = {"Legacy"};
        std::map<std::string, cesium::ModMeta> metas;
        auto order = cesium::sort_mods_by_deps(stems, metas, "2.0.0", nullptr);
        check(join(order) == "Legacy", "无声明 mod 照常加载");
    }

    printf("=== enabled 开关 ===\n");
    {
        // enabled=false → 不加载; 其余照常
        std::vector<std::string> stems = {"A", "Off"};
        std::map<std::string, cesium::ModMeta> metas;
        metas["Off"] = cesium::parse_sidecar(
            "{\"id\":\"Off\",\"version\":\"1.0.0\",\"enabled\":false}");
        std::vector<std::string> rejected;
        auto order = cesium::sort_mods_by_deps(stems, metas, "2.0.0", &rejected);
        check(join(order) == "A", ("禁用 Off 后只剩 A (实际 " + join(order) + ")").c_str());
        check(rejected.empty(), "禁用不算拒绝");
    }
    {
        // 缺省 enabled → 默认 true 加载
        auto m = cesium::parse_sidecar("{\"id\":\"X\",\"version\":\"1.0.0\"}");
        check(m.enabled, "缺省 enabled = true");
    }
    {
        // enabled=true 显式 → 加载
        auto m = cesium::parse_sidecar("{\"id\":\"Y\",\"version\":\"1.0.0\",\"enabled\":true}");
        check(m.enabled, "enabled=true 解析");
    }
    {
        // 禁用不破坏依赖: B 依赖 Off, Off 禁用 → B 仍加载(Off 只是不运行)
        std::vector<std::string> stems = {"B", "Off"};
        std::map<std::string, cesium::ModMeta> metas;
        metas["Off"] = cesium::parse_sidecar(
            "{\"id\":\"Off\",\"version\":\"1.0.0\",\"enabled\":false}");
        metas["B"] = cesium::parse_sidecar(
            "{\"id\":\"B\",\"version\":\"1.0.0\",\"dependencies\":[{\"id\":\"Off\"}]}");
        std::vector<std::string> rejected;
        auto order = cesium::sort_mods_by_deps(stems, metas, "2.0.0", &rejected);
        check(join(order) == "B", ("禁用 Off 后 B 仍加载 (实际 " + join(order) + ")").c_str());
    }

    printf("=== permissions 声明(仅展示/警告, 不门控) ===\n");
    {
        auto m = cesium::parse_sidecar("{\"id\":\"Act\",\"version\":\"1.0.0\",\"permissions\":2}");
        check(m.permissions == 2, "permissions=2 解析(操作游戏)");
        check(m.enabled, "permissions 不影响 enabled");
    }
    {
        // 声明读对局(1) | 操作游戏(2) = 3
        auto m = cesium::parse_sidecar("{\"id\":\"Full\",\"version\":\"1.0.0\",\"permissions\":3}");
        check(m.permissions == 3, "permissions=3 解析(读对局+操作游戏)");
    }
    {
        // 缺省 permissions → 0
        auto m = cesium::parse_sidecar("{\"id\":\"None\",\"version\":\"1.0.0\"}");
        check(m.permissions == 0, "缺省 permissions = 0");
    }
    {
        // 声明操作游戏的 mod 在排序结果里正常加载(不因权限被拒)
        std::vector<std::string> stems = {"Act", "Safe"};
        std::map<std::string, cesium::ModMeta> metas;
        metas["Act"] = cesium::parse_sidecar(
            "{\"id\":\"Act\",\"version\":\"1.0.0\",\"permissions\":2}");
        std::vector<std::string> rejected;
        auto order = cesium::sort_mods_by_deps(stems, metas, "2.0.0", &rejected);
        check(join(order) == "Act,Safe", ("声明操作游戏的 mod 照常加载 (实际 " + join(order) + ")").c_str());
        check(rejected.empty(), "权限声明不产生拒绝");
    }

    printf("=== scan_mods_dir 目录扫描(新布局: 每 mod 一个文件夹) ===\n");
    {
        namespace fs = std::filesystem;
        fs::path root = fs::temp_directory_path() / ("cesium_mods_test_" + std::to_string(GetCurrentProcessId()));
        fs::remove_all(root);
        fs::create_directories(root / "ModA");       // 新布局: 文件夹 mod
        fs::create_directories(root / "ModB");       // 新布局: 文件夹 mod + sidecar
        fs::create_directories(root / "NotAMod");    // 无同名 dll, 应忽略
        {
            std::ofstream(root / "ModA" / "ModA.dll");
            std::ofstream(root / "ModB" / "ModB.dll");
            std::ofstream(root / "ModB" / "ModB.json") << "{\"id\":\"ModB\",\"version\":\"1.0.0\"}";
            std::ofstream(root / "NotAMod" / "readme.txt") << "hi";
            std::ofstream(root / "Legacy.dll");      // 旧布局: 平铺
            std::ofstream(root / "Legacy.json") << "{\"id\":\"Legacy\",\"version\":\"0.5.0\"}";
        }

        auto mods = cesium::scan_mods_dir(root.string());
        check(mods.size() == 3, "扫到 3 个 mod(ModA/ModB/Legacy)");
        if (mods.size() == 3)
        {
            check(mods[0].id == "Legacy" && !mods[0].sidecar.empty(), "排序: Legacy 平铺(带 sidecar)");
            check(mods[1].id == "ModA" && mods[1].sidecar.empty(), "排序: ModA 文件夹(无 sidecar)");
            check(mods[2].id == "ModB" && !mods[2].sidecar.empty(), "排序: ModB 文件夹(带 sidecar)");
            check(mods[1].dll.find("ModA\\ModA.dll") != std::string::npos ||
                  mods[1].dll.find("ModA/ModA.dll") != std::string::npos, "ModA DLL 路径在文件夹内");
        }

        // 不存在的目录 → 空
        check(cesium::scan_mods_dir((root / "missing").string()).empty(), "不存在目录返回空");

        fs::remove_all(root);
    }

    printf("\n%s (%d 失败)\n", g_fail == 0 ? "全部通过" : "有失败", g_fail);
    return g_fail == 0 ? 0 : 1;
}
