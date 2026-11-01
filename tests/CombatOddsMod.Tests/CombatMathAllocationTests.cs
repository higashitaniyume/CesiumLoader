using System;
using System.Collections.Generic;
using Xunit;

namespace CombatOddsMod.Tests
{
    public class CombatMathAllocationTests
    {
        [Fact]
        public void DictionaryMode_SteadyCallsDoNotAllocate()
        {
            var distribution = new Dictionary<int, double> { [0] = 0.2, [3] = 0.6, [9] = 0.2 };
            int sum = 0;
            for (int i = 0; i < 10000; i++) sum += CombatMath.TypicalDamageOf(distribution);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) sum += CombatMath.TypicalDamageOf(distribution);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(60000, sum);
            Assert.Equal(0, allocated);
        }

        [Fact]
        public void Median_PreservesHalfWeightBoundaryAndRounding()
        {
            var distribution = new Dictionary<int, double> { [3] = 0.5, [0] = 0.5 };
            Assert.Equal(2, CombatMath.TypicalDamageOf(distribution));
        }

        [Fact]
        public void Dodge_SteadyCallsDoNotAllocate()
        {
            int sum = 0;
            for (int i = 0; i < 10000; i++) sum += CombatMath.Dodge(10, 3, 3).TypicalDamage;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) sum += CombatMath.Dodge(10, 3, 3).TypicalDamage;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(100000, sum);
            Assert.Equal(0, allocated);
        }

        [Fact]
        public void GeneralReadOnlyDistribution_PreservesMode()
        {
            IReadOnlyDictionary<int, double> distribution = new SortedDictionary<int, double>
            {
                [-4] = 0.2, [1] = 0.7, [8] = 0.1
            };
            Assert.Equal(1, CombatMath.TypicalDamageOf(distribution));
        }
    }
}
