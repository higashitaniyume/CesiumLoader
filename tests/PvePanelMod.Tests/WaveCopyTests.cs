using System;
using System.IO;
using Xunit;

namespace PvePanelMod.Tests
{
    /// <summary>
    /// <c>WaveModels.cs</c> / <c>WaveSchedule.cs</c> / <c>WaveScheduleCore.cs</c> 是
    /// <c>PveWaveForecastMod</c> 同名文件的副本（只改了命名空间）。波次规则以后一改就得改两处，
    /// 这里把「两边保持一致」固定成测试：谁只改一边，CI 立刻红。
    /// 真正去重（抽到共享工程）留给后续重构，这里只保证不悄悄漂移。
    /// </summary>
    public class WaveCopyTests
    {
        [Theory]
        [InlineData("WaveModels.cs")]
        [InlineData("WaveSchedule.cs")]
        [InlineData("WaveScheduleCore.cs")]
        public void CopiedWaveFilesStayIdenticalToPveWaveForecastMod(string file)
        {
            string root = RepoRoot();
            string forecast = Read(Path.Combine(root, "mods", "PveWaveForecastMod", file));
            string panel = Read(Path.Combine(root, "mods", "PvePanelMod", file));
            Assert.Equal(Normalize(forecast), Normalize(panel));
        }

        /// <summary>去掉文件头的「复制自…」说明，再把命名空间抹平成同一个占位符（副本比原文件多一层 .Waves）。</summary>
        private static string Normalize(string text)
        {
            int from = text.IndexOf("using ", StringComparison.Ordinal);
            if (from > 0) text = text.Substring(from);
            return text.Replace("PveWaveForecastMod", "NS").Replace("PvePanelMod", "NS").Replace("NS.Waves", "NS");
        }

        private static string Read(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到要对比的源文件", path);
            return File.ReadAllText(path);
        }

        /// <summary>从测试程序集所在目录往上找到仓库根（以 CesiumLoader.sln 为标志）。</summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CesiumLoader.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("从 " + AppContext.BaseDirectory + " 往上没找到 CesiumLoader.sln");
        }
    }
}
