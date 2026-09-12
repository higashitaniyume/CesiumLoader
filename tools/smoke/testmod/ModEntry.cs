using System;
using System.IO;

namespace TestMod
{
    /// <summary>
    /// Bootstrap 桌面测试用的假 mod: 入口写入标记文件 + 抛一个异常验证
    /// Bootstrap 的容错(异常被捕获并记录, 不中断)。
    /// </summary>
    public static class ModEntry
    {
        public static void Main()
        {
            string marker = Path.Combine(
                Environment.GetEnvironmentVariable("CESIUM_LOG_DIR") ?? ".",
                "testmod-ran.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(marker) ?? ".");
            File.WriteAllText(marker, "TestMod.Main executed at " + DateTime.Now.ToString("O"));

            // 模拟一个入口内部异常, 验证 Bootstrap 捕获且继续
            
        }
    }
}

