using System;
using System.Collections.Generic;
using FairyGUI;
using UnityEngine;

namespace PvePanelMod
{
    /// <summary>
    /// 左侧信息栏。所有尺寸照设计草图（2000×1125 的画布）写，再乘 k = 屏幕高 / 1125，
    /// 这样跟着游戏自己的界面缩放走（设计决定 11）。
    /// 面板整块不挡鼠标：除了把手和缩放按钮，其余元素都是 touchable = false，下面的地图照常能点。
    /// </summary>
    internal sealed class LeftPanel : IDisposable
    {
        // ---- 草图里的尺寸（画布单位）----
        private const float Top = 96f, Width = 284f, RowH = 116f, RowGap = 6f, Pad = 8f, Border = 3f;
        private const float PlateW = 104f, PlateH = 78f, HeadSize = 98f, StarSize = 20f, StarStep = 11f, SkillSize = 26f;
        private const float ColX = 116f, BuffSize = 28f, BuffGap = 4f, TabW = 32f, HandleH = 84f, TabH = 58f, HitPadX = 16f, HitPadY = 6f, HintH = 60f;
        private const int MaxBuffs = 4;
        // 右边一列的右边界：第一行（血量 星币 手牌）和关键筹码都不能超过它
        private const float ColRight = Width - Border - 6f;
        // buff 一行的位置：平时在第一行下面；装了「关键筹码置顶」时往下挪，中间空出来放关键筹码
        private const float BuffY = 64f, BuffYPinned = 87f;
        // 关键筹码 30 一个、间距 2：一行正好放 5 个原大的（26 在游戏里看不太清）
        private const float RelicY = 53f, RelicSize = 30f, RelicGap = 2f, ZoomSize = 22f;
        private const int MaxRelics = 8;

        // ---- 配色（取启动器 v2，和草图一致）----
        private static readonly Color PanelColor = new Color(0.086f, 0.098f, 0.157f, 0.94f);
        private static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color WellColor = new Color(0.051f, 0.059f, 0.094f, 1f);
        private static readonly Color Pink = new Color(1f, 0.09f, 0.463f, 1f);
        private static readonly Color Soft = new Color(0.776f, 0.804f, 0.859f, 1f);
        private static readonly Color Muted = new Color(0.541f, 0.565f, 0.651f, 1f);
        private static readonly Color Danger = new Color(1f, 0.353f, 0.353f, 1f);
        private static readonly Color Soon = new Color(1f, 0.749f, 0.235f, 1f);
        private static readonly Color[] Floor =
        {
            new Color(1f, 0.275f, 0.275f), new Color(0.58f, 1f, 0.275f), new Color(0.263f, 0.525f, 0.957f), new Color(1f, 0.702f, 0.275f)
        };

        private readonly Icons _icons;
        private GComponent _layer, _side;
        private GGraph _border, _bg;
        private GComponent _handle;
        private GTextField _handleText;
        private GTextField _hint, _hintKeys;
        private string _keysLine = "";
        private float _zoomLeft;
        private GComponent _tab;
        private GGraph _tabBg;
        private bool _handOpen;
        private readonly List<RowView> _rows = new List<RowView>();
        private float _k;
        private bool _collapsed;
        private bool _pinnedLayout;

        public bool Collapsed => _collapsed;
        /// <summary>左栏下边缘（界面坐标），战斗面板挂在它下面。</summary>
        public float BottomY => _side == null || _side.isDisposed ? 0f : _side.y + _side.height;
        /// <summary>手牌抽屉是否打开（粉色把手切换）。左栏收起时也能打开。</summary>
        public bool HandOpen => _handOpen;
        /// <summary>左栏（含把手一列）的右边界，手牌抽屉从这里往右摆。</summary>
        public float TabsRight => _side == null || _side.isDisposed ? 0f : _side.x + _side.width + (TabW - Border) * _k;
        public float SideTop => _side == null || _side.isDisposed ? 0f : _side.y;
        public float SideHeight => _side == null || _side.isDisposed ? 0f : _side.height;
        public float RowHeight => RowH * _k;
        /// <summary>每一行玩家的顶部（界面坐标），手牌抽屉的行和它对齐。</summary>
        public List<float> RowTops()
        {
            var list = new List<float>();
            if (_side == null || _side.isDisposed) return list;
            for (int i = 0; i < _rows.Count; i++) list.Add(_side.y + _rows[i].Root.y);
            return list;
        }
        public event Action CollapsedChanged;
        /// <summary>粉色把手切换了手牌抽屉。</summary>
        public event Action HandOpenChanged;
        /// <summary>点了快捷键那一排右边的「－」「＋」：+1 放大一档，-1 缩小一档。</summary>
        public event Action<int> ScaleRequested;

