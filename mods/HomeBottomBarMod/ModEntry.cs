using System;
using CesiumLoader.SDK.Mods;
using Tools;
using UI;

namespace HomeBottomBarMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new HomeBottomBarController());
        }
    }

    /// <summary>
    /// 主页底栏调整：隐藏公告，设置左移，最右侧增加退出。
    ///
    /// 改动只发生在内存里的 <c>UIPanelConfigure.ShowFunction</c>，不修改游戏文件。
    /// 禁用/移除模组并重启游戏后，底栏恢复原样。
    /// </summary>
    public sealed class HomeBottomBarController : ModBase
    {
        private DateTime _nextCheck;
        private DateTime _nextErrorLog;

        public override string Name { get { return "主页底栏调整"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get { return "隐藏主页底栏的公告按钮，把设置左移一位，并在最右侧增加退出按钮。"; }
        }

        public override void OnInitialize()
        {
            Apply(initial: true);
        }

        public override void OnUpdate()
        {
            // 游戏可能在登录/配置重载时重新解析静态配置，低频补一次即可。
            var now = DateTime.UtcNow;
            if (now < _nextCheck) return;
            _nextCheck = now.AddSeconds(1);
            Apply(initial: false);
        }

        private void Apply(bool initial)
        {
            try
            {
                UIConfigure ui = StaticConfigure.UI;
                if (ui == null) return;

                UIPanelConfigure config;
                if (!ui.PanelDict.TryGetValue((int)UIPanelType.Home, out config) || config == null)
                {
                    return;
                }

                if (initial && HomeBarLayout.IsDesired(config.ShowFunction))
                {
                    Log.Info("主页底栏已是目标排列: " + HomeBarLayout.Describe(config.ShowFunction));
                    return;
                }

                if (HomeBarLayout.IsDesired(config.ShowFunction)) return;

                var desired = HomeBarLayout.Desired(config.ShowFunction);
                var functions = config.ShowFunction;
                functions.Clear();
                for (int i = 0; i < desired.Count; i++)
                {
                    functions.Add(desired[i]);
                }

                Log.Info("主页底栏已调整: " + HomeBarLayout.Describe(functions));
                RefreshIfHome();
            }
            catch (Exception e)
            {
                var now = DateTime.UtcNow;
                if (now >= _nextErrorLog)
                {
                    _nextErrorLog = now.AddSeconds(30);
                    Log.Warn("调整主页底栏失败: " + e.Message);
                }
            }
        }

        /// <summary>如果当前就在主页且底栏已打开，立即重绘，避免等到下一次面板切换。</summary>
        private void RefreshIfHome()
        {
            UIManager manager = SimpleSingletonProvider<UIManager>.inst;
            if (manager == null) return;

            IBasePanel current = manager.currentPanel;
            if (current == null || current.config == null) return;
            if (current.config.PanelType != UIPanelType.Home) return;

            IBasePanel bottom = manager.bottomMenuPanel;
            if (bottom != null && bottom.IsOpen())
            {
                bottom.Refresh();
            }
        }
    }
}
