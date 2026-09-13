// modmeta.cpp - mod 元数据(sidecar)解析 + 依赖拓扑排序 (见 modmeta.h)

#include "modmeta.h"

#include <fstream>
#include <cstdlib>

namespace cesium
{

namespace
{

// 在 json 中定位 "key" 后的 ':' 值起始位置
size_t jval(const std::string& json, const char* key)
{
    std::string needle = std::string("\"") + key + "\"";
    size_t pos = json.find(needle);
    if (pos == std::string::npos) return std::string::npos;
    pos = json.find(':', pos);
    if (pos == std::string::npos) return std::string::npos;
    pos++;
    while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\r' || json[pos] == '\n'))
        pos++;
    return pos;
}

std::string jstr(const std::string& json, const char* key)
{
    size_t pos = jval(json, key);
    if (pos == std::string::npos || pos >= json.size() || json[pos] != '"') return "";
    pos++;
    std::string out;
    while (pos < json.size() && json[pos] != '"')
    {
        if (json[pos] == '\\' && pos + 1 < json.size())
        {
            char c = json[pos + 1];
            switch (c) { case 'n': out.push_back('\n'); break; case 't': out.push_back('\t'); break; case 'r': out.push_back('\r'); break; default: out.push_back(c); break; }
            pos += 2;
        }
        else { out.push_back(json[pos]); pos++; }
    }
    return out;
}

void jdeps(const std::string& json, const char* key, std::vector<ModDep>& out)
{
    size_t pos = jval(json, key);
    if (pos == std::string::npos || pos >= json.size() || json[pos] != '[') return;
    pos++;  // 跳过 [
    while (pos < json.size())
    {
        size_t closeBracket = json.find(']', pos);
        size_t idq = json.find("\"id\"", pos);
        if (idq == std::string::npos || (closeBracket != std::string::npos && idq > closeBracket)) break;
        size_t colon = json.find(':', idq);
        if (colon == std::string::npos) break;
        size_t v1 = json.find('"', colon);
        if (v1 == std::string::npos) break;
        size_t v2 = json.find('"', v1 + 1);
        if (v2 == std::string::npos) break;
        ModDep dep;
        dep.id = json.substr(v1 + 1, v2 - v1 - 1);
        size_t braceEnd = json.find('}', v2);
        if (braceEnd == std::string::npos) break;
        size_t mq = json.find("\"minVersion\"", v2);
        if (mq != std::string::npos && mq < braceEnd)
        {
            size_t mc = json.find(':', mq);
            size_t m1 = mc == std::string::npos ? std::string::npos : json.find('"', mc);
            size_t m2 = m1 == std::string::npos ? std::string::npos : json.find('"', m1 + 1);
            if (m2 != std::string::npos) dep.minVersion = json.substr(m1 + 1, m2 - m1 - 1);
        }
        out.push_back(dep);
        pos = braceEnd + 1;
    }
}

} // namespace

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
    m.id = jstr(json, "id");
    if (m.id.empty()) m.id = jstr(json, "name");   // 旧格式无 id
    m.name = jstr(json, "name");
    m.version = jstr(json, "version");
    m.sdkVersion = jstr(json, "sdkVersion");
    jdeps(json, "dependencies", m.deps);
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
        result.push_back(name);
    }
    return result;
}

} // namespace cesium
