using System;
using System.Globalization;

namespace CesiumLoader.SDK.Updates
{
    /// <summary>
    /// 一次更新检查的结果（<c>&lt;loader&gt;\update\state.json</c> 的投影）。
    ///
    /// 数据由**原生加载器**写入（见 CesiumLoader 的 <c>update/update.h</c>）：加载器启动后
    /// 在后台线程拉一次版本清单，把结论落到这个文件；SDK 与 mod 只读，不联网。
    /// 因此这里的状态<b>不会</b>因为网络慢而让 mod 卡住，也不会因为 mod 没装而丢掉检查结果。
    /// </summary>
    public sealed class UpdateCheckState
    {
        /// <summary>检查结论：<c>update-available</c> / <c>up-to-date</c> / <c>error</c> / <c>disabled</c>。</summary>
        public string Status = "";

        /// <summary>当前加载器版本（检查时写下的快照）。</summary>
        public string CurrentVersion = "";

        /// <summary>线上最新版本（取自 Release 的 version.json）。</summary>
        public string LatestVersion = "";

        /// <summary>Release tag，例如 <c>modloader-3.9.0</c>。</summary>
        public string ReleaseTag = "";

        /// <summary>Release 发布时间（ISO 8601，UTC；可能为空）。</summary>
        public string ReleaseDate = "";

        /// <summary>更新说明（<c>version.json</c> 里的 <c>releaseNotes</c>，可能为空）。</summary>
        public string ReleaseNotes = "";

        /// <summary>发布页地址（打开发布页 / 手动下载用）。</summary>
        public string ReleaseUrl = "";

        /// <summary>加载器整包 zip 的下载地址。</summary>
        public string DownloadUrl = "";

        /// <summary>整包 zip 的 SHA-256。</summary>
        public string DownloadSha256 = "";

        /// <summary>整包 zip 的字节数（0 = 清单里没写）。</summary>
        public long DownloadSize;

        /// <summary>这次检查走的是哪条通道：<c>direct</c> 或 <c>mirror</c>。</summary>
        public string Source = "";

        /// <summary>失败原因（<see cref="Status"/> 为 <c>error</c> 时有值）。</summary>
        public string Error = "";

        /// <summary>检查时间（Unix 秒，UTC）。</summary>
        public long CheckedAtUnix;

        /// <summary>是否有可用更新（结论是有新版本且确实拿到了版本号）。</summary>
        public bool HasUpdate
        {
            get
            {
                return string.Equals(Status, "update-available", StringComparison.Ordinal) &&
                       !string.IsNullOrEmpty(LatestVersion);
            }
        }

        /// <summary>是否已是最新。</summary>
        public bool IsUpToDate
        {
            get { return string.Equals(Status, "up-to-date", StringComparison.Ordinal); }
        }

        /// <summary>检查是否被配置关掉了。</summary>
        public bool IsDisabled
        {
            get { return string.Equals(Status, "disabled", StringComparison.Ordinal); }
        }

        /// <summary>检查失败（网络/解析），此时 <see cref="Error"/> 是原因。</summary>
        public bool IsError
        {
            get { return string.Equals(Status, "error", StringComparison.Ordinal); }
        }

        /// <summary>检查时间（UTC；时间戳无效时为 <see cref="DateTime.MinValue"/>）。</summary>
        public DateTime CheckedAtUtc
        {
            get
            {
                if (CheckedAtUnix <= 0) return DateTime.MinValue;
                try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(CheckedAtUnix); }
                catch { return DateTime.MinValue; }
            }
        }

        /// <summary>一行摘要，日志/面板标题都能直接用。</summary>
        public string Describe()
        {
            if (IsDisabled) return "更新检查已关闭";
            if (IsError) return "检查失败: " + (string.IsNullOrEmpty(Error) ? "未知错误" : Error);
            if (HasUpdate) return "有新版本 " + LatestVersion + " (当前 " + CurrentVersion + ")";
            if (IsUpToDate) return "已是最新 (" + CurrentVersion + ")";
            return string.IsNullOrEmpty(Status) ? "尚未检查" : "状态: " + Status;
        }

        /// <summary>诊断用的多行描述（写日志时比 <see cref="Describe"/> 有用）。</summary>
        public string DescribeDetailed()
        {
            var text = Describe();
            if (!string.IsNullOrEmpty(ReleaseTag)) text += " · " + ReleaseTag;
            if (!string.IsNullOrEmpty(Source)) text += " · " + Source;
            if (CheckedAtUtc != DateTime.MinValue)
                text += " · 检查于 " + CheckedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            return text;
        }
    }
}
