// modmeta.cpp - mod 元数据(sidecar)解析 + 依赖拓扑排序 (见 modmeta.h)

#include "modmeta.h"

#include "jsonc.h"   // JSONC 读取(nlohmann/json 封装: 允许注释、剥离 BOM、失败不抛异常)

#include <fstream>
#include <cstdlib>

namespace cesium
{

// 解析实现见下方 parse_sidecar —— 旧的"在 JSON 文本里按 key 找冒号"的手写扫描器
// (jval / jstr / jdeps / jbool_opt / jint_opt) 已删除, 改用 nlohmann/json(见 jsonc.h)。

std::string read_sidecar_text(const std::string& path)
{
    std::ifstream in(path, std::ios::binary);
    if (!in) return "";
    return std::string((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

ModMeta parse_sidecar(const std::string& json)
{
    ModMeta m;
    if (json.empty()) return m;
    m.hasSidecar = true;

    // 解析一次(JSONC: 允许注释; 失败得到 discarded, 不抛异常, 各字段退回默认值)。
    // 与旧实现一致: 只要 json 非空就置 hasSidecar = true —— 损坏的 sidecar 仍算"有声明"。
    const jsonc::Json j = jsonc::parse(json);

    m.id = jsonc::str(j, "id");
    if (m.id.empty()) m.id = jsonc::str(j, "name");   // 旧格式无 id
    m.name = jsonc::str(j, "name");
    m.version = jsonc::str(j, "version");
    m.sdkVersion = jsonc::str(j, "sdkVersion");

    // enabled: 只在键**存在**时覆盖默认 true。旧 jbool_opt 的语义是"键在但值不是 true → false",
    // 因此这里 def 传 false(非布尔值也落到 false), 与旧行为一致。
    bool enabledPresent = false;
    const bool enabled = jsonc::boolean_present(j, "enabled", enabledPresent, /*def=*/false);
    if (enabledPresent) m.enabled = enabled;

    // permissions: 缺失 → 0(无声明); 字符串形式的数字也接受(兼容手改过的清单)。
    m.permissions = static_cast<int>(jsonc::integer(j, "permissions", 0));

    // dependencies: [{ "id": "...", "minVersion": "..." }]
    // 与旧扫描器的差异(仅影响畸形输入): 旧实现在遇到第一个不含 "id" 的项就停止解析,
    // 这里改为跳过没有非空 id 的项、继续解析后续项。
    for (const auto& d : jsonc::array(j, "dependencies"))
    {
        if (!d.is_object()) continue;
        ModDep dep;
        dep.id = jsonc::str(d, "id");
        dep.minVersion = jsonc::str(d, "minVersion");
        if (!dep.id.empty()) m.deps.push_back(std::move(dep));
    }
    return m;
}

int semver_compare(const std::string& a, const std::string& b)
{
    auto parse3 = [](const std::string& s, int out[3])
    {
        for (int i = 0; i < 3; i++) out[i] = 0;
        size_t start = 0, idx = 0;
        while (idx < 3 && start < s.size())
        {
            size_t dot = s.find('.', start);
            std::string part = s.substr(start, dot == std::string::npos ? std::string::npos : dot - start);
            if (!part.empty()) out[idx] = atoi(part.c_str());
            if (dot == std::string::npos) break;
            start = dot + 1;
            idx++;
        }
    };
    int pa[3], pb[3];
    parse3(a, pa); parse3(b, pb);
    for (int i = 0; i < 3; i++)
        if (pa[i] != pb[i]) return pa[i] > pb[i] ? 1 : -1;
    return 0;
}

std::map<std::string, ModMeta> read_mods_meta(const std::string& mods_dir)
{
    std::map<std::string, ModMeta> result;
    // 遍历目录找 *.json 且对应 *.dll 存在
    std::string pattern = mods_dir + "/*.json";
    // 用 <filesystem> 遍历
    std::error_code ec;
    for (auto& entry : std::filesystem::directory_iterator(mods_dir, ec))
    {
        if (ec) break;
        if (!entry.is_regular_file()) continue;
        auto p = entry.path();
        if (p.extension() != ".json") continue;
        std::string stem = p.stem().string();
        ModMeta m = parse_sidecar(read_sidecar_text(p.string()));
        if (m.hasSidecar && !m.id.empty()) result[stem] = m;
    }
    return result;
}

std::vector<ModLoc> scan_mods_dir(const std::string& mods_dir)
{
    namespace fs = std::filesystem;
    std::vector<ModLoc> result;
    std::error_code ec;
    if (!fs::exists(mods_dir, ec) || ec) return result;

    auto add = [&](const std::string& id, const fs::path& dll, const fs::path& sidecar) {
        if (id.empty() || !fs::exists(dll)) return;
        for (auto& m : result)
            if (m.id == id) return;   // 去重(新布局优先, 平铺同名跳过)
        ModLoc loc;
        loc.id = id;
        loc.dll = dll.string();
        if (!sidecar.empty() && fs::exists(sidecar)) loc.sidecar = sidecar.string();
        result.push_back(std::move(loc));
    };

    // 新布局: 遍历 mods 下的子目录, 找 {目录名}.dll (+ 可选 {目录名}.json)
    for (auto& entry : fs::directory_iterator(mods_dir, ec))
    {
        if (ec) break;
        if (!entry.is_directory()) continue;
        std::string id = entry.path().filename().string();
        fs::path dll = entry.path() / (id + ".dll");
        if (!fs::exists(dll)) continue;   // 文件夹里没有同名 dll, 不是 mod 文件夹
        fs::path sc = entry.path() / (id + ".json");
        add(id, dll, sc);
    }
    // 旧布局兼容: mods 根下平铺的 .dll (sidecar 在同目录 {id}.json)
    for (auto& entry : fs::directory_iterator(mods_dir, ec))
    {
        if (ec) break;
        if (!entry.is_regular_file()) continue;
        auto p = entry.path();
        if (p.extension() != ".dll") continue;
        std::string id = p.stem().string();
        fs::path sc = p.parent_path() / (id + ".json");
        add(id, p, sc);
    }

    std::sort(result.begin(), result.end(), [](const ModLoc& a, const ModLoc& b) {
        return a.id < b.id;
    });
    return result;
}

std::vector<std::string> sort_mods_by_deps(
    const std::vector<std::string>& dll_stems,
    const std::map<std::string, ModMeta>& metas,
    const std::string& sdk_version,
    std::vector<std::string>* rejected_out)
{
    std::vector<std::string> rejected;
    if (rejected_out) rejected_out->clear();

    std::set<std::string> names(dll_stems.begin(), dll_stems.end());
    std::map<std::string, std::vector<std::string>> adj;
    std::map<std::string, int> indeg;
    for (auto& n : names) indeg[n] = 0;

    for (auto& n : dll_stems)
    {
        auto it = metas.find(n);
        if (it == metas.end() || !it->second.hasSidecar) continue;   // 无声明: 独立
        const ModMeta& m = it->second;

        // SDK 版本协商: mod 要求 > 当前 SDK → 拒绝
        if (!m.sdkVersion.empty() && !sdk_version.empty() &&
            semver_compare(m.sdkVersion, sdk_version) > 0)
        {
            rejected.push_back(n);
            continue;
        }

        bool depFailed = false;
        for (auto& dep : m.deps)
        {
            if (names.count(dep.id) == 0)
            {
                rejected.push_back(n);
                depFailed = true;
                break;
            }
            auto dit = metas.find(dep.id);
            if (dit != metas.end() && dit->second.hasSidecar && !dep.minVersion.empty())
            {
                if (semver_compare(dit->second.version, dep.minVersion) < 0)
                {
                    rejected.push_back(n);
                    depFailed = true;
                    break;
                }
            }
        }
        if (depFailed) continue;

        indeg[n] += (int)m.deps.size();
        for (auto& dep : m.deps)
        {
            adj[dep.id].push_back(n);
        }
    }

    std::set<std::string> rejected_set(rejected.begin(), rejected.end());
    if (rejected_out) *rejected_out = rejected;

    // 已禁用的 mod(sidecar enabled=false): 照常参与依赖图(保证依赖它的 mod
    // 不被误判缺失), 但最终输出时排除 —— 即"不加载但仍在安装集合里"。
    std::set<std::string> disabled_set;
    for (auto& n : dll_stems)
    {
        auto it = metas.find(n);
        if (it != metas.end() && it->second.hasSidecar && !it->second.enabled)
            disabled_set.insert(n);
    }

    // Kahn 拓扑排序, 同级按名字保证确定性
    std::deque<std::string> queue;
    for (auto& kv : indeg)
        if (kv.second == 0 && !rejected_set.count(kv.first)) queue.push_back(kv.first);
    std::sort(queue.begin(), queue.end());

    std::vector<std::string> order;
    while (!queue.empty())
    {
        std::string cur = queue.front(); queue.pop_front();
        order.push_back(cur);
        for (auto& next : adj[cur])
        {
            if (rejected_set.count(next)) continue;
            if (--indeg[next] == 0) queue.push_back(next);
        }
        std::sort(queue.begin(), queue.end());
    }

    // 剩余未排序(循环依赖) → 拒绝
    for (auto& kv : indeg)
    {
        if (kv.second > 0 && !rejected_set.count(kv.first))
        {
            rejected_set.insert(kv.first);
            if (rejected_out) rejected_out->push_back(kv.first);
        }
    }

    std::vector<std::string> result;
    for (auto& name : order)
    {
        if (rejected_set.count(name)) continue;
        if (disabled_set.count(name)) continue;   // 已禁用: 不加载
        result.push_back(name);
    }
    return result;
}

} // namespace cesium
