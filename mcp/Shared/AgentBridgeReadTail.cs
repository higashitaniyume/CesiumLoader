using System;
using System.IO;
using System.Text;

namespace AstralParty.Agent
{
    /// <summary>
    /// <see cref="AgentBridgeLayout"/> 的 **server-only** 分片: 只被
    /// <c>mcp\AstralParty.Mcp.Core</c>(net8.0) 编进去, 游戏内的 AgentMod 不包含本文件。
    ///
    /// 为什么单独拆一个文件: 热更程序集(HybridCLR/IL2CPP)的 BCL 是残缺的。真机实测
    /// <c>FileStream.Flush(true)</c> 直接抛 <c>MethodNotFind System.IO.FileStream::Flush</c>,
    /// 让整个 state.json 一条都写不出来 —— 而这类缺失只有在游戏里跑到那一行才会暴露。
    /// 既然这里用的 <c>FileStream.Seek</c> / <c>Encoding.UTF8.GetString</c> 没在游戏内验证过,
    /// 就让 mod 侧"看不到也调不到"它, 把风险挡在编译期而不是运行期。
    ///
    /// 规则(由 <c>tests\AstralParty.AgentMod.Tests\BclCompatibilityTests</c> 把关):
    /// **凡是编进 mod 的源码, 只用游戏内已被证明可用的 BCL 成员**(见 AGENTS.md §12.1)。
    /// </summary>
    public static partial class AgentBridgeLayout
    {
        /// <summary>
        /// 读文本文件的**末尾**最多 maxBytes 字节(jsonl 追加流的读取口)。
        /// 从第一个换行之后开始返回, 保证不会给出半行 JSON。
        /// </summary>
        public static string ReadTail(string path, int maxBytes)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length == 0) return string.Empty;
                    long start = fs.Length > maxBytes ? fs.Length - maxBytes : 0;
                    bool truncated = start > 0;
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    string text = Encoding.UTF8.GetString(buf, 0, read);
                    if (truncated)
                    {
                        int nl = text.IndexOf('\n');
                        text = nl >= 0 ? text.Substring(nl + 1) : string.Empty;
                    }
                    return text;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