        public LeftPanel(Icons icons) { _icons = icons; }

        public void Hide()
        {
            if (_layer != null && !_layer.isDisposed) _layer.visible = false;
        }

        /// <param name="pinnedLayout">装了「关键筹码置顶」：buff 往下挪一行，给关键筹码留位置。</param>
        public void Render(List<PlayerRow> data, bool pinnedLayout)
        {
            var root = GRoot.inst;
            if (root == null || data == null || data.Count == 0) { Hide(); return; }
            float k = PanelScale.K(root);
            if (_layer == null || _layer.isDisposed || _layer.parent != root || Math.Abs(k - _k) > 0.001f || _rows.Count != data.Count
                || pinnedLayout != _pinnedLayout)
                Build(root, k, data.Count, pinnedLayout);
            for (int i = 0; i < data.Count; i++) _rows[i].Update(data[i]);
            if (!_layer.visible) _layer.visible = true;
        }

        // ---------------- 搭骨架 ----------------

        private void Build(GRoot root, float k, int count, bool pinnedLayout)
        {
            Dispose();
            _k = k;
            _pinnedLayout = pinnedLayout;
            _layer = new GComponent { name = "PvePanelLeft", sortingOrder = 25010 };
            root.AddChild(_layer);

            float rowsBottom = Pad + count * RowH + (count - 1) * RowGap;
            float h = (rowsBottom + Pad + HintH) * k;   // 底部两行小字：操作提示 + 快捷键
            float w = Width * k;
            _side = new GComponent();
            _side.SetSize(w, h);
            _side.SetXY(_collapsed ? -w : 0f, Top * k);
            _layer.AddChild(_side);

            // 底板：外层是边框色，里层是面板色；右边圆角，左边贴死屏幕边。
            // 鼠标穿过底板（面板下面的地图和官方按钮还要能点）；只有把手和缩放按钮接点击。
            float r = 20f * k;
            _border = new GGraph { touchable = false };
            _border.SetSize(w, h);
            _border.DrawRoundRect(w, h, BorderColor, new[] { 0f, r, 0f, r });
            _side.AddChild(_border);
            float b = Border * k;
            _bg = new GGraph { touchable = false };
            _bg.SetXY(0f, b);
            _bg.DrawRoundRect(w - b, h - 2 * b, PanelColor, new[] { 0f, r - b, 0f, r - b });
            _side.AddChild(_bg);

            for (int i = 0; i < count; i++)
            {
                var row = new RowView(_icons, k, pinnedLayout);
                row.Root.SetXY(0f, (Pad + i * (RowH + RowGap)) * k);
                _side.AddChild(row.Root);
                _rows.Add(row);
            }

            // 底部小字：操作提示 + 快捷键提示（快捷键那行按配置生成）
            // 操作提示要醒目：黄色加粗、字稍大，左边一条黄色竖条
            _hint = Text(14f * k, Soon, true);
            _hint.text = "重掷筹码、上 buff 后不用收起，面板会跟着更新";
            _hint.SetXY(16f * k, (rowsBottom + 4f) * k);
            // 游戏字体比草图宽：放不下就把字号往小调，直到这一行不超出面板
            float room = w - b - 26f * k;
            for (int size = Mathf.RoundToInt(14f * k); _hint.textWidth > room && size > Mathf.RoundToInt(10f * k); size--)
            {
                var tf = _hint.textFormat; tf.size = size - 1; _hint.textFormat = tf;
            }
            var bar = new GGraph { touchable = false };
            bar.SetXY(9f * k, _hint.y + 2f * k);
            bar.DrawRoundRect(3f * k, _hint.textHeight - 4f * k, Soon, new[] { 2f * k, 2f * k, 2f * k, 2f * k });
            _side.AddChild(bar);
            _side.AddChild(_hint);
            _hintKeys = Text(12f * k, Muted, false);
            // 「－」「＋」按钮比这一行字高，以这行的中线为中心上下各伸出一截：
            // 往下多空一点，按钮上沿才不会压到上面那行黄字
            float keysGap = Math.Max(4f * k, (ZoomSize * k - _hintKeys.textHeight) / 2f + 5f * k);
            _hintKeys.SetXY(16f * k, _hint.y + _hint.textHeight + keysGap);
            _side.AddChild(_hintKeys);
            // 同一排最右边：[－] [＋]。白色细框 = 按钮（和左边只是提示的快捷键区分开）；整排只有这两个按钮接点击
            _zoomLeft = ZoomControl(w - b - 12f * k, _hintKeys.y + _hintKeys.textHeight / 2f, k);
            UpdateKeysLine();

            // 两个把手：看得见的部分照草图大小；点击范围比看到的大一圈（往右 HitPadX、上下 HitPadY），
            // 而且整块接点击（opaque）——面板其余部分都让鼠标穿过去，只有把手接点击。
            float padX = HitPadX * k, padY = HitPadY * k;

            // 收起把手：右边缘中间
            _handle = new GComponent { opaque = true };
            _handle.SetSize(TabW * k + padX, HandleH * k + 2 * padY);
            _handle.SetXY(w - b, (h - HandleH * k) / 2f - padY);
            var hb = new GGraph { touchable = false };
            hb.SetXY(0f, padY);
            hb.DrawRoundRect(TabW * k, HandleH * k, BorderColor, new[] { 0f, 12f * k, 0f, 12f * k });
            _handle.AddChild(hb);
            var hi = new GGraph { touchable = false };
            hi.SetXY(0f, padY + b);
            hi.DrawRoundRect(TabW * k - b, HandleH * k - 2 * b, PanelColor, new[] { 0f, 12f * k - b, 0f, 12f * k - b });
            _handle.AddChild(hi);
            _handleText = Text(18f * k, Soft, true);
            _handle.AddChild(_handleText);
            _handle.onClick.Add(Toggle);
            _side.AddChild(_handle);
            UpdateHandle();

            // 粉色手牌把手：收起把手正下方，图案是卡牌 + 向外箭头，按下拉出手牌抽屉
            float tabH = TabH * k;
            _tab = new GComponent { opaque = true };
            _tab.SetSize(TabW * k + padX, tabH + 2 * padY);
            _tab.SetXY(w - b, (h + HandleH * k) / 2f + 8f * k - padY);
            _tabBg = new GGraph { touchable = false };
            _tabBg.SetXY(0f, padY);
            _tab.AddChild(_tabBg);
            var pull = new GLoader { touchable = false, fill = FillType.ScaleFree, texture = _icons.Get("pull") };
            pull.SetSize(22f * k, 22f * k);
            pull.SetXY((TabW * k - b - 22f * k) / 2f, padY + (tabH - 22f * k) / 2f);
            _tab.AddChild(pull);
            _tab.onClick.Add(ToggleHand);
            _side.AddChild(_tab);
            UpdateTab();

        }

