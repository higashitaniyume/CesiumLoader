using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Core.Unit;
using FairyGUI;
using Tools;
using UI;
using UnityEngine;

namespace PvePanelMod.Map
{
    /// <summary>
    /// 方格地图：点开官方小地图（顶部灰色按钮）时，把里面斜着的圆点换成 wiki 那种方格图。
    /// 画在官方小地图组件（UIBattleInfo_Com_Minimap）里面，打开 / 关闭动画、位置、层级都跟着官方走。
    ///   格子：游戏自己的格子图标（UT_Platform_*，按当前类型，对局里变了跟着变），左上角小字是格子编号（和右栏波次表的编号一致）；
    ///   出生点：楼层色圆 + P1～P4；
    ///   头像：照抄官方小地图每个头像的「谁、在第几格、头像图」，玩家按楼层色描边，怪物红色，Boss 紫色。
    /// 官方的圆点和头像只是调成透明、不接点击，关掉方格地图（配置 GridMap）或面板时原样恢复。
    /// </summary>
    internal sealed class GridMap : IDisposable
    {
        private const float Pad = 12f;
        private static readonly Color PathColor = new Color(0.239f, 0.192f, 0.216f, 0.97f);
        private static readonly Color PathEdge = new Color(0f, 0f, 0f, 0.35f);
        private static readonly Color IdColor = new Color(1f, 1f, 1f, 0.85f);
        private static readonly Color MonsterRed = new Color(1f, 0.302f, 0.302f, 1f);
        private static readonly Color BossPurple = new Color(0.62f, 0.36f, 1f, 1f);
        private static readonly Color Dark = new Color(0.086f, 0.098f, 0.157f, 1f);
        private static readonly Color[] Floor =
        {
            new Color(1f, 0.275f, 0.275f), new Color(0.58f, 1f, 0.275f), new Color(0.263f, 0.525f, 0.957f), new Color(1f, 0.702f, 0.275f)
        };

        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static FieldInfo _fLand, _fPid;
        private static bool _fieldsResolved;

        private UIBattleInfo_Com_Minimap _host;
        private GComponent _layer, _tiles, _units;
        private GridLayout _grid;
        private string _tilesKey, _unitsKey;
        private float _cell, _ox, _oy;
        private readonly Dictionary<int, TileView> _tileViews = new Dictionary<int, TileView>();
        private readonly List<UnitView> _unitPool = new List<UnitView>();
        private readonly List<GTextField> _morePool = new List<GTextField>();
        /// <summary>被调成透明的官方元素，和它们原来能不能点。</summary>
        private readonly Dictionary<GObject, bool> _hidden = new Dictionary<GObject, bool>();
        private Dictionary<LandType, string> _landNames;
        private List<PlayerRow> _rows;
        private bool _loggedCollision;
        private readonly Action<string> _warn;

        public GridMap(Action<string> warn) { _warn = warn; }

        /// <summary>每帧调用（很省）：官方小地图开着时，把新冒出来的官方头像也藏起来，并保证方格图在最上面。</summary>
        public void Frame(bool active)
        {
            if (!active || _host == null || _host.isDisposed) return;
            if (!_host.visible) return;
            HideOfficial();
            if (_layer != null && !_layer.isDisposed && _host.GetChildIndex(_layer) != _host.numChildren - 1)
                _host.SetChildIndex(_layer, _host.numChildren - 1);
        }

        /// <summary>每 0.25 秒：找官方小地图，开着就刷新格子和头像。</summary>
        public void Tick(bool active, List<PlayerRow> rows)
        {
            _rows = rows;
            var host = PanelData.FindPanel()?.com_MapInfo?.com_Minimap;
            if (!active || host == null || host.isDisposed) { Restore(); return; }
            if (host != _host) { Restore(); DisposeLayer(); _host = host; }
            if (!host.visible) return;   // 地图没打开：什么都不用做（官方元素保持透明，下次打开不闪）

            EnsureLayer();
            if (!_layer.visible) _layer.visible = true;
            HideOfficial();
            RefreshTiles();
            RefreshUnits();
        }

