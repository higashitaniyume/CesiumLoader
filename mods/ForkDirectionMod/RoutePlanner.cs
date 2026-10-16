using System;
using System.Collections.Generic;

namespace ForkDirectionMod
{
    // Pure managed topology: never sort neighbors; zero is a valid land id.
    internal sealed class LandSnapshot
    {
        public int Id;
        public bool Disabled;
        public float X, Y, Z;
        public int[] Neighbors = new int[0];
    }

    internal sealed class ForkRoute
    {
        public LandSnapshot Junction, Incoming, Target;
        public int Rank;
    }

    internal static class RoutePlanner
    {
        public static List<ForkRoute> Build(IDictionary<int, LandSnapshot> lands)
        {
            var routes = new List<ForkRoute>();
            foreach (var pair in lands)
            {
                var junction = pair.Value;
                if (junction.Disabled) continue;
                var neighbors = new List<LandSnapshot>();
                var seen = new HashSet<int>();
                foreach (int id in junction.Neighbors)
                {
                    LandSnapshot next;
                    if (id != junction.Id && seen.Add(id) && lands.TryGetValue(id, out next) && !next.Disabled)
                        neighbors.Add(next);
                }
                if (neighbors.Count < 3) continue;
                foreach (var incoming in neighbors)
                {
                    int rank = 0;
                    foreach (var target in neighbors)
                    {
                        if (target.Id == incoming.Id) continue;
                        routes.Add(new ForkRoute { Junction = junction, Incoming = incoming, Target = target, Rank = rank++ });
                    }
                }
            }
            return routes;
        }

        // Exactly one pair per junction. Before a character supplies an actual
        // incoming edge, use the first valid neighbor as the reference approach.
        public static List<ForkRoute> SelectPairs(List<ForkRoute> routes, IDictionary<int, int> incomingByJunction)
        {
            var chosen = new Dictionary<int, int>();
            var result = new List<ForkRoute>();
            foreach (var route in routes)
            {
                int incoming;
                if (!chosen.TryGetValue(route.Junction.Id, out incoming))
                {
                    incoming = route.Incoming.Id;
                    int requested;
                    if (incomingByJunction.TryGetValue(route.Junction.Id, out requested))
                        foreach (var candidate in routes)
                            if (candidate.Junction.Id == route.Junction.Id && candidate.Incoming.Id == requested)
                            { incoming = requested; break; }
                    chosen[route.Junction.Id] = incoming;
                }
                if (route.Incoming.Id == incoming && route.Rank < 2) result.Add(route);
            }
            return result;
        }

        public static bool Same(IDictionary<int, LandSnapshot> a, IDictionary<int, LandSnapshot> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var pair in a)
            {
                LandSnapshot next;
                if (!b.TryGetValue(pair.Key, out next)) return false;
                var old = pair.Value;
                if (old.Disabled != next.Disabled || old.X != next.X || old.Y != next.Y || old.Z != next.Z || old.Neighbors.Length != next.Neighbors.Length) return false;
                for (int i = 0; i < old.Neighbors.Length; i++)
                    if (old.Neighbors[i] != next.Neighbors[i]) return false;
            }
            return true;
        }
    }
}