        /// <summary>
        /// 右对齐摆在 (right, midY)：[－][＋]，返回按钮组的左边界。按钮是白色细框圆角，点击范围比看到的大一圈。
        /// 中间不显示百分比，免得挤到左边的快捷键提示。
        /// </summary>
        private float ZoomControl(float right, float midY, float k)
        {
            float size = ZoomSize * k, gap = 6f * k, hit = 4f * k;
            float x = right - size;
            ZoomButton("+", x, midY, size, hit, 1, k);
            x -= gap + size;
            ZoomButton("-", x, midY, size, hit, -1, k);
            return x;
        }

        private void ZoomButton(string label, float x, float midY, float size, float hit, int dir, float k)
        {
            var btn = new GComponent { opaque = true };
            btn.SetSize(size + 2 * hit, size + 2 * hit);
            btn.SetXY(x - hit, midY - size / 2f - hit);
            float line = 1.5f * k, r = 6f * k;
            var edge = new GGraph { touchable = false };
            edge.SetXY(hit, hit);
            edge.DrawRoundRect(size, size, new Color(1f, 1f, 1f, 0.85f), new[] { r, r, r, r });
            btn.AddChild(edge);
            var fill = new GGraph { touchable = false };
            fill.SetXY(hit + line, hit + line);
            fill.DrawRoundRect(size - 2 * line, size - 2 * line, PanelColor, new[] { r - line, r - line, r - line, r - line });
            btn.AddChild(fill);
            var t = Text(16f * k, Color.white, true);
            t.text = label;
            t.SetXY(hit + (size - t.textWidth) / 2f, hit + (size - t.textHeight) / 2f);
            btn.AddChild(t);
            btn.onClick.Add(() => ScaleRequested?.Invoke(dir));
            _side.AddChild(btn);
        }

