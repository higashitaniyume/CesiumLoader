using System;
using CesiumLoader.SDK.Logging;
using FairyGUI;
using GameLogic;
using Tools;
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>左侧所有玩家头像昵称上方的主动技能冷却，数据来自各自的技能冷却消息。</summary>
    internal sealed class SkillCooldownOverlay
    {
        private readonly SkillCooldownTracker _cooldowns = new SkillCooldownTracker();
        private UI.UIBattleInfoPanel _panel;
        private readonly GTextField[] _labels = new GTextField[4];
        private readonly GTextField[] _names = new GTextField[4];
        private readonly int[] _lastShown = { -1, -1, -1, -1 };
        private readonly int[] _lastTotal = { -1, -1, -1, -1 };
        private bool _enabled = true;
        private bool _started;
        private bool _warned;

        public void Start()
        {
            if (_started) return;
            _cooldowns.Start();
            Timers.inst.Add(0.1f, 0, Refresh);
            _started = true;
        }

        public void SetEnabled(bool enabled) { _enabled = enabled; }

        /// <summary>把每条 CD 数据与最终显示值写进日志(排查显示问题用)。</summary>
        public void SetLogDetail(bool enabled) { _cooldowns.LogDetail = enabled; }

        /// <summary>下一次刷新时向服务器查询所有人的冷却(出牌/释放技能后调用)。</summary>
        public void RequestQueryAll() { _cooldowns.RequestQueryAll(); }

        public void Dispose()
        {
            if (_started) Timers.inst.Remove(Refresh);
            _started = false;
            _cooldowns.Dispose();
            Reset();
        }

        private void Reset()
        {
            for (int i = 0; i < _labels.Length; i++)
            {
                if (_labels[i] != null && !_labels[i].isDisposed) _labels[i].Dispose();
                _labels[i] = null;
                _names[i] = null;
            }
            _panel = null;
        }

        private void HideAll()
        {
            foreach (var label in _labels)
                if (label != null && !label.isDisposed) label.visible = false;
        }

        private void Refresh(object unused)
        {
            try
            {
                _cooldowns.Poll();
                if (!_enabled) { HideAll(); return; }
                var gm = SimpleSingletonProvider<GameLogicManager>.inst;
                if (gm == null || gm.battle == null)
                {
                    Reset();
                    return;
                }
                if (_panel == null || _panel.isDisposed || _panel.parent == null)
                {
                    Reset();
                    _panel = FindPanel(GRoot.inst, 0);
                }
                if (_panel == null) return;
                var container = _panel.com_BattlePlayer.com_Container;
                var slots = new[] { container.com_Player_1, container.com_Player_2,
                    container.com_Player_3, container.com_Player_4 };
                for (int i = 0; i < slots.Length; i++)
                {
                    try { RefreshSlot(i, slots[i]); }
                    catch (Exception e)
                    {
                        if (_labels[i] != null && !_labels[i].isDisposed) _labels[i].visible = false;
                        WarnOnce(e);
                    }
                }
            }
            catch (Exception e) { HideAll(); WarnOnce(e); }
        }

        private void WarnOnce(Exception e)
        {
            if (_warned) return;
            _warned = true;
            SdkLog.Warn("CombatOdds", "技能 CD 显示失败: " + e.Message);
        }

        private void RefreshSlot(int index, UI.UIBattleInfo_Button_PlayerInfo slot)
        {
            var player = slot != null ? slot.PlayerData : null;
            var anchor = slot != null ? (slot.com_player as UI.UICom_PlayerInfo)?.txt_PlayerName : null;
            // 先隐藏，空席位、角色切换或无技能配置时不残留上一个玩家的数字。
            var label = _labels[index];
            if (label != null && !label.isDisposed) label.visible = false;
            if (player == null || player.player == null || player.Property == null ||
                anchor == null || anchor.isDisposed || anchor.parent == null) return;

            int skillId = player.player.GetBattleActiveSkillId();
            SkillInfoConfigure skill;
            if (skillId == 0 || StaticConfigure.Skill == null ||
                !StaticConfigure.Skill.InfoDict.TryGetValue(skillId, out skill)) return;
            if (label == null || label.isDisposed || !ReferenceEquals(anchor, _names[index]))
            {
                if (label != null && !label.isDisposed) label.Dispose();
                _names[index] = anchor;
                label = new GTextField { name = "cesium-skill-cooldown", touchable = false,
                    autoSize = AutoSizeType.Both };
                var format = new TextFormat();
                format.font = anchor.textFormat.font;
                format.size = Math.Max(20, anchor.textFormat.size);
                format.bold = true;
                format.outline = 2;
                format.outlineColor = Color.black;
                label.textFormat = format;
                anchor.parent.AddChild(label);
                _labels[index] = label;
            }
            // 冷却只认服务器查询的结果: 没有查到过就宁可不显示, 也不显示一个错误的数字。
            int remaining;
            if (!_cooldowns.TryGetRemaining(player.player.Id, out remaining)) return;
            // 决赛轮服务器会给所有玩家推 98, 那不是真实剩余冷却, 不显示。
            if (!SkillCooldownRules.IsPlausible(remaining)) return;
            remaining = Math.Max(0, remaining);
            // 分母优先取该玩家这次实际施加的冷却上限(强化/天赋/遗物会让它和配表不同), 没有时用配表值。
            int total = skill.Round;
            if (_cooldowns.TryGetCap(player.player.Id, skillId, out int trackedCap)) total = trackedCap;
            // 服务器给的剩余不可能超过上限; 万一上限还没学到, 以剩余为准, 避免显示 "3/2"。
            if (total < remaining) total = remaining;
            if (_lastShown[index] != remaining || _lastTotal[index] != total)
            {
                _lastShown[index] = remaining;
                _lastTotal[index] = total;
                _cooldowns.Log("CD显示 slot=" + index + " player=" + player.player.Id + " skill=" + skillId +
                    " value=" + remaining + "/" + total);
            }
            label.color = remaining == 0 ? new Color32(0x5B, 0xE3, 0x7A, 0xFF) : new Color32(0xFF, 0xD2, 0x4A, 0xFF);
            label.text = SkillCooldownText.Format(remaining, total);
            // 相比原位置下移 8 像素，靠近昵称但保留原昵称不变。
            label.SetXY(anchor.x + (anchor.width - label.width) * 0.5f, anchor.y - label.height + 5f);
            label.visible = _panel.visible && slot.visible && anchor.visible;
            anchor.parent.SetChildIndex(label, anchor.parent.numChildren - 1);
        }

        private static UI.UIBattleInfoPanel FindPanel(GObject node, int depth)
        {
            if (node == null || depth > 12) return null;
            if (node is UI.UIBattleInfoPanel panel) return panel;
            if (node is GComponent component)
                for (int i = 0; i < component.numChildren; i++)
                {
                    var found = FindPanel(component.GetChildAt(i), depth + 1);
                    if (found != null) return found;
                }
            return null;
        }
    }
}
