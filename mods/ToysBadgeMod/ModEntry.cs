using System;
using CesiumLoader.SDK.Mods;
using UnityEngine;

namespace ToysBadgeMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new ToysBadgeController());
        }
    }

    public sealed class ToysBadgeController : ModBase
    {
        private readonly PresenceSession _session = new PresenceSession();
        private readonly BadgeOverlay _overlay = new BadgeOverlay();
        private volatile ToysBadgeConfig _config = new ToysBadgeConfig();
        private volatile ToysBadgeConfig _appliedConnConfig;
        private volatile bool _configReloaded;

        public override string Name { get { return "工具箱玩家标识"; } }
        public override string Version { get { return "1.0.1"; } }
        public override string Author { get { return "Aperfish"; } }
        public override string Description
        {
            get
            {
                return "对局内自动在同样使用 Astral Party Toys（吉星派对工具箱）的玩家头像左侧显示圆形工具箱图标。";
            }
        }

        public override void OnInitialize()
        {
            ToysBadgeConfig.EnsureDefaults(Config);
            if (Config != null) Config.SaveIfDirty();

            ToysBadgeConfig cfg = ToysBadgeConfig.Read(Config);
            _config = cfg;
            _appliedConnConfig = cfg;

            Log.Info("ToysBadgeMod 已启动: enabled=" + cfg.Enabled + ", url=" + (string.IsNullOrEmpty(cfg.ServiceUrl) ? "(disabled)" : cfg.ServiceUrl));
        }

        public override void OnUpdate()
        {
            ToysBadgeConfig cfg = _config;
            if (_configReloaded)
            {
                _configReloaded = false;
                ToysBadgeConfig prev = _appliedConnConfig;
                if (prev == null || !prev.SameConnectionSettings(cfg))
                {
                    _appliedConnConfig = cfg;
                    _session.OnConnectionSettingsChanged(cfg);
                }
                _overlay.InvalidateTexture();
            }

            float now = Time.realtimeSinceStartup;
            _session.Tick(now, cfg);
            _overlay.Tick(now, cfg, _session);
        }

        public override void OnConfigChanged()
        {
            ToysBadgeConfig next = ToysBadgeConfig.Read(Config);
            _config = next;
            _configReloaded = true;
            Log.Info("ToysBadgeMod 配置已热重载: enabled=" + next.Enabled + ", iconSize=" + next.IconSize);
        }

        public override void OnUnload()
        {
            try { _overlay.Dispose(); }
            catch { }

            try { _session.Shutdown(_config); }
            catch { }
        }
    }
}