        /// <summary>快捷键提示，例如「F3 左栏 · F4 手牌 · F5 右栏」。</summary>
        public void SetKeysLine(string line)
        {
            _keysLine = line ?? "";
            UpdateKeysLine();
        }

        private void UpdateKeysLine()
        {
            if (_hintKeys == null || _hintKeys.isDisposed) return;
            _hintKeys.text = _keysLine;
            _hintKeys.visible = _keysLine.Length > 0;
            // 保险：快捷键提示不能伸到「－」「＋」按钮底下，放不下就缩小字号
            float room = _zoomLeft - 8f * _k - _hintKeys.x;
            for (int size = Mathf.RoundToInt(12f * _k); room > 0 && _hintKeys.textWidth > room && size > Mathf.RoundToInt(9f * _k); size--)
            {
                var tf = _hintKeys.textFormat; tf.size = size - 1; _hintKeys.textFormat = tf;
            }
        }

        public void ToggleHand()
        {
            _handOpen = !_handOpen;
            UpdateTab();
            HandOpenChanged?.Invoke();
        }

        private void UpdateTab()
        {
            if (_tabBg == null || _tabBg.isDisposed) return;
            var c = _handOpen ? new Color(0.769f, 0.063f, 0.353f, 1f) : Pink;   // 打开时颜色深一点
            _tabBg.DrawRoundRect(TabW * _k, TabH * _k, c, new[] { 0f, 12f * _k, 0f, 12f * _k });
        }

        /// <summary>直接设成收起 / 展开（启动时恢复记住的状态用：不动画、不触发 CollapsedChanged）。</summary>
        public void SetCollapsed(bool collapsed)
        {
            if (_collapsed == collapsed) return;
            _collapsed = collapsed;
            if (_side != null && !_side.isDisposed) _side.x = collapsed ? -_side.width : 0f;
            UpdateHandle();
        }

        public void Toggle()
        {
            _collapsed = !_collapsed;
            if (_side != null && !_side.isDisposed)
                _side.TweenMoveX(_collapsed ? -_side.width : 0f, 0.28f).SetEase(EaseType.CubicOut);
            UpdateHandle();
            CollapsedChanged?.Invoke();
        }

        private void UpdateHandle()
        {
            if (_handleText == null || _handleText.isDisposed) return;
            _handleText.text = _collapsed ? "›" : "‹";
            _handleText.SetXY((TabW * _k - _handleText.textWidth) / 2f - 1f, HitPadY * _k + (HandleH * _k - _handleText.textHeight) / 2f);
        }

        internal static GTextField Text(float size, Color color, bool bold)
        {
            return new GTextField
            {
                touchable = false,
                autoSize = AutoSizeType.Both,
                textFormat = new TextFormat { size = Mathf.Max(8, Mathf.RoundToInt(size)), color = color, bold = bold }
            };
        }

        public void Dispose()
        {
            foreach (var r in _rows) r.Dispose();
            _rows.Clear();
            if (_layer != null && !_layer.isDisposed) _layer.Dispose();
            _layer = null; _side = null; _border = null; _bg = null; _handle = null; _handleText = null; _tab = null; _tabBg = null; _hint = null; _hintKeys = null;
        }

        // ---------------- 一行 ----------------

        private sealed class RowView : IDisposable
        {
            public readonly GComponent Root = new GComponent { touchable = false };
            private readonly Icons _icons;
            private readonly float _k;
            private readonly GLoader _plate;
            private readonly UI.TextureLoader _head;
            private readonly GGraph _meBg;
            private readonly GTextField _me;
            private readonly GLoader[] _stars = new GLoader[3];
            private readonly GLoader _skill;
            private readonly GGraph _cdBg;
            private readonly GTextField _cd;
            private readonly GLoader _heart, _coin, _card;
            private readonly GTextField _hp, _hpMax, _gold, _hand;
            // 关键筹码（装了「关键筹码置顶」才有）：品质底图 + 筹码图标
            private readonly GLoader[] _relicBase;
            private readonly UI.TextureLoader[] _relicImg;
            private readonly GGraph[] _buffBg = new GGraph[MaxBuffs];
            private readonly UI.TextureLoader[] _buff = new UI.TextureLoader[MaxBuffs];
            private readonly GLoader[] _buffLocal = new GLoader[MaxBuffs];
            private readonly GGraph[] _layerBg = new GGraph[MaxBuffs];
            private readonly GTextField[] _layer = new GTextField[MaxBuffs];
            private string _lastKey;

