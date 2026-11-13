// 基于 hpenguins/HandViewerMod（github.com/hpenguins/HandViewerMod，AGPL-3.0）的 ServiceAddress.cs 改写，原作者 hpenguins。
// 合并进 PVE 面板后的改动见文件内「改动」注释。
using System;

namespace PvePanelMod.Hands
{
    public static class ServiceAddress
    {
        /// <summary>
        /// 改动：把校验单独拆出来。地址写错时在配置阶段就能说明原因，而不是每次请求都抛一次、日志只写「网络或响应数据不可用」。
        /// 返回 null = 可用；否则是能直接显示给用户的原因。
        /// </summary>
        public static string Validate(string endpoint)
        {
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri)) return "服务地址不合法：需要完整网址";
            if (uri.Scheme != "http" && uri.Scheme != "https") return "服务地址不合法：只支持 http / https";
            if (string.IsNullOrEmpty(uri.Host)) return "服务地址不合法：缺少主机名";
            if (!string.IsNullOrEmpty(uri.UserInfo)) return "服务地址不合法：不能带账号信息";
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return "服务地址不合法：不能带查询串或 #";
            return null;
        }

        public static string QueryUrl(string endpoint, string code)
        {
            string problem = Validate(endpoint);
            if (problem != null) throw new ArgumentException(problem);
            return new Uri(endpoint, UriKind.Absolute).AbsoluteUri.TrimEnd('/') + "/query?id=" + Uri.EscapeDataString(code ?? "");
        }
    }
}