        // ---------------- 骨架 ----------------

        private void EnsureLayer()
        {
            if (_layer != null && !_layer.isDisposed && _layer.parent == _host) return;
            DisposeLayer();
            // 整块接住点击：官方是按「斜着的小地图」上的位置换算镜头位置的，点到方格图上会算错，所以不能让点击漏给官方
            _layer = new GComponent { name = "PvePanelGridMap", touchable = true, opaque = true };
            _layer.SetSize(_host.width, _host.height);
            _layer.onTouchBegin.Add(OnTouchBegin);
            _layer.onTouchMove.Add(OnTouchMove);
            _layer.onTouchEnd.Add(OnTouchEnd);
            _layer.onClick.Add(Swallow);
            _layer.onRightClick.Add(Swallow);
            _tiles = new GComponent { touchable = false };
            _units = new GComponent { touchable = false };
            _layer.AddChild(_tiles);
            _layer.AddChild(_units);
            _host.AddChild(_layer);
        }

        // ---------------- 格子 ----------------

        private void RefreshTiles()
        {
            var lands = SimpleSingletonProvider<LandManager>.inst?.NodeDict;
            if (lands == null || lands.Count == 0) return;

            // 格子类型、图标在对局里会变（如「事件」变「疾行」），连同数量一起当成「要不要重画」的依据
            var sb = new StringBuilder();
            foreach (var kv in lands)
            {
                var l = kv.Value;
                if (l == null) continue;
                sb.Append(kv.Key).Append(':').Append((int)l.LandType).Append(':').Append(SafeIcon(l)).Append(';');
            }
            string key = sb.ToString();
            if (key == _tilesKey)
            {
                foreach (var t in _tileViews.Values) t.CheckLoaded();
                return;
            }
            bool sameMap = _grid != null && _tilesKey != null && SameIds(lands);
            _tilesKey = key;
            _unitsKey = null;

            if (!sameMap)
            {
                var points = new List<LandPoint>();
                foreach (var kv in lands)
                {
                    var l = kv.Value;
                    if (l == null) continue;
                    var adj = l.AdjacencyLandIds;
                    points.Add(new LandPoint(kv.Key, l.X, l.Z, adj != null ? adj.ToArray() : new int[0]));
                }
                _grid = GridLayout.Build(points);
                if (_grid.Collisions > 0 && !_loggedCollision)
                {
                    _loggedCollision = true;
                    _warn?.Invoke("PVE 面板方格地图：有 " + _grid.Collisions + " 格算到了同一个位置（这张图可能不是方格排的），会叠在一起显示");
                }
                float w = _host.width, h = _host.height;
                _cell = Mathf.Min((w - 2 * Pad) / Mathf.Max(1, _grid.Cols), (h - 2 * Pad) / Mathf.Max(1, _grid.Rows));
                _ox = (w - _cell * _grid.Cols) / 2f;
                _oy = (h - _cell * _grid.Rows) / 2f;
            }

            _tiles.RemoveChildren(0, -1, true);
            _tileViews.Clear();
            DrawPaths(lands);
            foreach (var kv in lands)
            {
                if (kv.Value == null || !_grid.Col.ContainsKey(kv.Key)) continue;
                _tileViews[kv.Key] = new TileView(_tiles, kv.Key, kv.Value, Center(kv.Key), _cell, LandName(kv.Value.LandType));
            }
        }

        private bool SameIds(Dictionary<int, UnitLand> lands)
        {
            if (lands.Count != _grid.Col.Count) return false;
            foreach (var id in lands.Keys) if (!_grid.Col.ContainsKey(id)) return false;
            return true;
        }