            public RowView(Icons icons, float k, bool pinnedLayout)
            {
                _icons = icons;
                _k = k;
                Root.SetSize(Width * k, RowH * k);
                float plateY = 4f * k;

                _plate = Img(icons.Get("plate"), 0f, 0f, PlateW, PlateH, plateY);
                _head = new UI.TextureLoader { touchable = false, fill = FillType.ScaleMatchHeight, align = AlignType.Center, verticalAlign = VertAlignType.Bottom };
                // 头像比底板高一截、底边对齐，脑袋探出底板上沿（和官方左栏一样）
                _head.SetSize(HeadSize * k, HeadSize * k);
                _head.SetXY(0f, plateY + (PlateH - HeadSize) * k);
                Root.AddChild(_head);

                _meBg = new GGraph { touchable = false };
                _meBg.SetXY(4f * k, plateY + 4f * k);
                Root.AddChild(_meBg);
                _me = Text(11f * k, Color.white, true);
                _me.text = "我";
                _me.SetXY(8f * k, plateY + 4f * k);
                Root.AddChild(_me);
                _meBg.DrawRoundRect(_me.textWidth + 8f * k, _me.textHeight, Pink, new[] { 5f * k, 5f * k, 5f * k, 5f * k });

                // 三颗星：第一颗压在最上面，所以倒着加
                float starY = plateY + PlateH * k + 6f * k;
                for (int i = 2; i >= 0; i--) _stars[i] = Img(null, 6f + i * StarStep, 0f, StarSize, StarSize, starY);

                float skillX = (PlateW - SkillSize - 6f) * k, skillY = starY - 3f * k;
                _skill = Img(icons.Get("skill"), 0f, 0f, SkillSize, SkillSize);
                _skill.SetXY(skillX, skillY);
                _cdBg = new GGraph { touchable = false };
                Root.AddChild(_cdBg);
                _cd = Text(13f * k, Soft, true);
                Root.AddChild(_cd);

                float lineY = 26f * k;
                _heart = Img(icons.Get("heart"), ColX, 0f, 20f, 20f, lineY + 3f * k);
                _hp = Text(21f * k, Color.white, true); Root.AddChild(_hp);
                _hpMax = Text(14f * k, Muted, true); Root.AddChild(_hpMax);   // 「/上限」：小一号、灰色，紧跟在血量后面
                _coin = Img(icons.Get("coin"), 0f, 0f, 20f, 20f, lineY + 3f * k);
                _gold = Text(21f * k, Color.white, true); Root.AddChild(_gold);
                _card = Img(icons.Get("card"), 0f, 0f, 20f, 20f, lineY + 3f * k);
                _hand = Text(21f * k, Color.white, true); Root.AddChild(_hand);

                // 关键筹码一行：在第一行和 buff 之间（只有装了「关键筹码置顶」才建）
                int relics = pinnedLayout ? MaxRelics : 0;
                _relicBase = new GLoader[relics];
                _relicImg = new UI.TextureLoader[relics];
                for (int i = 0; i < relics; i++)
                {
                    _relicBase[i] = new GLoader { touchable = false, fill = FillType.ScaleFree, visible = false };
                    Root.AddChild(_relicBase[i]);
                    _relicImg[i] = new UI.TextureLoader { touchable = false, fill = FillType.ScaleFree, visible = false };
                    Root.AddChild(_relicImg[i]);
                }

                float buffY = (pinnedLayout ? BuffYPinned : BuffY) * k;
                for (int i = 0; i < MaxBuffs; i++)
                {
                    float x = (ColX + i * (BuffSize + BuffGap)) * k;
                    _buffBg[i] = new GGraph { touchable = false };
                    _buffBg[i].SetXY(x, buffY);
                    _buffBg[i].DrawRoundRect(BuffSize * k, BuffSize * k, Color.clear, Corners(7f * k));   // buff 直接放在面板底色上：底块透明
                    Root.AddChild(_buffBg[i]);
                    _buff[i] = new UI.TextureLoader { touchable = false, fill = FillType.ScaleFree };
                    _buff[i].SetSize(22f * k, 22f * k);
                    _buff[i].SetXY(x + 3f * k, buffY + 3f * k);
                    Root.AddChild(_buff[i]);
                    _buffLocal[i] = Img(null, 0f, 0f, 20f, 20f);
                    _buffLocal[i].SetXY(x + 4f * k, buffY + 4f * k);
                    _layerBg[i] = new GGraph { touchable = false };
                    Root.AddChild(_layerBg[i]);
                    _layer[i] = Text(10f * k, Color.white, true);
                    Root.AddChild(_layer[i]);
                }
            }

