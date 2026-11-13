using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Runtime;
using FairyGUI;
using UI;

namespace PvePanelMod
{
    /// <summary>
    /// 适配「关键筹码置顶」（PinnedRelicsMod）：它把玩家持有的关键筹码画在官方左栏的星币下面，
    /// 而本面板展开时会把官方左栏藏起来，那排筹码也就跟着看不见了。这里把它正在显示的筹码读出来，画进本面板的角色栏。
    ///
    /// 不复制它的名单和名称表：直接看它画出来的那一行（官方玩家栏里名为 "PinnedRelicsMod.Row" 的组件，
    /// 子元素名为 "Relic" + 筹码 ID），按横坐标排好就是它的显示顺序。这样它的配置（名单、顺序、总开关）改了，这里自动一致；
    /// 它没装时什么都读不到。没有编译期依赖。
    /// </summary>
    internal sealed class PinnedRelics
    {
        private const string RowName = "PinnedRelicsMod.Row";
        private const string IconPrefix = "Relic";
        private Type _controller;
        private DateTime _nextResolve = DateTime.MinValue;

        /// <summary>「关键筹码置顶」有没有被加载器加载（装了才给它在角色栏留位置）。</summary>
        public bool Installed
        {
            get
            {
                if (_controller != null) return true;
                if (DateTime.UtcNow < _nextResolve) return false;
                _nextResolve = DateTime.UtcNow.AddSeconds(5);   // 没装时每 5 秒才再找一次
                try
                {
                    var t = RuntimeAssemblyService.FindTypeBySimpleName("PinnedRelicsController");
                    if (t != null && t.Namespace == "PinnedRelicsMod") _controller = t;
                }
                catch { }
                return _controller != null;
            }
        }

        /// <summary>玩家 ID → 它正在显示的筹码 ID（按显示顺序）。没显示的玩家不在表里。</summary>
        public Dictionary<long, List<int>> Read(UIBattleInfoPanel panel)
        {
            var map = new Dictionary<long, List<int>>();
            var container = panel?.com_BattlePlayer?.com_Container;
            if (container == null) return map;
            Add(map, container.com_Player_1);
            Add(map, container.com_Player_2);
            Add(map, container.com_Player_3);
            Add(map, container.com_Player_4);
            return map;
        }

        private static void Add(Dictionary<long, List<int>> map, UIBattleInfo_Button_PlayerInfo button)
        {
            try
            {
                long id = button?.PlayerData?.player?.Id ?? 0;
                var info = button?.com_player as GComponent;
                if (id == 0 || info == null || info.isDisposed) return;
                var row = info.GetChild(RowName) as GComponent;
                if (row == null || row.isDisposed || !row.visible) return;
                var shown = new List<KeyValuePair<float, int>>();
                for (int i = 0; i < row.numChildren; i++)
                {
                    var c = row.GetChildAt(i);
                    int relicId;
                    if (c == null || c.isDisposed || !c.visible || c.name == null || !c.name.StartsWith(IconPrefix, StringComparison.Ordinal)) continue;
                    if (!int.TryParse(c.name.Substring(IconPrefix.Length), out relicId)) continue;
                    shown.Add(new KeyValuePair<float, int>(c.x, relicId));
                }
                if (shown.Count == 0) return;
                shown.Sort((a, b) => a.Key.CompareTo(b.Key));
                var ids = new List<int>(shown.Count);
                foreach (var s in shown) ids.Add(s.Value);
                map[id] = ids;
            }
            catch { }
        }
    }
}
