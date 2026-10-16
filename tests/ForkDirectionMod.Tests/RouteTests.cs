using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ForkDirectionMod.Tests
{
    public class RouteTests
    {
        private static Dictionary<int, LandSnapshot> Map(params int[] neighbors)
        {
            var map = new Dictionary<int, LandSnapshot>();
            map[11] = new LandSnapshot { Id=11, Neighbors=neighbors };
            foreach(int id in neighbors) if(id!=11) map[id]=new LandSnapshot { Id=id };
            return map;
        }
        [Theory]
        [InlineData(10,52,12)]
        [InlineData(52,10,12)]
        [InlineData(12,10,52)]
        public void ScreenshotJunctionPreservesOrderForEachApproach(int incoming,int red,int blue)
        {
            var routes=RoutePlanner.Build(Map(10,52,12)).Where(r=>r.Incoming.Id==incoming).ToArray();
            Assert.Equal(new[]{red,blue},routes.Select(r=>r.Target.Id));
            Assert.Equal(new[]{0,1},routes.Select(r=>r.Rank));
        }
        [Fact] public void EachJunctionHasExactlyTwoArrows()
        {
            var all=RoutePlanner.Build(Map(10,52,12));
            var reference=RoutePlanner.SelectPairs(all,new Dictionary<int,int>());
            Assert.Equal(2,reference.Count);
            Assert.Equal(new[]{52,12},reference.Select(r=>r.Target.Id));
            var actual=RoutePlanner.SelectPairs(all,new Dictionary<int,int>{{11,52}});
            Assert.Equal(2,actual.Count);
            Assert.Equal(new[]{10,12},actual.Select(r=>r.Target.Id));
        }
        [Fact] public void DisabledNeighborRemovesFork()
        { var map=Map(10,52,12);map[52].Disabled=true;Assert.Empty(RoutePlanner.Build(map)); }
        [Fact] public void MissingNeighborRemovesFork()
        { var map=Map(10,52,12);map.Remove(52);Assert.Empty(RoutePlanner.Build(map)); }
        [Fact] public void DisabledJunctionHasNoRoutes()
        { var map=Map(10,52,12);map[11].Disabled=true;Assert.Empty(RoutePlanner.Build(map)); }
        [Fact] public void ZeroIsAValidId()
        { var map=Map(0,52,12);var routes=RoutePlanner.Build(map);Assert.Contains(routes,r=>r.Incoming.Id==52&&r.Target.Id==0&&r.Rank==0); }
        [Fact] public void DuplicateOrSelfLinksDoNotInventForks()
        { Assert.Empty(RoutePlanner.Build(Map(10,10,12,11))); }
        [Fact] public void FourWayForkPreservesAllRanks()
        { var routes=RoutePlanner.Build(Map(52,10,12,0)).Where(r=>r.Incoming.Id==10).ToArray(); Assert.Equal(new[]{52,12,0},routes.Select(r=>r.Target.Id));Assert.Equal(new[]{0,1,2},routes.Select(r=>r.Rank)); }
        [Fact] public void RefreshDetectsOrderDisableAndMovement()
        {
            var a=Map(10,52,12); var b=Map(10,52,12);Assert.True(RoutePlanner.Same(a,b));
            b[11].Neighbors=new[]{10,12,52};Assert.False(RoutePlanner.Same(a,b));
            b=Map(10,52,12);b[52].Disabled=true;Assert.False(RoutePlanner.Same(a,b));
            b=Map(10,52,12);b[11].X=1;Assert.False(RoutePlanner.Same(a,b));
        }
        [Theory]
        [InlineData(100,0)]
        [InlineData(0,100)]
        [InlineData(-100,-20)]
        [InlineData(0,-100)]
        public void ArrowEndsAtActualTargetAndHasFiniteGeometry(float x,float y)
        {
            var end=new Point2(x,y);var points=ArrowGeometry.Straight(new Point2(0,0),end,4);
            Assert.Equal(7,points.Length);
            Assert.True(ArrowGeometry.SignedArea(points)>0f, "FairyGUI requires positive winding for its ear clipping.");
            Assert.NotEmpty(points);Assert.Contains(points,p=>p.X==x&&p.Y==y);
            Assert.All(points,p=>{Assert.True(float.IsFinite(p.X));Assert.True(float.IsFinite(p.Y));});
        }
        [Fact] public void DegenerateArrowIsHidden()
        { Assert.Empty(ArrowGeometry.Straight(new Point2(1,1),new Point2(1,1),4)); }
    }
}
