using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;
using CesiumLoader.SDK.Events;
using CesiumLoader.SDK.Mods;
using FairyGUI;
using GameLogic;
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>在棋盘角色头顶显示实时攻防, 悬停时显示属性说明。</summary>
    internal sealed class PlayerAttrOverlay
    {
        private const float HeadGap = 8f;
        private const int TipSortingOrder = 30000;
        private const int FontSize = 28;
        private const int TipFontSize = 22;
        private static readonly Color AtkColor = new Color32(0x5A, 0xA9, 0xFF, 0xFF);
        private static readonly Color DefColor = new Color32(0x6D, 0xE0, 0xA2, 0xFF);
        private static readonly Color TipBg = new Color(0.05f, 0.05f, 0.08f, 0.94f);
        private static readonly Color TipBorder = new Color(1f, 1f, 1f, 0.3f);

        private sealed class Entry
        {
            public long Id;
            public GComponent Box;
            public GTextField Text;
            public BattlePlayerData Data;
            public Component SpriteOwner;
            public SpriteRenderer SpriteRenderer;
            public bool AnchorWarningLogged;
            /// <summary>上次写入文本的内容 —— 值没变就跳过字符串比较/排版重算。</summary>
            public string LastText;
        }

        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();
        private UI.UIBattleInfoPanel _panel;
        private GComponent _tip;
        private GGraph _tipBg;
        private GTextField _tipText;
        private long _hoverId;
        private bool _polling;
        /// <summary>配置开关(ShowBoardPlayerAttrs)的当前值 —— 每帧回调必须尊重它, 否则会把开关关掉的标签又摆回来。</summary>
        private bool _enabled = true;
        private static PropertyInfo _mousePosition;
        private static bool _mouseProbeDone;

        public void Start()
        {
            if (_polling) return;
            try
            {
                // RaiseLateUpdate 先执行 UpdateService 的全部订阅(包括自由相机)，
                // 然后才触发此事件。标签用本帧最终机位，顺序不依赖 mod 初始化先后。
                UpdateEvents.LateUpdate += Poll;
                _polling = true;
                ModContext.Current?.RegisterCleanup(Stop);
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "玩家攻防悬浮轮询启动失败: " + e.Message); }
        }

        private void Stop()
        {
            UpdateEvents.LateUpdate -= Poll;
            _polling = false;
            foreach (var entry in _entries.Values) RemoveEntry(entry);
            _entries.Clear();
            HideTip();
            try { _tip?.Dispose(); } catch { }
            _tip = null;
        }

        /// <summary>
        /// 同步配置开关(ShowBoardPlayerAttrs)。秒级调用即可 —— 真正的每帧跟随在 <see cref="Poll"/> 里,
        /// 它读的就是这里写下的 <see cref="_enabled"/>。
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) { HideEntries(); HideTip(); }
        }

        public void Update(bool enabled = true)
        {
            _enabled = enabled;
            try
            {
                if (!enabled)
                {
                    HideEntries();
                    HideTip();
                    return;
                }
                _panel = FindPanel();
                if (_panel == null || !_panel.visible || _panel.parent == null || _panel.com_PlayerAttrInfos == null)
                {
                    HideEntries();
                    HideTip();
                    return;
                }

                var active = new HashSet<long>();
                var players = Players.All();
                var camera = Core.Scene.BattleSceneController.inst?.mainCamera;
                if (camera == null) return;

                float rootW = GRoot.inst.width;
                float rootH = GRoot.inst.height;
                var container = _panel.com_PlayerAttrInfos;

                foreach (var player in players)
                {
                    if (player?.player == null || player.characterType == CharacterType.Monster || player.Property == null || player.CharacterInst == null ||
                        player.CharacterInst.characterObject == null || player.CharacterInst.characterAnimator == null ||
                        player.CharacterInst.characterAnimator.IsHide()) continue;

                    long id = player.player.Id;
                    active.Add(id);
                    var entry = EnsureEntry(id);
                    if (entry == null) continue;
                    Vector3 pos;
                    if (!TryProjectSpriteTop(entry, player.CharacterInst.characterAnimator, camera, out pos) || pos.z <= 0f)
                    {
                        entry.Box.visible = false;
                        if (_hoverId == id) HideTip();
                        continue;
                    }
                    pos.y = Screen.height - pos.y;
                    var screenPoint = new Vector2(pos.x, pos.y);
                    var local = container.GlobalToLocal(screenPoint);
                    // 可见范围要在 GRoot 坐标系比较，不能把容器局部坐标与 root 尺寸混用。
                    var rootPoint = GRoot.inst.GlobalToLocal(screenPoint);
                    entry.Box.visible = rootPoint.x >= 0 && rootPoint.y >= 0 && rootPoint.x <= rootW && rootPoint.y <= rootH;

                    // 压在最上层: com_PlayerAttrInfos 是与游戏自己那份头顶名牌(UICom_PlayerAttrInfo)
                    // 共用的容器, 游戏后加的名牌会盖住我们的字。只在真的不在最上层时才重排
                    // (原来是每帧无条件 SetChildIndex, 那是每帧一次子节点重排, 没必要)。
                    if (container.numChildren > 0 && container.GetChildAt(container.numChildren - 1) != entry.Box)
                        container.SetChildIndex(entry.Box, container.numChildren - 1);

                    // 数值: 只有影响显示的东西真的变了才重排文本
                    // (字符串拼接 + textWidth 排版 + 子节点尺寸重算都不便宜, 没变就别做)。
                    // 注意: 显示的是 finalAtk(基础攻击 + 筹码加成), 与原来一致; 加成明细进悬停浮框。
                    int atk = player.Property.ATK != null ? player.Property.ATK.Value : 0;
                    int def = player.Property.DEF != null ? player.Property.DEF.Value : 0;
                    int hp = player.Property.HP != null ? player.Property.HP.Value : 0;

                    RelicAtkBonus.Input input;
                    var bonuses = ModEntry.CurrentAtkBonuses(player, out input);
                    int finalAtk = atk + RelicAtkBonus.Total(bonuses);
                    string text = "[color=#5AA9FF]攻 " + finalAtk + "[/color]   [color=#6DE0A2]防 " + def + "[/color]";

                    // 签名覆盖浮框会显示的全部字段(基础攻/最终攻/防/血), 任一变化就一起重建,
                    // 避免"只帮浮框重建却因为文字没变而跳过"导致的悬停信息过期。
                    string signature = text + "|" + atk + "|" + hp;
                    if (entry.LastText != signature)
                    {
                        entry.LastText = signature;
                        entry.Text.text = text;
                        // 尺寸给 Box(普通 GComponent, 会老实采纳)。不能改成给 GTextField.SetSize:
                        // 本字段 autoSize=Both, GTextField.HandleSizeChanged 在 Both 下直接 return,
                        // 那个 SetSize 是空操作, 宽度/高度都不会按我们给的算。
                        entry.Box.SetSize(Math.Max(90f, entry.Text.textWidth + 14f), Math.Max(30f, entry.Text.textHeight + 8f));

                        entry.Data = player;
                        // 悬停浮框内容直接存进 Box.data(ShowTip 就取它), 不再另存一份, 避免两处状态不一致。
                        entry.Box.data = BuildTip(Players.SafeNick(player) ?? ("P" + id), atk, finalAtk, def, hp, bonuses);

                    }
                    // 文字维持固定字号；只有与实际顶部之间的小间距使用 UI 单位。
                    entry.Box.SetXY(local.x - entry.Box.width * 0.5f, local.y - HeadGap - entry.Box.height);
                }

                var stale = new List<long>();
                foreach (var pair in _entries) if (!active.Contains(pair.Key)) stale.Add(pair.Key);
                foreach (long id in stale) Hide(id);
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "更新棋盘玩家攻防显示失败: " + e.Message); }
        }

        private static bool TryProjectSpriteTop(Entry entry, Component animator, Camera camera, out Vector3 screen)
        {
            screen = Vector3.zero;
            // 把 Unity 调用放在独立方法里：HybridCLR 在解析未支持的引擎调用时，
            // 异常可能发生在方法入口，外层仍能捕获，避免一名角色影响整层标签。
            try { return ProjectSpriteTop(entry, animator, camera, out screen); }
            catch (Exception e)
            {
                if (!entry.AnchorWarningLogged)
                {
                    entry.AnchorWarningLogged = true;
                    SdkLog.Warn("CombatOdds", "角色 " + entry.Id + " 顶部锚点读取失败: " + e.Message);
                }
                return false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ProjectSpriteTop(Entry entry, Component animator, Camera camera, out Vector3 screen)
        {
            screen = Vector3.zero;
            if (entry.SpriteOwner != animator || entry.SpriteRenderer == null)
            {
                entry.SpriteOwner = animator;
                entry.SpriteRenderer = animator.GetComponent(typeof(SpriteRenderer)) as SpriteRenderer;
            }
            var renderer = entry.SpriteRenderer;
            if (renderer == null || renderer.sprite == null) return false;
            var bounds = renderer.sprite.bounds;
            if (bounds.size.y <= 0f) return false;
            // sprite.bounds 在图像本地坐标里，包含 pivot 偏移。
            // flipY 时显示的顶部来自原图下边缘，flipX 时中心 x 也要镜像。
            var top = new Vector3(renderer.flipX ? -bounds.center.x : bounds.center.x,
                renderer.flipY ? -bounds.min.y : bounds.max.y, bounds.center.z);
            var world = renderer.transform.TransformPoint(top);
            screen = camera.WorldToScreenPoint(world);
            return true;
        }

        private static string BuildTip(string name, int baseAtk, int finalAtk, int def, int hp, List<RelicAtkBonus.Bonus> bonuses)
        {
            var text = "[b]" + name + "[/b]\n[color=#5AA9FF]攻击 " + finalAtk + "[/color]";
            if (bonuses != null && bonuses.Count > 0)
            {
                text += "\n[color=#D9E2F2]基础攻击 " + baseAtk + "[/color]";
                for (int i = 0; i < bonuses.Count; i++)
                {
                    var bonus = bonuses[i];
                    string color = bonus.Color == RelicAtkBonus.Tint.Green ? "#6DE0A2" : "#FFD24A";
                    text += "  [color=" + color + "]+" + bonus.Value + " " + bonus.Name + "[/color]";
                }
            }
            text += "\n[color=#6DE0A2]防御 " + def + "[/color]   [color=#FFCF66]生命 " + hp + "[/color]";
            return text;
        }

        private Entry EnsureEntry(long id)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.Box != null && entry.Box.parent == _panel.com_PlayerAttrInfos)
                return entry;
            if (entry != null) RemoveEntry(entry);
            try
            {
                var box = new GComponent { opaque = true, touchable = true };
                box.onRollOver.Add(OnRollOver);
                box.onRollOut.Add(OnRollOut);
                var text = new GTextField { autoSize = AutoSizeType.Both, touchable = false, UBBEnabled = true };
                text.textFormat = new TextFormat { size = FontSize, bold = true, color = Color.white, outline = 2f, outlineColor = Color.black };
                box.AddChild(text);
                _panel.com_PlayerAttrInfos.AddChild(box);
                entry = new Entry { Id = id, Box = box, Text = text };
                _entries[id] = entry;
                return entry;
            }
            catch (Exception e) { SdkLog.Warn("CombatOdds", "创建棋盘玩家攻防标签失败: " + e.Message); return null; }
        }

        private void Poll()
        {
            try
            {
                // 尊重配置开关: Update 每帧跑, 关掉开关时只做隐藏(仍会摆位置, 但 visible=false),
                // 绝不能在开关关掉时把标签又显示出来。
                Update(_enabled);
                if (!_enabled) { HideTip(); return; }

                var point = Pointer();
                Entry hit = null;
                foreach (var entry in _entries.Values)
                {
                    if (entry.Box == null || !entry.Box.visible) continue;
                    var p = entry.Box.LocalToGlobal(Vector2.zero);
                    if (point.x >= p.x && point.x <= p.x + entry.Box.width && point.y >= p.y && point.y <= p.y + entry.Box.height)
                    { hit = entry; break; }
                }
                if (hit != null) ShowTip(hit);
                else HideTip();
            }
            catch { }
        }

        private static Vector2 Pointer()
        {
            try
            {
                if (!_mouseProbeDone)
                {
                    _mouseProbeDone = true;
                    var type = RuntimeAssemblyService.FindType("UnityEngine.Input") ?? RuntimeAssemblyService.FindTypeBySimpleName("Input");
                    _mousePosition = type?.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static);
                }
                var raw = _mousePosition?.GetValue(null);
                if (raw is Vector3 p)
                {
                    var point = new Vector2(p.x, Screen.height - p.y);
                    float scale = GRoot.contentScaleFactor;
                    if (scale > 0 && (scale < 0.999f || scale > 1.001f)) { point.x /= scale; point.y /= scale; }
                    return point;
                }
            }
            catch { }
            return Stage.inst.touchPosition;
        }

        private void ShowTip(Entry entry)
        {
            string value = entry?.Box?.data as string;
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                EnsureTip();
                if (_tip == null) return;
                _tipText.text = value;
                float w = _tipText.textWidth + 20f;
                float h = _tipText.textHeight + 16f;
                _tipBg.DrawRect(w, h, 1, TipBorder, TipBg);
                _tip.SetSize(w, h);
                _tipText.SetXY(10, 8);
                var root = GRoot.inst;
                var owner = root.GlobalToLocal(entry.Box.LocalToGlobal(Vector2.zero));
                float x = Math.Min(Math.Max(4, owner.x), root.width - w - 4);
                float y = owner.y - h - 6;
                if (y < 4) y = Math.Min(root.height - h - 4, owner.y + entry.Box.height + 6);
                _tip.SetXY(x, y);
                _tip.visible = true;
                _tip.sortingOrder = TipSortingOrder;
                root.SetChildIndex(_tip, root.numChildren - 1);
                _hoverId = entry.Id;
            }
            catch { }
        }

        private void EnsureTip()
        {
            if (_tip != null && _tip.parent != null) return;
            _tip = new GComponent { touchable = false };
            _tipBg = new GGraph();
            _tipText = new GTextField { autoSize = AutoSizeType.Both, touchable = false, UBBEnabled = true };
            _tipText.textFormat = new TextFormat { size = TipFontSize, color = Color.white };
            _tip.AddChild(_tipBg);
            _tip.AddChild(_tipText);
            GRoot.inst.AddChild(_tip);
            _tip.sortingOrder = TipSortingOrder;
            _tip.visible = false;
        }

        private void OnRollOver(EventContext context)
        {
            if (context?.sender is GComponent box && box.data is string)
                foreach (var entry in _entries.Values) if (entry.Box == box) { ShowTip(entry); break; }
        }

        private void OnRollOut(EventContext _) { HideTip(); }

        private void Hide(long id)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                if (_hoverId == id) HideTip();
                RemoveEntry(entry);
                _entries.Remove(id);
            }
        }

        private static void RemoveEntry(Entry entry)
        {
            try { if (entry?.Box?.parent != null) entry.Box.parent.RemoveChild(entry.Box, true); } catch { }
        }

        private void HideEntries()
        {
            foreach (var entry in _entries.Values) if (entry.Box != null) entry.Box.visible = false;
        }

        private void HideTip()
        {
            try { if (_tip != null) _tip.visible = false; } catch { }
            _hoverId = 0;
        }

        private static UI.UIBattleInfoPanel FindPanel()
        {
            try { return Walk(GRoot.inst, 0); } catch { return null; }
        }

        private static UI.UIBattleInfoPanel Walk(GObject node, int depth)
        {
            if (node == null || depth > 12) return null;
            if (node is UI.UIBattleInfoPanel panel) return panel;
            if (node is GComponent component)
                for (int i = 0; i < component.numChildren; i++)
                {
                    var found = Walk(component.GetChildAt(i), depth + 1);
                    if (found != null) return found;
                }
            return null;
        }
    }
}
