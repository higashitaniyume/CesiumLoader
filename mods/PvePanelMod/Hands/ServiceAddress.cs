// 基于 hpenguins/HandViewerMod（github.com/hpenguins/HandViewerMod，AGPL-3.0）的 ServiceAddress.cs 改写，原作者 hpenguins。
// 合并进 PVE 面板后的改动见文件内「改动」注释。
using System;

namespace PvePanelMod.Hands
{
    public static class ServiceAddress
    {
        public static string QueryUrl(string endpoint, string code)
        {
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")
                || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("服务地址必须为 HTTP/HTTPS 地址，不能含账号信息、查询串或 fragment");
            return uri.AbsoluteUri.TrimEnd('/') + "/query?id=" + Uri.EscapeDataString(code ?? "");
        }
    }
}
