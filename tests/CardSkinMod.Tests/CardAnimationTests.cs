using System;
using System.IO;
using CardSkinMod;
using Xunit;

namespace CardSkinMod.Tests
{
    public sealed class CardAnimationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cardanim-tests-" + Guid.NewGuid().ToString("N"));
        public CardAnimationTests() { Directory.CreateDirectory(_root); }
        public void Dispose() { Directory.Delete(_root, true); }
        private string Create(string binding = "20001", int fps = 15, int count = 3, int width = 2, int height = 3)
        {
            string path = Path.Combine(_root, binding + ".cardanim");
            File.WriteAllText(path, $"version=1\nfps={fps}\nframeCount={count}\nwidth={width}\nheight={height}\n");
            string dir = Path.ChangeExtension(path, "frames");
            Directory.CreateDirectory(dir);
            for (int i = 0; i < count; i++)
            {
                byte[] png = new byte[33];
                new byte[] {137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82}.CopyTo(png,0);
                for (int b = 0; b < 4; b++) { png[19-b] = (byte)(width >> (b*8)); png[23-b] = (byte)(height >> (b*8)); }
                File.WriteAllBytes(Path.Combine(dir, i.ToString("D6") + ".png"), png);
            }
            return path;
        }
        [Theory]
        [InlineData(0, 0)] [InlineData(66, 0)] [InlineData(67, 1)] [InlineData(133, 1)]
        [InlineData(134, 2)] [InlineData(200, 0)] [InlineData(-1, 0)] [InlineData(1000000, 0)]
        public void SharedClockSelectsFramesAndWraps(long milliseconds, int expected)
        {
            Assert.True(CardAnimation.TryLoad(Create(), out var animation));
            Assert.Equal(expected, animation.FrameAt(milliseconds));
            Assert.InRange(animation.FrameAt(long.MaxValue), 0, 2);
        }
        [Theory]
        [InlineData(1,1,1,1,true)] [InlineData(30,300,1,560,true)] [InlineData(15,300,400,500,true)]
        [InlineData(15,300,401,500,false)] [InlineData(0,1,1,1,false)] [InlineData(31,1,1,1,false)]
        [InlineData(15,301,1,1,false)] [InlineData(15,1,561,1,false)] [InlineData(15,1,1,561,false)]
        [InlineData(15,1,0,1,false)] [InlineData(15,0,1,1,false)]
        public void EnforcesLimits(int fps, int count, int width, int height, bool valid)
        { Assert.Equal(valid, CardAnimation.TryLoad(Create(fps:fps,count:count,width:width,height:height), out _)); }
        [Theory]
        [InlineData("version=2\nfps=15\nframeCount=3\nwidth=2\nheight=3")]
        [InlineData("version=1\nfps=15\nframeCount=3\nwidth=2")]
        [InlineData("version=1\nfps=15\nframeCount=3\nwidth=2\nheight=3\nfps=15")]
        [InlineData("version=1\nfps=15\nframeCount=3\nwidth=2\nheight=3\npath=../outside")]
        [InlineData("version=1\nfps=+15\nframeCount=3\nwidth=2\nheight=3")]
        [InlineData("version=1\nfps=2147483648\nframeCount=3\nwidth=2\nheight=3")]
        public void RejectsMalformedManifest(string text)
        { string path = Create(); File.WriteAllText(path,text); Assert.False(CardAnimation.TryLoad(path,out _)); }
        [Fact]
        public void RequiresEverySixDigitPngWithDeclaredDimensions()
        {
            string path=Create(); string frame=Path.Combine(Path.ChangeExtension(path,"frames"),"000001.png");
            File.Move(frame, Path.Combine(Path.GetDirectoryName(frame),"1.png"));
            Assert.False(CardAnimation.TryLoad(path,out _));
            Create(); byte[] bytes=File.ReadAllBytes(frame); bytes[23]=4; File.WriteAllBytes(frame,bytes);
            Assert.False(CardAnimation.TryLoad(path,out _));
            Create(); File.WriteAllText(frame,"broken"); Assert.False(CardAnimation.TryLoad(path,out _));
        }
        [Fact]
        public void ModelKeepsFrameDirectoryOutOfStaticIndexAndFallsBack()
        {
            Create("加速卡"); Create("UT_Item_AltArt_123"); Create();
            File.WriteAllText(Path.Combine(_root,"20001.png"),"static");
            var model=new CardSkinModel(); Assert.True(model.LoadSkinDirectory(_root));
            Assert.Equal(3,model.AnimationCount); Assert.Equal(1,model.FileCount);
            Assert.True(model.TryGetAnimationForCard(20001,null,"UT_HandCard_20001",out _));
            Assert.True(model.TryGetAnimationForCard(20002,"加速卡",null,out _));
            Assert.False(model.TryGetExactAnimation("000000",out _));
            Assert.True(model.TryGetSkinFile(20001,null,out _));
            Assert.False(model.TryGetAnimationForCard(99999,null,null,out _));
        }
        [Fact]
        public void OrdinaryAnimationDoesNotBindAlternateAndExplicitAlternateDoes()
        {
            Create(); var model=new CardSkinModel(); model.LoadSkinDirectory(_root);
            var catalog=new CardSkinCatalog(model); catalog.Register(20001,"普通","base");
            catalog.ProtectAlternate("alt","altvideo"); catalog.RegisterAlternate(70001,"alt","altvideo");
            Assert.True(catalog.TryGetAnimation("base",out _)); Assert.False(catalog.TryGetAnimation("alt",out _));
            Create("UT_Item_AltArt_70001"); model.LoadSkinDirectory(_root);
            catalog=new CardSkinCatalog(model); catalog.ProtectAlternate("alt","altvideo"); catalog.RegisterAlternate(70001,"alt","altvideo");
            Assert.True(catalog.TryGetAnimation("altvideo",out _)); Assert.True(catalog.IsAlternateAnimation("altvideo"));
            Assert.True(catalog.IsProtectedAsset("altvideo")); Assert.False(catalog.TryGetFile("altvideo",out _));
        }
        [Fact]
        public void SfwOnlyAnimationBindsWithoutAnOrdinaryAnimation()
        {
            Create("UT_HandCard_20001_sfw");
            var model = new CardSkinModel(); model.LoadSkinDirectory(_root);
            var catalog = new CardSkinCatalog(model);
            catalog.Register(20001, "普通", "UT_HandCard_20001", "UT_HandCard_20001_sfw");
            Assert.False(catalog.TryGetAnimation("UT_HandCard_20001", out _));
            Assert.True(catalog.TryGetAnimation("UT_HandCard_20001_sfw", out var sfw));
            Create("20001"); model.LoadSkinDirectory(_root);
            catalog = new CardSkinCatalog(model);
            catalog.Register(20001, "普通", "UT_HandCard_20001", "UT_HandCard_20001_sfw");
            Assert.True(catalog.TryGetAnimation("UT_HandCard_20001", out var normal));
            Assert.True(catalog.TryGetAnimation("UT_HandCard_20001_sfw", out sfw));
            Assert.NotEqual(normal.ManifestPath, sfw.ManifestPath);
        }
        [Fact]
        public void FrameDirectoryJunctionCannotEscapeSkinRoot()
        {
            string path=Create(); string dir=Path.ChangeExtension(path,"frames");
            Directory.Delete(dir,true);
            string outside=Path.Combine(_root,"outside"); Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(dir,outside);
            Assert.False(CardAnimation.TryLoad(path,out _));
            Directory.Delete(dir);
        }
    }
}