            public void Update(PlayerRow d)
            {
                string key = d.Key() + "|" + d.IsSelf;
                if (key == _lastKey) return;
                _lastKey = key;
                float k = _k;

                // 头像 + 楼层色底板；被击倒变灰
                _plate.color = Floor[Mathf.Clamp(d.Slot, 0, 3)];
                if (_head.url != d.HeadUrl) _head.url = d.HeadUrl;
                _plate.grayed = d.Knocked;
                _head.grayed = d.Knocked;
                _me.visible = _meBg.visible = d.IsSelf;

                for (int i = 0; i < 3; i++) _stars[i].texture = _icons.Get(i < d.Star ? "star_on" : "star_off");

                // 技能：好了就亮，没好变灰，右下角叠剩余回合
                bool ready = d.SkillCd <= 0;
                _skill.grayed = !ready;
                _skill.alpha = ready ? 1f : 0.75f;
                _cd.visible = _cdBg.visible = !ready;
                if (!ready)
                {
                    _cd.text = d.SkillCd.ToString();
                    _cd.color = d.SkillCd == 1 ? Soon : Soft;
                    float cx = _skill.x + _skill.width - _cd.textWidth + 4f * k, cy = _skill.y + _skill.height - _cd.textHeight + 4f * k;
                    _cd.SetXY(cx, cy);
                    _cdBg.SetXY(cx - 2f * k, cy);
                    _cdBg.DrawRoundRect(_cd.textWidth + 4f * k, _cd.textHeight, WellColor, Corners(5f * k));
                }

                // 第一行：♥ 血量/上限  星币  手牌（数字排在图标后面，按文字宽度往后挪）
                bool low = d.MaxHp > 0 && d.Hp * 10 <= d.MaxHp * 3;
                _hp.text = d.Hp.ToString();
                _hp.color = d.Knocked ? Muted : (low ? Danger : Color.white);
                _hpMax.visible = d.MaxHp > 0;
                _hpMax.text = d.MaxHp > 0 ? "/" + d.MaxHp : "";
                _gold.text = d.Gold.ToString();
                _hand.text = d.Hand.ToString();
                // 加了血量上限后，星币三位数时一行可能放不下：放不下就把这一行的数字一起缩小，最小 15
                int size = Mathf.RoundToInt(21f * k), min = Mathf.RoundToInt(15f * k);
                SetSize(size);
                while (LayoutLine() > ColRight * k && size > min) SetSize(--size);

                // 关键筹码
                ShowRelics(d.Pinned);

                // 第二行：离线/挂机标记 + buff，放不下的不显示
                int slot = 0;
                if (d.Away) { ShowBuff(slot++, null, _icons.Get("pc"), 1, null); }
                foreach (var b in d.Buffs) { if (slot >= MaxBuffs) break; ShowBuff(slot++, b.Icon, null, b.Layers, TipText.Buff(b.Id, b.Layers, b.KeepRound)); }
                for (; slot < MaxBuffs; slot++) HideBuff(slot);
            }

            private void SetSize(int size)
            {
                foreach (var t in new[] { _hp, _gold, _hand })
                {
                    if (t.textFormat.size == size) continue;
                    var tf = t.textFormat; tf.size = size; t.textFormat = tf;
                }
            }