        /// <summary>wiki 那条深色的「路」：每格底下一个方块，相连的格子之间连一条带（斜着相连的窄一点，免得糊成一片）。</summary>
        private void DrawPaths(Dictionary<int, UnitLand> lands)
        {
            float band = _cell * 0.96f;
            foreach (var e in _grid.Edges)
            {
                Vector2 a = Center(e.Key), b = Center(e.Value);
                float dx = b.x - a.x, dy = b.y - a.y;
                float len = Mathf.Sqrt(dx * dx + dy * dy);
                if (len < 0.5f) continue;
                bool diagonal = Mathf.Abs(dx) > 1f && Mathf.Abs(dy) > 1f;
                float thick = diagonal ? band * 0.5f : band;
                var g = new GGraph { touchable = false };
                g.DrawRect(len, thick, 0, Color.clear, PathColor);
                g.SetPivot(0.5f, 0.5f, true);
                g.SetXY((a.x + b.x) / 2f, (a.y + b.y) / 2f);
                g.rotation = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                _tiles.AddChild(g);
            }
            foreach (var kv in lands)
            {
                if (kv.Value == null || !_grid.Col.ContainsKey(kv.Key)) continue;
                Vector2 c = Center(kv.Key);
                var g = new GGraph { touchable = false };
                g.DrawRect(band, band, 1, PathEdge, PathColor);
                g.SetXY(c.x - band / 2f, c.y - band / 2f);
                _tiles.AddChild(g);
            }
        }

        // ---------------- 按住方格图：镜头移到手指下面那一格 ----------------

        private bool _pressing;
        private int _focused = -1;
        private bool _loggedFocus;

        private static void Swallow(EventContext context) { context.StopPropagation(); }

        private void OnTouchBegin(EventContext context)
        {
            context.StopPropagation();
            context.CaptureTouch();
            _pressing = true;
            _focused = -1;
            FocusAt(context);
        }

        private void OnTouchMove(EventContext context)
        {
            context.StopPropagation();
            if (_pressing) FocusAt(context);
        }

        private void OnTouchEnd(EventContext context)
        {
            context.StopPropagation();
            _pressing = false;
            _focused = -1;
        }

        private void FocusAt(EventContext context)
        {
            if (_grid == null || _layer == null || _layer.isDisposed || context.inputEvent == null) return;
            Vector2 p = _layer.GlobalToLocal(context.inputEvent.position);
            int land = GridLayout.Nearest(_grid, (p.x - _ox) / _cell, (p.y - _oy) / _cell, 0.75f);
            if (land < 0 || land == _focused) return;
            _focused = land;
            var l = SimpleSingletonProvider<LandManager>.inst?.GetLandById(land);
            var cam = SimpleSingletonProvider<Core.Camera.CameraManager>.inst;
            if (l == null || cam == null) return;
            cam.ControlFreeCamera(new Vector3(l.X, l.Y, l.Z));
            if (!_loggedFocus)
            {
                _loggedFocus = true;
                _warn?.Invoke("PVE 面板方格地图：按住地图，镜头移到 " + land + " 号格（之后不再记日志）");
            }
        }

        private Vector2 Center(int id)
        {
            return new Vector2(_ox + (_grid.Col[id] + 0.5f) * _cell, _oy + (_grid.Row[id] + 0.5f) * _cell);
        }

        private static string SafeIcon(UnitLand l)
        {
            try { return l.GetLandIcon(); } catch { return null; }
        }

        private string LandName(LandType type)
        {
            if (_landNames == null)
            {
                _landNames = new Dictionary<LandType, string>();
                try
                {
                    var dict = StaticConfigure.Land?.InfoDict;
                    if (dict != null)
                        foreach (var kv in dict)
                        {
                            var c = kv.Value;
                            if (c == null || _landNames.ContainsKey(c.LandType)) continue;
                            string n = null;
                            try { n = UIHelper.GetLocal(c.NameID, UIStringType.Land); } catch { }
                            if (!string.IsNullOrEmpty(n)) _landNames[c.LandType] = n;
                        }
                }
                catch { }
            }
            string name;
            return _landNames.TryGetValue(type, out name) ? name : null;
        }

