using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using Core;

namespace LocalServerMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new LocalServerController(), tag: "LocalServer");
        }
    }

    /// <summary>
    /// 开发用：把客户端的连接地址固定指向本地私服。
    /// 只改 GameSettings.IP/Port，不碰协议、不碰存档。
    /// </summary>
    public sealed class LocalServerController : ModBase
    {
        private string _host = "127.0.0.1";
        private int _port = 8800;
        private bool _enabled = true;
        private bool _logged;

        public override string Name => "本地服务器";
        public override string Version => "0.1.0";
        public override string Author => "Hiynet";
        public override string Description => "把客户端连接地址指向本地私服（开发测试用）。";

        public override void OnInitialize()
        {
            ModConfig config = Config;
            if (config != null)
            {
                _enabled = config.GetBool("enabled", true);
                _host = config.GetString("host", "127.0.0.1");
                _port = config.GetInt("port", 8800);
            }

            Log.Info($"本地服务器：enabled={_enabled}, host={_host}, port={_port}");
        }

        public override void OnUpdate()
        {
            if (!_enabled)
            {
                return;
            }

            if (GameSettings.IP == _host && GameSettings.Port == _port)
            {
                return;
            }

            GameSettings.IP = _host;
            GameSettings.Port = _port;
            if (!_logged)
            {
                _logged = true;
                Log.Info($"已把连接地址改为 {_host}:{_port}");
            }
        }
    }
}
