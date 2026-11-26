using System;
using CesiumLoader.SDK.Mods;
using UnityEngine;

namespace HubBridgeMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new HubBridgeController());
        }
    }

    public sealed class HubBridgeController : ModBase
    {
        private HubBridgeSession _session;
        private volatile HubBridgeConfig _config = new HubBridgeConfig();
        private volatile HubBridgeConfig _appliedConfig;
        private volatile bool _configReloaded;

        public override string Name { get { return "组队大厅联动"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }

        public override string Description
        {
            get
            {
                return "连接 AstralParty.Toys 组队大厅：房主开房自动捕获房间码与密码、队员一键进房、对局结束自动回传即时战报与回放 ID。";
            }
        }

        public override void OnInitialize()
        {
            HubBridgeConfig.EnsureDefaults(Config);
            if (Config != null) Config.SaveIfDirty();

            HubBridgeConfig cfg = HubBridgeConfig.Read(Config);
            _config = cfg;
            _appliedConfig = cfg;

            string modsDir = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR") ?? "";
            _session = new HubBridgeSession(modsDir);
            Log.Info("HubBridgeMod 已启动: enabled=" + cfg.Enabled + ", pollMs=" + cfg.PollIntervalMs);
        }

        public override void OnUpdate()
        {
            if (_session == null) return;

            HubBridgeConfig cfg = _config;
            if (_configReloaded)
            {
                _configReloaded = false;
                HubBridgeConfig prev = _appliedConfig;
                if (prev == null || !prev.SameSettings(cfg))
                {
                    _appliedConfig = cfg;
                    _session.OnSettingsChanged(cfg);
                }
            }

            _session.Tick(Time.realtimeSinceStartup, cfg, Log);
        }

        public override void OnConfigChanged()
        {
            HubBridgeConfig next = HubBridgeConfig.Read(Config);
            _config = next;
            _configReloaded = true;
            Log.Info("HubBridgeMod 配置已热重载: enabled=" + next.Enabled + ", pollMs=" + next.PollIntervalMs);
        }

        public override void OnUnload()
        {
            try { _session?.Dispose(); }
            catch { }
            _session = null;
        }
    }
}
