using System.Collections.Generic;

namespace ForkDirectionMod
{
    internal sealed class NeighborLabel
    {
        public LandSnapshot Junction, Land;
        public int Rank;
        public string Letter { get { return Rank == 0 ? "A" : Rank == 1 ? "B" : "C"; } }
    }

    internal static class NeighborLabels
    {
        public static List<NeighborLabel> Build(IDictionary<int, LandSnapshot> lands)
        {
            var result = new List<NeighborLabel>();
            foreach (var pair in lands)
            {
                var junction = pair.Value;
                if (junction.Disabled) continue;
                var neighbors = new List<LandSnapshot>();
                var seen = new HashSet<int>();
                foreach (int id in junction.Neighbors)
                {
                    LandSnapshot land;
                    if (id != junction.Id && seen.Add(id) && lands.TryGetValue(id, out land) && !land.Disabled)
                        neighbors.Add(land);
                }
                if (neighbors.Count != 3) continue;
                for (int rank = 0; rank < 3; rank++)
                    result.Add(new NeighborLabel { Junction = junction, Land = neighbors[rank], Rank = rank });
            }
            return result;
        }
    }
}