        // ---------------- 头像 ----------------

        private struct Unit
        {
            public long Id;
            public int Land;
            public string Url;
            public int Order;      // 0 玩家、1 Boss、2 怪物：同一格里玩家排前面
            public Color Ring;
            public bool Self;
        }

        private void RefreshUnits()
        {
            if (_grid == null) return;
            ResolveFields();
            var list = new List<Unit>();
            for (int i = 0; i < _host.numChildren; i++)
            {
                var ic = _host.GetChildAt(i) as UIBattleInfo_Com_MiniMap_Icon;
                if (ic == null || ic.isDisposed || !ic.visible) continue;
                var u = new Unit();
                try
                {
                    u.Land = _fLand != null ? Convert.ToInt32(_fLand.GetValue(ic)) : -1;
                    u.Id = _fPid != null ? Convert.ToInt64(_fPid.GetValue(ic)) : 0;
                }
                catch { continue; }
                if (!_grid.Col.ContainsKey(u.Land)) continue;
                u.Url = ic.loader_Head?.url;
                int ctrl = ic.player != null ? ic.player.selectedIndex : -1;
                var row = FindRow(u.Id);
                if (row != null)
                {
                    u.Order = 0;
                    u.Ring = Floor[Mathf.Clamp(row.Slot, 0, Floor.Length - 1)];
                    u.Self = row.IsSelf;
                }
                else if (ctrl == 6) { u.Order = 1; u.Ring = BossPurple; }   // 实测：玩家 0～3、怪物 4、Boss 6
                else { u.Order = 2; u.Ring = MonsterRed; }
                list.Add(u);
            }
            list.Sort((a, b) => a.Land != b.Land ? a.Land.CompareTo(b.Land) : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Id.CompareTo(b.Id));

            var sb = new StringBuilder();
            foreach (var u in list) sb.Append(u.Id).Append('@').Append(u.Land).Append(':').Append(u.Url).Append(';');
            string key = sb.ToString();
            if (key == _unitsKey) return;
            _unitsKey = key;

            int used = 0, moreUsed = 0;
            for (int i = 0; i < list.Count;)
            {
                int land = list[i].Land, j = i;
                while (j < list.Count && list[j].Land == land) j++;
                int count = j - i, shown = GridLayout.VisibleUnits(count);
                Vector2 c = Center(land);
                for (int n = 0; n < shown; n++)
                {
                    float dx, dy, size;
                    GridLayout.UnitSlot(n, count, out dx, out dy, out size);
                    if (used >= _unitPool.Count) _unitPool.Add(new UnitView(_units));
                    _unitPool[used++].Set(list[i + n], c.x + dx * _cell, c.y + dy * _cell, size * _cell);
                }
                if (count > shown)
                {
                    float dx, dy, size;
                    GridLayout.UnitSlot(3, count, out dx, out dy, out size);
                    if (moreUsed >= _morePool.Count)
                    {
                        var t = LeftPanel.Text(12f, Color.white, true);
                        t.textFormat = Outlined(t.textFormat);
                        _units.AddChild(t);
                        _morePool.Add(t);
                    }
                    var more = _morePool[moreUsed++];
                    more.textFormat = Sized(more.textFormat, _cell * 0.3f);
                    more.text = "+" + (count - shown);
                    more.SetXY(c.x + dx * _cell - more.textWidth / 2f, c.y + dy * _cell - more.textHeight / 2f);
                    more.visible = true;
                }
                i = j;
            }
            for (int i = used; i < _unitPool.Count; i++) _unitPool[i].Hide();
            for (int i = moreUsed; i < _morePool.Count; i++) _morePool[i].visible = false;
        }

        private PlayerRow FindRow(long id)
        {
            if (_rows == null) return null;
            foreach (var r in _rows) if (r.Id == id) return r;
            return null;
        }

