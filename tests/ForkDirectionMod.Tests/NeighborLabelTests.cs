using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ForkDirectionMod.Tests
{
    public class NeighborLabelTests
    {
        private static Dictionary<int,LandSnapshot> Map(params int[] neighbors)
        {
            var map=new Dictionary<int,LandSnapshot>();
            map[11]=new LandSnapshot { Id=11,Neighbors=neighbors };
            foreach(int id in neighbors) if(id!=11) map[id]=new LandSnapshot { Id=id };
            return map;
        }
        [Fact] public void LettersFollowOriginalNeighborOrderIncludingIncomingRoad()
        {
            var labels=NeighborLabels.Build(Map(10,52,12));
            Assert.Equal(new[]{10,52,12},labels.Select(x=>x.Land.Id));
            Assert.Equal(new[]{"A","B","C"},labels.Select(x=>x.Letter));
            Assert.Equal(new[]{0,1,2},labels.Select(x=>x.Rank));
            Assert.All(labels,x=>Assert.Equal(11,x.Junction.Id));
        }
        [Fact] public void ZeroIdIsLabeled()
        { Assert.Equal(0,NeighborLabels.Build(Map(0,52,12))[0].Land.Id); }
        [Fact] public void DisabledNeighborNoLongerFormsThreeWayFork()
        { var map=Map(10,52,12);map[52].Disabled=true;Assert.Empty(NeighborLabels.Build(map)); }
        [Fact] public void MissingNeighborDoesNotProducePhantomLabel()
        { var map=Map(10,52,12);map.Remove(52);Assert.Empty(NeighborLabels.Build(map)); }
        [Fact] public void DuplicatesAreNotExtraNeighbors()
        { Assert.Empty(NeighborLabels.Build(Map(10,10,12))); }
        [Fact] public void FourWayForkDoesNotMislabelFourthNeighborAsC()
        { Assert.Empty(NeighborLabels.Build(Map(10,52,12,0))); }
    }
}
