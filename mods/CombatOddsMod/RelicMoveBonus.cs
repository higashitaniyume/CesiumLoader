using System.Collections.Generic;

namespace CombatOddsMod
{
    /// <summary>
    /// 会「加移速」的筹码及其常驻数值。
    ///
    /// 为什么要自己列这张表: 服务器把常驻移速(筹码等)直接算进投骰后的移动点数, 并不会把它
    /// 作为增量推给客户端 —— <c>BattleProperty.ExtraAddMovePoint</c> 只在 buff/临时效果变化时
    /// 才有值。实测(回放解包)玩家持有「速度轮滑-中级」时连续几次投骰都比骰点多 2 点, 而客户端
    /// 的加速值一直是 0。所以常驻部分要由本表补上, 临时部分仍以服务器推送为准。
    ///
    /// 只登记**无条件**生效的筹码; 有前置条件的(鸳鸯玉佩需要【鸳鸯连理】、能量回收需要层数、
    /// 跃迁引擎只加「下一次」)不在这里猜, 它们的实际增量会体现在投骰差值里。
    ///
    /// 纯逻辑(零游戏依赖), 由 CombatOddsMod.Tests 直接编译测试。
    /// </summary>
    internal static class RelicMoveBonus
    {
        internal struct Bonus
        {
            public int RelicId;
            public int Value;
            public string Name;
        }

        /// <summary>速度轮滑-初级。</summary>
        internal const int SkateBasicId = 50004;
        /// <summary>速度轮滑-中级。</summary>
        internal const int SkateMidId = 50005;
        /// <summary>速度轮滑-高级。</summary>
        internal const int SkateHighId = 50006;
        /// <summary>梦想号模型。</summary>
        internal const int DreamShipId = 50054;
        /// <summary>永动机。</summary>
        internal const int PerpetualId = 50090;

        internal static List<Bonus> Evaluate(IList<int> relicIds)
        {
            var list = new List<Bonus>();
            if (relicIds == null) return list;

            for (int i = 0; i < relicIds.Count; i++)
            {
                int id = relicIds[i];
                int value;
                string name;
                switch (id)
                {
                    case SkateBasicId: value = 1; name = "速度轮滑-初级"; break;
                    case SkateMidId: value = 2; name = "速度轮滑-中级"; break;
                    case SkateHighId: value = 4; name = "速度轮滑-高级"; break;
                    case DreamShipId: value = 2; name = "梦想号模型"; break;
                    case PerpetualId: value = 2; name = "永动机"; break;
                    default: continue;
                }
                list.Add(new Bonus { RelicId = id, Value = value, Name = name });
            }
            return list;
        }

        internal static int Total(IList<int> relicIds)
        {
            var list = Evaluate(relicIds);
            int total = 0;
            for (int i = 0; i < list.Count; i++) total += list[i].Value;
            return total;
        }
    }
}
