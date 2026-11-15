using System;
using System.Collections.Generic;
using FairyGUI;
using PvePanelMod.Waves;
using UnityEngine;

namespace PvePanelMod
{
    /// <summary>
    /// 右侧情报栏：从屏幕右边伸出来。尺寸照设计草图的 .rb（画布 2000×1125），乘 k = 屏幕高 / 1125。
    ///   第一排：小灰字「第 N 回合」+ 怪物「N回合后 / 下回合！」（红、恶魔）+ 轮次奖励（黄、金币）；
    ///   波次表：接下来最多 3 波，每波 = 回合 + 类别 + 怪物头像（×数量）+ 刷怪格子编号 / 随机；
    ///   一至四楼各一行：最近一次的商店（卡面）或筹码（品质底图 + 图标），结束后变灰但不消失。
    /// 整块不挡鼠标（下面的地图和官方按钮还要能点）。
    /// </summary>
    internal sealed class RightPanel : IDisposable
    {
        private const float Top = 384f, Width = 312f, HeaderH = 34f, WaveLineH = 38f, RowH = 52f, RowGap = 6f, Border = 3f;
        private const float ItemSize = 38f, ItemGap = 7f, ItemX = 40f;
        private const int MaxItems = 5, MaxWaves = 3;

        private static readonly Color PanelColor = new Color(0.086f, 0.098f, 0.157f, 0.94f);
        private static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color LineColor = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color WellColor = new Color(0.051f, 0.059f, 0.094f, 1f);
        private static readonly Color Soft = new Color(0.776f, 0.804f, 0.859f, 1f);
        private static readonly Color Muted = new Color(0.541f, 0.565f, 0.651f, 1f);
        private static readonly Color MonsterRed = new Color(1f, 0.302f, 0.302f, 1f);
        private static readonly Color Gold = new Color(1f, 0.761f, 0.239f, 1f);
        private static readonly Color Live = new Color(0.212f, 0.851f, 0.498f, 1f);
        private static readonly Color[] Floor =
        {
            new Color(1f, 0.275f, 0.275f), new Color(0.58f, 1f, 0.275f), new Color(0.263f, 0.525f, 0.957f), new Color(1f, 0.702f, 0.275f)
        };

        private readonly Icons _icons;
        private GComponent _layer, _box;
        private GTextField _round;
        private Countdown _mon, _rew;
        private readonly List<WaveLine> _waves = new List<WaveLine>();
        private readonly List<RowView> _rows = new List<RowView>();
        // 每 0.25 秒都要用，放字段里复用，免得每帧刷新都 new 两个 List
        private readonly List<WaveInfo> _upcoming = new List<WaveInfo>();
        private readonly List<int> _monsterRounds = new List<int>();
        private const int DefaultOrder = 25010;
        private float _k, _right, _top;
        private bool _floating, _loggedOrder;
        private int _waveLines = -1;
        private string _lastHeader;

        private readonly Action<string> _info;

        public RightPanel(Icons icons, Action<string> info) { _icons = icons; _info = info; }

        public void Hide()
        {
            if (_layer != null && !_layer.isDisposed) _layer.visible = false;
        }

