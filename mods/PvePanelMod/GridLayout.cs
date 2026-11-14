using System;
using System.Collections.Generic;

namespace PvePanelMod.Map
{
    /// <summary>一格的原始数据：编号、世界坐标（俯视图的 X / Z）、相连的格子。</summary>
    internal struct LandPoint
    {
        public int Id;
        public float X, Z;
        public int[] Adjacent;

        public LandPoint(int id, float x, float z, params int[] adjacent)
        {
            Id = id; X = x; Z = z; Adjacent = adjacent ?? new int[0];
        }
    }

    /// <summary>排好的方格：每格在第几列、第几行（0 起，左上角是 0,0）。</summary>
    internal sealed class GridLayout
    {
        public int Cols, Rows;
        public float Step;
        public readonly Dictionary<int, int> Col = new Dictionary<int, int>();
        public readonly Dictionary<int, int> Row = new Dictionary<int, int>();
        /// <summary>两格算到同一个方格里（地图不是方格排的）的次数；不为 0 时画出来会叠在一起。</summary>
        public int Collisions;
        /// <summary>相连的两格，每对只记一次（小编号在前）。</summary>
        public readonly List<KeyValuePair<int, int>> Edges = new List<KeyValuePair<int, int>>();

        /// <summary>
        /// 世界坐标 → 方格。wiki 的地图就是游戏坐标直接排成的方格（实测 82017「森秋幻境」56 格逐格对上）：
        /// 间距取「相连两格在 X 或 Z 上的较大差值」的中位数（约 30），列 = (X - 最小X) / 间距，行 = (最大Z - Z) / 间距，四舍五入。
        /// 手摆的格子有零点几的误差，四舍五入正好吃掉；斜着相连的格子落在对角，和 wiki 一样只碰一个角。
        /// </summary>
        public static GridLayout Build(IList<LandPoint> lands)
        {
            var g = new GridLayout();
            if (lands == null || lands.Count == 0) return g;

            var byId = new Dictionary<int, LandPoint>();
            foreach (var l in lands) byId[l.Id] = l;

            var gaps = new List<float>();
            var seen = new HashSet<long>();
            foreach (var l in lands)
            {
                foreach (int other in l.Adjacent)
                {
                    LandPoint o;
                    if (other == l.Id || !byId.TryGetValue(other, out o)) continue;
                    int a = Math.Min(l.Id, other), b = Math.Max(l.Id, other);
                    if (!seen.Add(((long)a << 32) | (uint)b)) continue;
                    g.Edges.Add(new KeyValuePair<int, int>(a, b));
                    float d = Math.Max(Math.Abs(l.X - o.X), Math.Abs(l.Z - o.Z));
                    if (d > 0.001f) gaps.Add(d);
                }
            }
            gaps.Sort();
            g.Step = gaps.Count > 0 ? gaps[gaps.Count / 2] : 30f;
            if (g.Step < 0.001f) g.Step = 30f;

            float minX = float.MaxValue, maxZ = float.MinValue;
            foreach (var l in lands) { if (l.X < minX) minX = l.X; if (l.Z > maxZ) maxZ = l.Z; }

            var used = new HashSet<long>();
            foreach (var l in lands)
            {
                int c = (int)Math.Round((l.X - minX) / g.Step, MidpointRounding.AwayFromZero);
                int r = (int)Math.Round((maxZ - l.Z) / g.Step, MidpointRounding.AwayFromZero);
                g.Col[l.Id] = c;
                g.Row[l.Id] = r;
                if (!used.Add(((long)r << 32) | (uint)c)) g.Collisions++;
                if (c + 1 > g.Cols) g.Cols = c + 1;
                if (r + 1 > g.Rows) g.Rows = r + 1;
            }
            return g;
        }

        /// <summary>
        /// 同一格里站了 count 个单位时，第 index 个放在哪（相对格子中心，单位 = 格子边长）以及多大。
        /// 1 个：居中、大；2～4 个：2×2 四个角；更多：前 3 个占三个角，第四个角留给「+N」。
        /// </summary>
        public static void UnitSlot(int index, int count, out float dx, out float dy, out float size)
        {
            if (count <= 1) { dx = 0f; dy = 0f; size = 0.8f; return; }
            size = 0.5f;
            int i = Math.Min(index, 3);
            dx = (i % 2 == 0) ? -0.25f : 0.25f;
            dy = (i < 2) ? -0.25f : 0.25f;
        }

        /// <summary>
        /// 手指下面是哪一格：col / row 是以格子边长为单位的位置（左上角 0,0，格子中心在 n + 0.5）。
        /// 离最近的格子中心超过 maxDistance（单位同样是格子边长）就算点在空白处，返回 -1。
        /// </summary>
        public static int Nearest(GridLayout g, float col, float row, float maxDistance)
        {
            if (g == null) return -1;
            int best = -1;
            float bestD = maxDistance * maxDistance;
            foreach (var kv in g.Col)
            {
                float dx = kv.Value + 0.5f - col, dy = g.Row[kv.Key] + 0.5f - row;
                float d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = kv.Key; }
            }
            return best;
        }

        /// <summary>同一格最多画几个头像（剩下的写成「+N」）。</summary>
        public static int VisibleUnits(int count) => count <= 4 ? count : 3;
    }
}