            /// <summary>摆第一行，返回最右边的位置。</summary>
            private float LayoutLine()
            {
                float k = _k, x = ColX * k, y = 24f * k;
                float end = Place(_heart, _hp, x, y);
                if (_hpMax.visible)
                {
                    // 和血量数字底边对齐
                    _hpMax.SetXY(end + 1f * k, _hp.y + _hp.textHeight - _hpMax.textHeight - 2f * k);
                    end = _hpMax.x + _hpMax.textWidth;
                }
                x = Place(_coin, _gold, end + 9f * k, y) + 9f * k;
                return Place(_card, _hand, x, y);
            }

            private float Place(GLoader icon, GTextField text, float x, float y)
            {
                icon.SetXY(x, y + 3f * _k);
                text.SetXY(x + 22f * _k, y + (24f * _k - text.textHeight) / 2f);
                return text.x + text.textWidth;
            }

            /// <summary>
            /// 关键筹码排成一行（顺序照「关键筹码置顶」）。默认 26 一个，放不下就一起缩小到刚好放下（和它自己的做法一样）。
            /// 底图用右栏那套品质底图（data/relicbase_*.png），悬停显示筹码名字和说明。
            /// </summary>
            private void ShowRelics(List<int> ids)
            {
                int count = ids == null ? 0 : Math.Min(ids.Count, _relicBase.Length);
                float k = _k, size = RelicSize * k, gap = RelicGap * k;
                float room = (ColRight - ColX) * k;
                float width = count * size + Math.Max(0, count - 1) * gap;
                float scale = width > room && width > 0f ? room / width : 1f;
                float s = size * scale, g = gap * scale, inner = s * 0.83f;
                float y = RelicY * k + (size - s) / 2f;
                for (int i = 0; i < _relicBase.Length; i++)
                {
                    bool show = i < count;
                    _relicBase[i].visible = _relicImg[i].visible = show;
                    _relicBase[i].data = null;
                    if (!show) continue;
                    string icon, quality;
                    RelicLook.Info(ids[i], out icon, out quality);
                    float x = ColX * k + i * (s + g);
                    _relicBase[i].texture = _icons.Get("relicbase_" + quality);
                    _relicBase[i].SetSize(s, s);
                    _relicBase[i].SetXY(x, y);
                    _relicBase[i].data = TipText.Relic(ids[i]);   // 悬停小字挂在整枚筹码上
                    _relicImg[i].SetSize(inner, inner);
                    _relicImg[i].SetXY(x + (s - inner) / 2f, y + (s - inner) / 2f);
                    if (_relicImg[i].url != icon) _relicImg[i].url = icon;
                }
            }

            private void ShowBuff(int i, string url, NTexture local, int layers, Tip tip)
            {
                _buffBg[i].visible = true;
                _buffBg[i].data = tip;   // 悬停小字挂在这个 28×28 的格子上
                _buff[i].visible = url != null;
                if (url != null && _buff[i].url != url) _buff[i].url = url;
                _buffLocal[i].visible = local != null;
                _buffLocal[i].texture = local;
                bool showN = layers > 1;
                _layer[i].visible = _layerBg[i].visible = showN;
                if (showN)
                {
                    _layer[i].text = layers.ToString();
                    float bx = _buffBg[i].x + BuffSize * _k - _layer[i].textWidth - 2f * _k, by = _buffBg[i].y + BuffSize * _k - _layer[i].textHeight;
                    _layer[i].SetXY(bx, by);
                    _layerBg[i].SetXY(bx - 2f * _k, by);
                    _layerBg[i].DrawRoundRect(_layer[i].textWidth + 4f * _k, _layer[i].textHeight, new Color(0, 0, 0, 0.75f), Corners(4f * _k));
                }
            }

            private void HideBuff(int i)
            {
                _buffBg[i].visible = _buff[i].visible = _buffLocal[i].visible = _layer[i].visible = _layerBg[i].visible = false;
                _buffBg[i].data = null;
            }

            private GLoader Img(NTexture tex, float x, float y, float w, float h, float absY = -1f)
            {
                var g = new GLoader { touchable = false, fill = FillType.ScaleFree, texture = tex };
                g.SetSize(w * _k, h * _k);
                g.SetXY(x * _k, absY >= 0f ? absY : y * _k);
                Root.AddChild(g);
                return g;
            }

            private static float[] Corners(float r) { return new[] { r, r, r, r }; }

            public void Dispose()
            {
                if (!Root.isDisposed) Root.Dispose();
            }
        }

        private static float[] Corners(float r) { return new[] { r, r, r, r }; }
    }
}