        /// <param name="rightEdge">右栏右边贴到哪（界面坐标）：官方按钮列的左边，或者屏幕右边。窄屏（如 1028×720）时界面根节点比屏幕窄，要用官方面板的右边界。</param>
        /// <param name="top">右栏顶部的位置；小于 0 = 用原来的默认高度。</param>
        /// <param name="under">要压在它下面的官方窗口（表情 / 聊天那列按钮所在的窗口）；null = 照旧放在最上层。</param>
        /// <param name="round">第几回合：标题和轮次奖励倒计时用。</param>
        /// <param name="progress">游戏进度（GameProgress）：波次按它触发。平时等于回合，玩家被击倒会让它多推进。</param>
        public void Render(float rightEdge, float top, Window under, int round, int progress, List<WaveInfo> waves, List<PlayerRow> players, Dictionary<long, RightEntry> entries)
        {
            var root = GRoot.inst;
            if (root == null || players == null || players.Count == 0) { Hide(); return; }
            float k = PanelScale.K(root);

            _upcoming.Clear();
            if (waves != null)
                foreach (var w in waves) if (w.Progress > progress && _upcoming.Count < MaxWaves) _upcoming.Add(w);

            if (_layer == null || _layer.isDisposed || _layer.parent != root || Math.Abs(k - _k) > 0.001f
                || _rows.Count != players.Count || _waveLines != _upcoming.Count || _floating != (under != null))
                Build(root, k, players.Count, _upcoming.Count, under != null);
            if (top < 0f) top = Top * k;
            if (Math.Abs(rightEdge - _right) > 0.5f || Math.Abs(top - _top) > 0.5f)
            {
                _right = rightEdge;
                _top = top;
                _box.SetXY(rightEdge - _box.width, PanelScale.FitBottom(top, _box.height));   // 放大后不超出屏幕底边
            }
            PlaceUnder(root, under);

            // 第一排：刷怪和轮次奖励都在回合开头结算，所以只看以后的（没有「本回合」）
            _monsterRounds.Clear();
            if (waves != null) foreach (var w in waves) if (w.SpawnsMonsters) _monsterRounds.Add(w.Progress);
            int mon = RoundMath.MonsterIn(progress, _monsterRounds);
            int rew = RoundMath.RewardIn(round);
            string header = round + "|" + progress + "|" + mon + "|" + rew;
            if (header != _lastHeader)
            {
                _lastHeader = header;
                _round.text = "第 " + round + " 回合";
                float x = 16f * k + _round.textWidth + 10f * k;
                x = _mon.Set(mon, x) + (mon > 0 ? 8f * k : 0f);
                _rew.Set(rew, x);
            }

            for (int i = 0; i < _upcoming.Count; i++) _waves[i].Set(_upcoming[i]);

            var now = DateTime.UtcNow;
            for (int i = 0; i < players.Count; i++)
            {
                RightEntry e;
                entries.TryGetValue(players[i].Id, out e);
                _rows[i].Update(players[i].Slot, e, now);
            }
            if (!_layer.visible) _layer.visible = true;
        }

        /// <summary>
        /// 把右栏的层级放到 <paramref name="under"/> 下面。FairyGUI 里 sortingOrder 大的在上；
        /// sortingOrder 相同（都是 0）时按子节点顺序，而 SetChildIndex 只对 sortingOrder 为 0 的节点有效。
        /// </summary>
        private void PlaceUnder(GRoot root, Window under)
        {
            if (under == null || under.isDisposed || under.parent != root)
            {
                if (_layer.sortingOrder != DefaultOrder) _layer.sortingOrder = DefaultOrder;
                return;
            }
            if (!_loggedOrder)
            {
                _loggedOrder = true;
                _info?.Invoke("PVE 面板右栏：表情 / 聊天按钮窗口 sortingOrder=" + under.sortingOrder
                    + "，在第 " + root.GetChildIndex(under) + " 层（共 " + root.numChildren + " 层）");
            }
            if (under.sortingOrder > 0)
            {
                int order = under.sortingOrder - 1;
                if (_layer.sortingOrder != order) _layer.sortingOrder = order;
                return;
            }
            if (_layer.sortingOrder != 0) _layer.sortingOrder = 0;
            int wi = root.GetChildIndex(under), li = root.GetChildIndex(_layer);
            if (wi >= 0 && li > wi) root.SetChildIndex(_layer, wi);
        }

        // ---------------- 搭骨架 ----------------

