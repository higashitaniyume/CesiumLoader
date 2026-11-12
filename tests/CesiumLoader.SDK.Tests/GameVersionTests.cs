using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using CesiumLoader.SDK.Manifests;
using Xunit;

namespace CesiumLoader.SDK.Tests;

/// <summary>
/// 游戏程序集版本闸门: 换游戏热更程序集时, 必须同步更新 <see cref="GameVersion.CompiledGameAssemblyMvid"/>。
/// </summary>
/// <remarks>
/// 这条断言是"改了 refs/game 却忘了改常量"的最后一道防线 —— 常量错了, 内置 mod 会被整体跳过,
/// 或者更糟: 带着旧协议假设的 mod 在游戏里把整局卡死。
/// </remarks>
public sealed class GameVersionTests
{
    [Fact]
    public void CompiledMvid_MatchesTheGameAssemblyWeBuildAgainst()
    {
        string path = GameAssemblyPath();
        Guid mvid;
        try
        {
            // 直接读 PE 元数据, 不用 Assembly.LoadFile: 往测试进程里塞一份游戏程序集
            // 会污染同进程其它测试(比如按程序集枚举的诊断测试), 偶发失败。
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new InvalidOperationException("没有元数据: " + path);
            MetadataReader reader = pe.GetMetadataReader();
            mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("读不到游戏程序集版本: " + path + " (" + e.Message + ")");
        }

        Assert.Equal(GameVersion.CompiledGameAssemblyMvid, mvid.ToString("D"));
    }

    [Fact]
    public void Marker_IsOnlyUsedForBuildTimeChecks()
    {
        // 运行期不能读程序集身份(HybridCLR 裁剪了 Assembly.ManifestModule), 所以 SDK 里
        // 只保留构建期标记, 不得再出现任何运行期探测入口。
        Assert.False(string.IsNullOrEmpty(GameVersion.CompiledGameAssemblyMvid));
        Assert.Null(typeof(GameVersion).GetProperty("RunningGameAssemblyMvid"));
        Assert.Null(typeof(GameVersion).GetProperty("Matches"));
    }

    private static string GameAssemblyPath()
    {
        string configured = typeof(GameVersionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GameRuntimeAssemblyPath")?.Value;
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

        string fallback = Path.Combine(Repo, "refs", "game", "AstralParty.Runtime.dll");
        if (File.Exists(fallback)) return fallback;
        throw new FileNotFoundException(
            "找不到游戏热更程序集: 期望 refs/game/AstralParty.Runtime.dll (见 refs/game/README.md)");
    }

    private static string Repo
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "refs", "game")))
                dir = dir.Parent;
            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }
}