        private static void ResolveFields()
        {
            if (_fieldsResolved) return;
            _fieldsResolved = true;
            try
            {
                var t = typeof(UIBattleInfo_Com_MiniMap_Icon);
                _fLand = t.GetField("_LandID", F);
                _fPid = t.GetField("playerID", F);
            }
            catch { }
        }

        // ---------------- 官方圆点、头像：透明 + 不接点击 ----------------

        private void HideOfficial()
        {
            for (int i = 0; i < _host.numChildren; i++)
            {
                var o = _host.GetChildAt(i);
                if (!(o is UIBattleInfo_Com_MiniMap_Icon) && !(o is UIBattleInfo_Com_MiniMap_Land)) continue;
                if (!_hidden.ContainsKey(o)) _hidden[o] = o.touchable;
                if (o.alpha != 0f) o.alpha = 0f;
                if (o.touchable) o.touchable = false;
            }
        }

        private void Restore()
        {
            foreach (var kv in _hidden)
            {
                var o = kv.Key;
                if (o == null || o.isDisposed) continue;
                o.alpha = 1f;
                o.touchable = kv.Value;
            }
            _hidden.Clear();
            if (_layer != null && !_layer.isDisposed) _layer.visible = false;
            _tilesKey = null;
            _unitsKey = null;
        }

        public void Hide() { Restore(); }

        private void DisposeLayer()
        {
            _tileViews.Clear();
            _unitPool.Clear();
            _morePool.Clear();
            if (_layer != null && !_layer.isDisposed) _layer.Dispose();
            _layer = null; _tiles = null; _units = null;
            _grid = null;
            _tilesKey = null;
            _unitsKey = null;
        }

        public void Dispose()
        {
            Restore();
            DisposeLayer();
            _host = null;
        }

        internal static TextFormat Outlined(TextFormat f)
        {
            f.outline = 1.5f;
            f.outlineColor = new Color(0f, 0f, 0f, 0.85f);
            return f;
        }

        internal static TextFormat Sized(TextFormat f, float size)
        {
            f.size = Mathf.Max(8, Mathf.RoundToInt(size));
            return f;
        }

        // ---------------- 一格 ----------------

        private sealed class TileView
        {
            private readonly UI.TextureLoader _icon;
            private readonly GComponent _fallback;
            private readonly GTextField _fallbackText;
            private readonly DateTime _since = DateTime.UtcNow;
            private bool _decided;

            public TileView(GComponent parent, int id, UnitLand land, Vector2 c, float cell, string name)
            {
                float s = cell * 0.84f;
                if (land.LandType == LandType.Born)
                {
                    // 出生点：楼层色圆 + P1～P4（wiki 也是这么画的；游戏里出生点的图标随角色变，不好认）
                    int slot = Mathf.Clamp(land.PlayerSerialNumber, 0, Floor.Length - 1);
                    parent.AddChild(Circle(c, s, Floor[slot]));
                    var t = LeftPanel.Text(cell * 0.36f, Color.white, true);
                    t.textFormat = Outlined(t.textFormat);
                    t.text = "P" + (slot + 1);
                    t.SetXY(c.x - t.textWidth / 2f, c.y - t.textHeight / 2f);
                    parent.AddChild(t);
                    _decided = true;
                }
                else
                {
                    // 图标加载不出来时的替补：灰圆 + 格子名字的第一个字。先藏着，2 秒后还没图再露出来
                    _fallback = Circle(c, s, FallbackColor(land.LandType));
                    _fallback.visible = false;
                    parent.AddChild(_fallback);
                    _fallbackText = LeftPanel.Text(cell * 0.4f, Color.white, true);
                    _fallbackText.textFormat = Outlined(_fallbackText.textFormat);
                    _fallbackText.text = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1);
                    _fallbackText.SetXY(c.x - _fallbackText.textWidth / 2f, c.y - _fallbackText.textHeight / 2f);
                    _fallbackText.visible = false;
                    parent.AddChild(_fallbackText);

                    _icon = new UI.TextureLoader { touchable = false, fill = FillType.ScaleFree, align = AlignType.Center, verticalAlign = VertAlignType.Middle };
                    _icon.SetSize(s, s);
                    _icon.SetXY(c.x - s / 2f, c.y - s / 2f);
                    parent.AddChild(_icon);
                    string url = SafeIcon(land);
                    if (!string.IsNullOrEmpty(url)) _icon.url = url;
                }