        private void Build(GRoot root, float k, int count, int waveLines, bool floating)
        {
            Dispose();
            _k = k;
            _waveLines = waveLines;
            _floating = floating;
            _lastHeader = null;
            _right = -1f;
            _top = -1f;
            _layer = new GComponent { name = "PvePanelRight", sortingOrder = DefaultOrder, touchable = false };
            root.AddChild(_layer);

            float waveBlock = waveLines > 0 ? waveLines * WaveLineH + 8f : 0f;
            float h = (6f + HeaderH + waveBlock + 4f + count * (RowH + RowGap) + 4f) * k;
            float w = Width * k, b = Border * k, r = 20f * k;
            _box = new GComponent { touchable = false };
            _box.SetSize(w, h);
            _layer.AddChild(_box);

            // 底板：贴屏幕边时左边圆角、右边贴死；挪到按钮列左边（浮着）时四角都圆、四边都有边框
            float rr = floating ? r : 0f, ri = floating ? r - b : 0f, inner = floating ? w - 2 * b : w - b;
            var border = new GGraph { touchable = false };
            border.DrawRoundRect(w, h, BorderColor, new[] { r, rr, r, rr });
            _box.AddChild(border);
            var bg = new GGraph { touchable = false };
            bg.SetXY(b, b);
            bg.DrawRoundRect(inner, h - 2 * b, PanelColor, new[] { r - b, ri, r - b, ri });
            _box.AddChild(bg);

            float y = 6f * k;
            _round = LeftPanel.Text(13f * k, Muted, true);
            _round.SetXY(16f * k, y + 9f * k);
            _box.AddChild(_round);
            _mon = new Countdown(_box, _icons.Get("demon"), MonsterRed, k, y);
            _rew = new Countdown(_box, _icons.Get("coin"), Gold, k, y);
            y += HeaderH * k;

            for (int i = 0; i < waveLines; i++)
            {
                var line = new WaveLine(_box, k, w);
                line.SetXY(14f * k, y + i * WaveLineH * k);
                _waves.Add(line);
            }
            y += waveBlock * k;

            var sep = new GGraph { touchable = false };
            sep.SetXY(b, y);
            sep.DrawRect(inner, Mathf.Max(1f, k), 0, Color.clear, LineColor);
            _box.AddChild(sep);
            y += 4f * k;

            for (int i = 0; i < count; i++)
            {
                var row = new RowView(_icons, k, floating ? inner : w);
                row.Root.SetXY(b, y + i * (RowH + RowGap) * k);
                _box.AddChild(row.Root);
                _rows.Add(row);
            }
        }

        public void Dispose()
        {
            _rows.Clear();
            _waves.Clear();
            if (_layer != null && !_layer.isDisposed) _layer.Dispose();
            _layer = null; _box = null; _round = null; _mon = null; _rew = null;
        }

        // ---------------- 倒计时：图标 + 「N回合后 / 下回合！」，下回合时加同色底 ----------------

        private sealed class Countdown
        {
            private readonly GGraph _pill;
            private readonly GLoader _icon;
            private readonly GTextField _text;
            private readonly Color _color;
            private readonly float _k, _y;

            public Countdown(GComponent parent, NTexture icon, Color color, float k, float y)
            {
                _color = color; _k = k; _y = y;
                _pill = new GGraph { touchable = false };
                parent.AddChild(_pill);
                _icon = new GLoader { touchable = false, fill = FillType.ScaleFree, texture = icon };
                _icon.SetSize(22f * k, 22f * k);
                parent.AddChild(_icon);
                _text = LeftPanel.Text(17f * k, color, true);
                parent.AddChild(_text);
            }

            /// <summary>n &lt;= 0 表示没有下一次（例如后面没有怪物波次了），整组隐藏。返回右边界。</summary>
            public float Set(int n, float x)
            {
                bool show = n > 0;
                _pill.visible = _icon.visible = _text.visible = show;
                if (!show) return x;
                _text.text = n == 1 ? "下回合！" : n + "回合后";
                _icon.SetXY(x + 5f * _k, _y + 6f * _k);
                _text.SetXY(_icon.x + 27f * _k, _y + (34f * _k - _text.textHeight) / 2f);
                float right = _text.x + _text.textWidth + 8f * _k;
                _pill.visible = n == 1;
                if (n == 1)
                {
                    _pill.SetXY(x, _y + 3f * _k);
                    var c = _color; c.a = 0.2f;
                    _pill.DrawRoundRect(right - x, 28f * _k, c, new[] { 9f * _k, 9f * _k, 9f * _k, 9f * _k });
                }
                return right;
            }
        }

        // ---------------- 波次一行：R3 精英 [头像×2][头像] 格 37·42 ----------------

        private sealed class WaveLine
        {
            private const int MaxMonsters = 4;
            private const float Face = 34f;   // 头像太小认不出是哪只怪
            private readonly float _k, _width;
            private readonly GTextField _round, _cat, _where;
            private readonly UI.TextureLoader[] _faces = new UI.TextureLoader[MaxMonsters];
            private readonly GTextField[] _counts = new GTextField[MaxMonsters];
            private float _x, _y;
            private string _key;

