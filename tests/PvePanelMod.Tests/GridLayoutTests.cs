using System.Collections.Generic;
using PvePanelMod.Map;
using Xunit;

namespace PvePanelMod.Tests
{
    public class GridLayoutTests
    {
        /// <summary>82017「森秋幻境」56 格的真实坐标和相连关系（2026-11-14 地图探针实测）。</summary>
        private static List<LandPoint> Autumn()
        {
            return new List<LandPoint>
            {
                new LandPoint(0, -120.125f, 164.23f, 37,1), // Born
                new LandPoint(1, -89.898f, 164.429f, 0,2), // MonsterPursuit
                new LandPoint(2, -60.655f, 165.106f, 1,3), // MoveAgain
                new LandPoint(3, -30.641f, 165.224f, 2,4), // Event
                new LandPoint(4, -0.549f, 165.372f, 3,5), // Monster
                new LandPoint(5, 29.255f, 165.613f, 4,6), // BloodLoss
                new LandPoint(6, 59.47f, 165.263f, 5,7), // Relic
                new LandPoint(7, 89.261f, 165.203f, 6,8), // MonsterPursuit
                new LandPoint(8, 119.763f, 164.76f, 7,9), // Born
                new LandPoint(9, 119.069f, 135.168f, 8,10), // RollGold
                new LandPoint(10, 119.838f, 104.973f, 9,11), // MoveAgain
                new LandPoint(11, 119.236f, 75.223f, 10,52,12), // Pveshop
                new LandPoint(12, 149.485f, 75.257f, 11,13), // RollGold
                new LandPoint(13, 179.133f, 45.541f, 12,14), // DrawCard
                new LandPoint(14, 180.026f, 13.551f, 13,15), // BloodLoss
                new LandPoint(15, 179.741f, -16.429f, 14,16), // Monster
                new LandPoint(16, 179.714f, -46.185f, 15,17), // Event
                new LandPoint(17, 179.947f, -75.716f, 16,18), // MoveAgain
                new LandPoint(18, 150.168f, -105.666f, 17,19), // Born
                new LandPoint(19, 119.905f, -135.579f, 18,20), // RollGold
                new LandPoint(20, 90.095f, -166.014f, 19,21), // MonsterPursuit
                new LandPoint(21, 60.117f, -165.948f, 20,22), // Pveshop
                new LandPoint(22, 29.979f, -166.258f, 21,23), // Event
                new LandPoint(23, 0.033f, -165.706f, 22,55,24), // Monster
                new LandPoint(24, -30.204f, -165.864f, 23,25), // BloodLoss
                new LandPoint(25, -59.697f, -165.834f, 24,26), // Relic
                new LandPoint(26, -90.677f, -166.18f, 25,27), // MonsterPursuit
                new LandPoint(27, -119.886f, -136.17f, 26,28), // RollGold
                new LandPoint(28, -150.032f, -105.602f, 27,29), // Born
                new LandPoint(29, -180.695f, -75.482f, 28,30), // DrawCard
                new LandPoint(30, -179.731f, -46.286f, 29,31), // Event
                new LandPoint(31, -180.416f, -16.233f, 30,32), // Monster
                new LandPoint(32, -180.811f, 14.53f, 31,33), // BloodLoss
                new LandPoint(33, -179.814f, 44.213f, 32,34), // MoveAgain
                new LandPoint(34, -149.81f, 73.902f, 35,33), // Heal
                new LandPoint(35, -119.573f, 74.06f, 34,36,38), // Pveshop
                new LandPoint(36, -120.226f, 104.598f, 35,37), // Event
                new LandPoint(37, -120.459f, 134.129f, 36,0), // DrawCard
                new LandPoint(38, -89.337f, 74.218f, 39,35), // Monster
                new LandPoint(39, -59.391f, 73.666f, 40,38), // RollGold
                new LandPoint(40, -60.355f, 44.469f, 41,39), // MonsterPursuit
                new LandPoint(41, -60.187f, 13.967f, 42,40), // BloodLoss
                new LandPoint(42, -60.181f, -15.303f, 43,41), // Monster
                new LandPoint(43, -60.272f, -46.029f, 44,42), // Heal
                new LandPoint(44, -30.035f, -45.871f, 45,43), // DrawCard
                new LandPoint(45, 0.234f, -45.228f, 46,44,53), // FillingStation
                new LandPoint(46, 30.633f, -46.302f, 47,45), // RollGold
                new LandPoint(47, 60.641f, -45.646f, 48,46), // MonsterPursuit
                new LandPoint(48, 59.357f, -16.081f, 49,47), // BloodLoss
                new LandPoint(49, 59.947f, 13.776f, 50,48), // Event
                new LandPoint(50, 60.237f, 43.451f, 51,49), // DrawCard
                new LandPoint(51, 60.255f, 74.24f, 52,50), // Heal
                new LandPoint(52, 90.31f, 73.413f, 11,51), // Monster
                new LandPoint(53, -0.001f, -75.95f, 54,45), // RollGold
                new LandPoint(54, -0.292f, -105.624f, 55,53), // MoveAgain
                new LandPoint(55, -0.012f, -136.231f, 23,54), // PlantBuildingShop
            };
        }

