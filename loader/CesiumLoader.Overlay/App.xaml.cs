using System;
using System.Windows;

namespace CesiumLoader.Overlay
{
    public partial class App : Application
    {
        public int TargetPid { get; private set; }
        public int UdpPort { get; private set; }
        public bool DemoMode { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // args: --pid <int> --port <int> [--demo]
            for (int i = 0; i < e.Args.Length; i++)
            {
                string a = e.Args[i];
                if (a == "--pid" && i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int pid)) { TargetPid = pid; i++; }
                else if (a == "--port" && i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int port)) { UdpPort = port; i++; }
                else if (a == "--demo") DemoMode = true;
            }
            // 无参时按 B 方案：WPF 自己开一个 UDP 端口并写文件发现？加载器模式下端口必由 version.dll 注入。
            // 这里直接兜底：若未给 port，则开系统分配的临时端口并写到 exe 旁 port.txt 方便本机调试。
            if (UdpPort <= 0 && !DemoMode)
            {
                try
                {
                    var tmp = new System.Net.Sockets.UdpClient(0);
                    UdpPort = ((System.Net.IPEndPoint)tmp.Client.LocalEndPoint).Port;
                    tmp.Close();
                    string portFile = System.IO.Path.Combine(AppContext.BaseDirectory, "port.txt");
                    System.IO.File.WriteAllText(portFile, UdpPort.ToString());
                }
                catch { UdpPort = 0; }
            }
            base.OnStartup(e);
        }
    }
}