            public WaveLine(GComponent parent, float k, float width)
            {
                _k = k; _width = width;
                _round = LeftPanel.Text(14f * k, Soft, true); parent.AddChild(_round);
                _cat = LeftPanel.Text(14f * k, Soft, true); parent.AddChild(_cat);
                for (int i = 0; i < MaxMonsters; i++)
                {
                    _faces[i] = new UI.TextureLoader { touchable = false, fill = FillType.ScaleMatchHeight, align = AlignType.Center, verticalAlign = VertAlignType.Middle };
                    _faces[i].SetSize(Face * k, Face * k);
                    parent.AddChild(_faces[i]);
                    _counts[i] = LeftPanel.Text(11f * k, Color.white, true);
                    parent.AddChild(_counts[i]);
                }
                _where = LeftPanel.Text(12f * k, Muted, false); parent.AddChild(_where);
            }

            public void SetXY(float x, float y) { _x = x; _y = y; _key = null; }

            public void Set(WaveInfo w)
            {
                string key = w.EventId + "|" + w.Progress;
                if (key == _key) return;
                _key = key;
                float k = _k, mid = _y + WaveLineH * k / 2f;

                _round.text = "R" + w.Progress;
                _round.SetXY(_x, mid - _round.textHeight / 2f);
                float x = _x + Math.Max(_round.textWidth, 30f * k) + 6f * k;

                _cat.visible = w.SpawnsMonsters;
                if (w.SpawnsMonsters)
                {
                    _cat.text = w.Category == WaveCategory.Boss ? "首领" : w.Category == WaveCategory.Elite ? "精英" : "普通";
                    _cat.color = w.Category == WaveCategory.Boss ? new Color(1f, 0.302f, 0.302f) : w.Category == WaveCategory.Elite ? new Color(0.753f, 0.545f, 1f) : new Color(0.384f, 0.659f, 1f);
                    _cat.SetXY(x, mid - _cat.textHeight / 2f);
                    x += _cat.textWidth + 6f * k;
                }

                // 怪物头像：游戏里的 UT_Hero_ProfilePhoto_{怪物 id}（wiki 的怪物头像也是这个名字），右下角写数量
                for (int i = 0; i < MaxMonsters; i++)
                {
                    bool show = i < w.Monsters.Count;
                    _faces[i].visible = show;
                    _counts[i].visible = show && w.Monsters[i].Count > 1;
                    _faces[i].data = show ? TipText.Monster(w.Monsters[i].Name, w.Monsters[i].Count) : null;
                    if (!show) continue;
                    string url = "UT_Hero_ProfilePhoto_" + w.Monsters[i].Id;
                    if (_faces[i].url != url) _faces[i].url = url;
                    _faces[i].SetXY(x, mid - Face * k / 2f);
                    x += Face * k;
                    if (_counts[i].visible)
                    {
                        _counts[i].text = "×" + w.Monsters[i].Count;
                        _counts[i].SetXY(x - 6f * k, mid + Face * k / 2f - _counts[i].textHeight + 2f * k);
                        x += _counts[i].textWidth - 4f * k;
                    }
                    x += 4f * k;
                }

                // 刷怪位置：配置给了精确格子就写格子编号，否则写「随机」
                if (w.RandomLands || w.LandIds.Count == 0) _where.text = w.SpawnsMonsters ? "随机" : "";
                else
                {
                    var parts = new List<string>();
                    for (int i = 0; i < w.LandIds.Count && i < 3; i++) parts.Add(w.LandIds[i].ToString());
                    _where.text = "格 " + string.Join("·", parts.ToArray()) + (w.LandIds.Count > 3 ? "…" : "");
                }
                _where.SetXY(Math.Min(x + 4f * k, _x + _width - 30f * k - _where.textWidth), mid - _where.textHeight / 2f);
            }
        }

        // ---------------- 一行：楼层色条 + 类型图标 + 卡面/筹码 ----------------

        private sealed class RowView
        {
            public readonly GComponent Root = new GComponent { touchable = false };
            private readonly Icons _icons;
            private readonly float _k;
            private readonly GGraph _flash, _strip, _live;
            private readonly GLoader _type;
            private readonly GTextField _empty, _reroll, _more;
            private readonly Item[] _items = new Item[MaxItems];
            private string _lastKey;