        [Fact]
        public void Autumn_MatchesWikiGrid()
        {
            var g = GridLayout.Build(Autumn());
            // wiki 图 715×660 = 13 列 × 12 行（每格 55 像素）
            Assert.Equal(13, g.Cols);
            Assert.Equal(12, g.Rows);
            Assert.Equal(0, g.Collisions);
            Assert.InRange(g.Step, 29f, 31f);
            // P1～P4 出生点在 wiki 上的位置
            AssertCell(g, 0, 0, 2);    // P1 左上
            AssertCell(g, 8, 0, 10);   // P2 右上
            AssertCell(g, 18, 9, 11);  // P3 右下
            AssertCell(g, 28, 9, 1);   // P4 左下
            // 中间的保障点、底部正中的植物建筑商店
            AssertCell(g, 45, 7, 6);
            AssertCell(g, 55, 10, 6);
        }

        [Fact]
        public void Autumn_DiagonalNeighborsTouchAtCorner()
        {
            var g = GridLayout.Build(Autumn());
            // 12 → 13 是楼梯：在 wiki 上只碰一个角
            Assert.Equal(1, System.Math.Abs(g.Col[13] - g.Col[12]));
            Assert.Equal(1, System.Math.Abs(g.Row[13] - g.Row[12]));
        }

        [Fact]
        public void Edges_AreCountedOnce()
        {
            var g = GridLayout.Build(Autumn());
            var seen = new HashSet<string>();
            foreach (var e in g.Edges)
            {
                Assert.True(e.Key < e.Value);
                Assert.True(seen.Add(e.Key + "-" + e.Value));
            }
            // 56 格各连 2 格，四个岔口（11、23、35、45）各多连 1 格：(56×2 + 4) / 2 = 58 条
            Assert.Equal(58, g.Edges.Count);
        }

        [Fact]
        public void Empty_DoesNotThrow()
        {
            var g = GridLayout.Build(new List<LandPoint>());
            Assert.Equal(0, g.Cols);
            Assert.Equal(0, g.Rows);
        }

        [Fact]
        public void SamePosition_IsReportedAsCollision()
        {
            var g = GridLayout.Build(new List<LandPoint>
            {
                new LandPoint(1, 0f, 0f, 2),
                new LandPoint(2, 30f, 0f, 1, 3),
                new LandPoint(3, 31f, 1f, 2),
            });
            Assert.Equal(1, g.Collisions);
        }

        [Fact]
        public void Nearest_FindsTileUnderFinger()
        {
            var g = GridLayout.Build(Autumn());
            Assert.Equal(0, GridLayout.Nearest(g, 2.5f, 0.5f, 0.75f));    // P1 正中
            Assert.Equal(1, GridLayout.Nearest(g, 3.9f, 0.2f, 0.75f));    // 1 号格偏右上，还算 1 号
            Assert.Equal(-1, GridLayout.Nearest(g, 6.5f, 5.5f, 0.75f));   // 地图中间的空地
            Assert.Equal(-1, GridLayout.Nearest(null, 0f, 0f, 0.75f));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(4, 4)]
        [InlineData(5, 3)]
        [InlineData(9, 3)]
        public void VisibleUnits_LeavesCornerForMore(int count, int shown)
        {
            Assert.Equal(shown, GridLayout.VisibleUnits(count));
        }

        [Fact]
        public void UnitSlot_FourCornersAreDistinct()
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < 4; i++)
            {
                GridLayout.UnitSlot(i, 4, out float dx, out float dy, out float size);
                Assert.Equal(0.5f, size);
                Assert.True(seen.Add(dx + "," + dy));
            }
        }

        private static void AssertCell(GridLayout g, int id, int row, int col)
        {
            Assert.Equal(row, g.Row[id]);
            Assert.Equal(col, g.Col[id]);
        }
    }
}