                var idText = LeftPanel.Text(cell * 0.26f, IdColor, true);
                idText.textFormat = Outlined(idText.textFormat);
                idText.text = id.ToString();
                idText.SetXY(c.x - cell * 0.48f, c.y - cell * 0.5f);
                parent.AddChild(idText);
            }

            public void CheckLoaded()
            {
                if (_decided || _icon == null || _icon.isDisposed) return;
                if (_icon.texture != null) { _decided = true; return; }
                if ((DateTime.UtcNow - _since).TotalSeconds < 2) return;
                _decided = true;
                _fallback.visible = true;
                _fallbackText.visible = true;
            }

            /// <summary>带深色描边的圆（GGraph 的圆没有描边参数，用大小两个圆叠出来）。</summary>
            private static GComponent Circle(Vector2 c, float s, Color fill)
            {
                var box = new GComponent { touchable = false };
                box.SetSize(s, s);
                box.SetXY(c.x - s / 2f, c.y - s / 2f);
                var outer = new GGraph { touchable = false };
                outer.DrawEllipse(s, s, Dark);
                box.AddChild(outer);
                float b = Mathf.Max(2f, s * 0.06f);
                var inner = new GGraph { touchable = false };
                inner.DrawEllipse(s - 2 * b, s - 2 * b, fill);
                inner.SetXY(b, b);
                box.AddChild(inner);
                return box;
            }

            private static Color FallbackColor(LandType t)
            {
                switch (t)
                {
                    case LandType.Monster: case LandType.BloodLoss: return new Color(0.85f, 0.2f, 0.25f);
                    case LandType.MonsterPursuit: case LandType.Pursuit: case LandType.MoveAgain: case LandType.Portal: case LandType.Jump: return new Color(0.15f, 0.55f, 0.95f);
                    case LandType.Event: case LandType.Destiny: return new Color(0.9f, 0.2f, 0.65f);
                    case LandType.RollGold: case LandType.Relic: return new Color(0.95f, 0.65f, 0.1f);
                    case LandType.Shop: case LandType.Pveshop: case LandType.DrawCard: case LandType.Heal: case LandType.PlantBuildingShop: return new Color(0.15f, 0.7f, 0.45f);
                    default: return new Color(0.45f, 0.47f, 0.55f);
                }
            }
        }

        // ---------------- 一个头像：彩色描边方块 + 官方头像图 ----------------

        private sealed class UnitView
        {
            private readonly GGraph _ring;
            private readonly UI.TextureLoader _head;
            private string _url;

            public UnitView(GComponent parent)
            {
                _ring = new GGraph { touchable = false };
                _head = new UI.TextureLoader { touchable = false, fill = FillType.ScaleFree, align = AlignType.Center, verticalAlign = VertAlignType.Middle };
                parent.AddChild(_ring);
                parent.AddChild(_head);
            }

            public void Set(Unit u, float cx, float cy, float size)
            {
                float line = Mathf.Max(2f, size * (u.Self ? 0.12f : 0.08f));
                _ring.DrawRect(size, size, Mathf.RoundToInt(line), u.Ring, Color.clear);   // 底色透明：头像本身是透明底，下面的格子图标能透出来
                _ring.SetXY(cx - size / 2f, cy - size / 2f);
                float inner = size - 2 * line;
                _head.SetSize(inner, inner);
                _head.SetXY(cx - inner / 2f, cy - inner / 2f);
                if (_url != u.Url) { _url = u.Url; _head.url = u.Url; }
                _ring.visible = true;
                _head.visible = true;
            }

            public void Hide()
            {
                _ring.visible = false;
                _head.visible = false;
            }
        }
    }
}