            public RowView(Icons icons, float k, float width)
            {
                _icons = icons; _k = k;
                Root.SetSize(width, RowH * k);
                _flash = new GGraph { touchable = false };
                _flash.DrawRect(width, RowH * k, 0, Color.clear, Color.white);
                Root.AddChild(_flash);
                _strip = new GGraph { touchable = false };
                _strip.SetXY(0f, 4f * k);
                Root.AddChild(_strip);
                _type = new GLoader { touchable = false, fill = FillType.ScaleFree };
                _type.SetSize(22f * k, 22f * k);
                _type.SetXY(13f * k, (RowH - 22f) / 2f * k);
                Root.AddChild(_type);
                _empty = LeftPanel.Text(14f * k, Muted, false);
                _empty.text = "—";
                _empty.SetXY(ItemX * k, (RowH * k - _empty.textHeight) / 2f);
                Root.AddChild(_empty);
                for (int i = 0; i < MaxItems; i++)
                {
                    _items[i] = new Item(Root, icons, k);
                    _items[i].SetXY((ItemX + i * (ItemSize + ItemGap)) * k, (RowH - ItemSize) / 2f * k);
                }
                _reroll = LeftPanel.Text(12f * k, Soft, true);
                Root.AddChild(_reroll);
                // 超出一行能放下的数量时给个「+N」，免得看起来像"只有这几个"
                _more = LeftPanel.Text(12f * k, Soft, true);
                _more.SetXY((ItemX + MaxItems * (ItemSize + ItemGap)) * k, (RowH - 12f) / 2f * k);
                _more.visible = false;
                Root.AddChild(_more);
                _live = new GGraph { touchable = false };
                _live.DrawEllipse(7f * k, 7f * k, Live);
                _live.SetXY(width - 16f * k, 3f * k);
                Root.AddChild(_live);
            }

            public void Update(int slot, RightEntry e, DateTime now)
            {
                bool flashing = e != null && (now - e.StartedUtc).TotalSeconds < 1.2;
                string key = slot + "|" + (e == null ? "-" : (e.IsShop + "|" + string.Join(",", e.Ids) + "|" + string.Join(",", e.Bought)
                             + "|" + e.PickId + "|" + e.Rerolls + "|" + e.Done)) + "|" + flashing;
                if (key == _lastKey) return;
                _lastKey = key;
                float k = _k;
                var floor = Floor[Mathf.Clamp(slot, 0, 3)];

                _strip.DrawRoundRect(6f * k, (RowH - 8f) * k, floor, new[] { 0f, 4f * k, 0f, 4f * k });
                _flash.visible = flashing;
                if (flashing) { var c = floor; c.a = 0.3f; _flash.color = c; }

                bool has = e != null && e.Ids.Length > 0;
                int extra = has ? e.Ids.Length - MaxItems : 0;
                _empty.visible = !has;
                _type.visible = has;
                _live.visible = has && !e.Done;
                _more.visible = extra > 0;
                if (_more.visible) _more.text = "+" + extra;
                _reroll.visible = has && !e.IsShop && e.Rerolls > 0 && !_more.visible;
                if (!has) { foreach (var it in _items) it.Hide(); return; }

                _type.texture = _icons.Get(e.IsShop ? "cart" : "chip");
                _type.alpha = e.Done ? 0.6f : 1f;

                for (int i = 0; i < MaxItems; i++)
                {
                    if (i >= e.Ids.Length) { _items[i].Hide(); continue; }
                    int id = e.Ids[i];
                    if (e.IsShop) _items[i].ShowCard(id, i < e.Bought.Length && e.Bought[i], e.Done);
                    else _items[i].ShowRelic(id, e.Done && e.PickId != 0 && id == e.PickId, e.Done);
                }

                if (_reroll.visible)
                {
                    _reroll.text = "↻" + e.Rerolls;
                    _reroll.SetXY(Root.width - _reroll.textWidth - 16f * k, (RowH * k - _reroll.textHeight) / 2f);
                }
            }
        }

        // ---------------- 一个格子：卡面，或 品质底图 + 筹码图标 ----------------

        /// <summary>
        /// 卡牌和筹码用同一套规则，「彩色 = 被拿走了」：
        ///   还在逛 / 还在选（绿点亮着）：全部彩色，方便看清店里有什么；已经买下的卡加一圈绿色细框；
        ///   结束后：买了的卡 / 选中的筹码保持彩色，没拿的变黑白（筹码连同品质底图一起）。
        /// 不再盖深色和打勾：彩色本身就是「拿走了」，悬停提示里仍写着「已购买」。
        /// 筹码品质底图是 wiki 上的 UT_RelicBase_*（游戏里按这个名字加载不出来），随模组放在 data/relicbase_*.png。
        /// </summary>
        private sealed class Item
        {
            private readonly Icons _icons;
            private readonly float _k;
            private readonly GGraph _well, _frame;
            private readonly GLoader _base;
            private readonly UI.TextureLoader _img;
            private float _x, _y;

