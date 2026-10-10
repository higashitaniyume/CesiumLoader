using System;
using System.IO;
using CardSkinMod;
using Xunit;

namespace CardSkinMod.Tests
{
    public sealed class CardSkinModelTests : IDisposable
    {
        private readonly string _tempDir;

        public CardSkinModelTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "CardSkinTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public void LoadSkinDirectory_WhenDirectoryDoesNotExist_ReturnsFalseAndEmpty()
        {
            var model = new CardSkinModel();
            string nonExistent = Path.Combine(_tempDir, "non_existent_skin");

            bool ok = model.LoadSkinDirectory(nonExistent, "non_existent_skin");

            Assert.False(ok);
            Assert.Equal(0, model.FileCount);
            Assert.False(model.TryGetSkinFile(10101, "加速卡", out _));
        }

        [Fact]
        public void LoadSkinDirectory_WithValidFiles_IndexesByIdAndName()
        {
            // 创建测试图片
            string card101File = Path.Combine(_tempDir, "10101.png");
            string card102File = Path.Combine(_tempDir, "10102.jpg");
            string cardNamedFile = Path.Combine(_tempDir, "换位卡.png");
            string ignoredFile = Path.Combine(_tempDir, "readme.txt");

            File.WriteAllText(card101File, "dummy-png");
            File.WriteAllText(card102File, "dummy-jpg");
            File.WriteAllText(cardNamedFile, "dummy-named");
            File.WriteAllText(ignoredFile, "text-ignore");

            var model = new CardSkinModel();
            bool ok = model.LoadSkinDirectory(_tempDir, "test_skin");

            Assert.True(ok);
            Assert.Equal(3, model.FileCount); // 忽略 .txt

            // 按 ID 查询
            Assert.True(model.TryGetSkinFile(10101, null, out string path1));
            Assert.Equal(card101File, path1);

            Assert.True(model.TryGetSkinFile(10102, null, out string path2));
            Assert.Equal(card102File, path2);

            // 按名称查询
            Assert.True(model.TryGetSkinFile(99999, "换位卡", out string pathNamed));
            Assert.Equal(cardNamedFile, pathNamed);

            // 未配置的卡牌自动回退 (返回 false)
            Assert.False(model.TryGetSkinFile(10103, "普通骰子", out string fallbackPath));
            Assert.Null(fallbackPath);
        }

        [Theory]
        [InlineData(21012, "对怪激光", "UT_HandCard_20001")]
        [InlineData(21013, "对怪板砖", "UT_HandCard_20013")]
        [InlineData(20029, "符卡-祸", "UT_HandCard_20028")]
        [InlineData(20031, "活体书页", "UT_HandCard_20030")]
        public void SharedImageCard_ResolvesActualAssetKey(int cardId, string cardName, string assetKey)
        {
            string file = Path.Combine(_tempDir, assetKey + ".png");
            File.WriteAllText(file, "test-image");
            var model = new CardSkinModel();
            model.LoadSkinDirectory(_tempDir);

            Assert.True(model.TryGetSkinForCard(cardId, cardName, assetKey, out var resolved));
            Assert.Equal(file, resolved);
        }

        [Theory]
        [InlineData("21012")]
        [InlineData("对怪激光")]
        public void CardSpecificSkin_TakesPriorityOverSharedAsset(string fileName)
        {
            string file = Path.Combine(_tempDir, fileName + ".png");
            File.WriteAllText(file, "card-specific");
            File.WriteAllText(Path.Combine(_tempDir, "UT_HandCard_20001.png"), "shared-image");
            var model = new CardSkinModel();
            model.LoadSkinDirectory(_tempDir);

            Assert.True(model.TryGetSkinForCard(21012, "对怪激光", "UT_HandCard_20001", out var resolved));
            Assert.Equal(file, resolved);
        }

        [Fact]
        public void MissingCardSkin_PreservesOfficialFallback()
        {
            File.WriteAllText(Path.Combine(_tempDir, "UT_HandCard_20001.png"), "other-card");
            var model = new CardSkinModel();
            model.LoadSkinDirectory(_tempDir);

            Assert.False(model.TryGetSkinForCard(21025, "罪证嫌疑", "UT_HandCard_21025", out var resolved));
            Assert.Null(resolved);
        }

        [Fact]
        public void LoadSkinDirectory_WithSkinJson_ParsesMetadata()
        {
            string json = "{\n  \"name\": \"超级萌化方案\",\n  \"author\": \"测试作者\",\n  \"description\": \"测试描述说明\"\n}";
            File.WriteAllText(Path.Combine(_tempDir, "skin.json"), json);
            File.WriteAllText(Path.Combine(_tempDir, "10101.png"), "png");

            var model = new CardSkinModel();
            bool ok = model.LoadSkinDirectory(_tempDir, "genshin_pack");

            Assert.True(ok);
            Assert.Equal("genshin_pack", model.SkinName);
            Assert.Equal("超级萌化方案", model.Metadata.Name);
            Assert.Equal("测试作者", model.Metadata.Author);
            Assert.Equal("测试描述说明", model.Metadata.Description);
        }

        [Fact]
        public void LoadSkinDirectory_WithPrefixedAndSfwFiles_IndexesCorrectly()
        {
            string handCardFile = Path.Combine(_tempDir, "UT_HandCard_10001.png");
            string sfwFile = Path.Combine(_tempDir, "UT_HandCard_10006_sfw.png");
            string destinyFile = Path.Combine(_tempDir, "UT_Destiny_40001.png");
            string altItemFile = Path.Combine(_tempDir, "UT_Item_AltArt_13020002.png");

            File.WriteAllText(handCardFile, "data1");
            File.WriteAllText(sfwFile, "data2");
            File.WriteAllText(destinyFile, "data3");
            File.WriteAllText(altItemFile, "data4");

            var model = new CardSkinModel();
            bool ok = model.LoadSkinDirectory(_tempDir, "materials_skin");

            Assert.True(ok);
            Assert.Equal(4, model.FileCount);

            // 1. 卡牌 ID 查询提取到的 ID
            Assert.True(model.TryGetSkinFile(10001, null, out string path1));
            Assert.Equal(handCardFile, path1);

            Assert.True(model.TryGetSkinFile(10006, null, out string path2));
            Assert.Equal(sfwFile, path2);

            Assert.True(model.TryGetSkinFile(40001, null, out string path3));
            Assert.Equal(destinyFile, path3);

            Assert.True(model.TryGetSkinFile(13020002, null, out string path4));
            Assert.Equal(altItemFile, path4);

            // 2. 按 AssetKey 查询
            Assert.True(model.TryGetSkinByAssetKey("UT_HandCard_10001", out string keyPath1));
            Assert.Equal(handCardFile, keyPath1);

            Assert.True(model.TryGetSkinByAssetKey("UT_Destiny_40001", out string keyPath2));
            Assert.Equal(destinyFile, keyPath2);

            Assert.True(model.TryGetSkinByAssetKey("UT_Item_AltArt_13020002", out string keyPath3));
            Assert.Equal(altItemFile, keyPath3);
        }
    }
}