            public Item(GComponent parent, Icons icons, float k)
            {
                _icons = icons;
                _k = k;
                float s = ItemSize * k;
                _well = new GGraph { touchable = false };
                _well.DrawRoundRect(s, s, WellColor, new[] { 7f * k, 7f * k, 7f * k, 7f * k });
                parent.AddChild(_well);
                _base = new GLoader { touchable = false, fill = FillType.ScaleFree };
                _base.SetSize(s, s);
                parent.AddChild(_base);
                _img = new UI.TextureLoader { touchable = false, fill = FillType.ScaleFree };
                parent.AddChild(_img);
                // 还在逛时「已经买下」的细框：和右上角「进行中」的绿点同色，盖在卡面上面（只有边、不挡图）
                _frame = new GGraph { touchable = false };
                _frame.DrawRect(s, s, Mathf.Max(2, Mathf.RoundToInt(2f * k)), Live, Color.clear);
                parent.AddChild(_frame);
            }

            public void SetXY(float x, float y)
            {
                _x = x; _y = y;
                _well.SetXY(x, y);
                _base.SetXY(x, y);
                _frame.SetXY(x, y);
            }

            public void ShowCard(int cardId, bool bought, bool done)
            {
                float s = ItemSize * _k;
                _well.visible = true;
                _base.visible = false;
                _img.visible = true;
                _img.SetSize(s, s);
                _img.SetXY(_x, _y);
                string url = CardUrl(cardId);
                if (_img.url != url) _img.url = url;
                _img.grayed = done && !bought;
                _img.alpha = 1f;
                _frame.visible = bought && !done;
                _img.data = TipText.Card(cardId, bought);
                _base.data = null;
            }

            public void ShowRelic(int relicId, bool picked, bool done)
            {
                float s = ItemSize * _k, inner = s * 0.83f;
                string icon, quality;
                RelicInfo(relicId, out icon, out quality);
                _well.visible = false;
                _frame.visible = false;   // 筹码只能选一个、选完就结束，用不着「已经拿了」的框
                _base.visible = true;
                _base.texture = _icons.Get("relicbase_" + quality);
                _img.visible = true;
                _img.SetSize(inner, inner);
                _img.SetXY(_x + (s - inner) / 2f, _y + (s - inner) / 2f);
                if (_img.url != icon) _img.url = icon;
                bool gray = done && !picked;
                _img.grayed = _base.grayed = gray;
                _img.alpha = _base.alpha = 1f;
                _base.data = TipText.Relic(relicId);   // 挂在整枚筹码（底图）上
                _img.data = null;
            }

            public void Hide()
            {
                _well.visible = _base.visible = _img.visible = _frame.visible = false;
                _img.data = _base.data = null;
            }

            private static string CardUrl(int id)
            {
                try
                {
                    CardInfoConfigure cfg;
                    if (StaticConfigure.Card != null && StaticConfigure.Card.InfoDict.TryGetValue(id, out cfg) && cfg != null) return cfg.GetImage();
                }
                catch { }
                return "UT_HandCard_" + id;
            }

            private static void RelicInfo(int id, out string icon, out string quality) { RelicLook.Info(id, out icon, out quality); }
        }
    }

    /// <summary>筹码的图标地址和品质（Blue / Purple / Orange），右栏和左栏的关键筹码共用。读不到给默认值。</summary>
    internal static class RelicLook
    {
        public static void Info(int id, out string icon, out string quality)
        {
            icon = "UT_Relic_" + id;
            quality = "Blue";
            try
            {
                RelicInfoConfigure cfg;
                if (StaticConfigure.Relic != null && StaticConfigure.Relic.InfoDict.TryGetValue(id, out cfg) && cfg != null)
                {
                    if (!string.IsNullOrEmpty(cfg.Icon)) icon = cfg.Icon;
                    string q = cfg.RelicQualityType.ToString();
                    if (q == "Blue" || q == "Purple" || q == "Orange") quality = q;
                }
            }
            catch { }
        }
    }
}
