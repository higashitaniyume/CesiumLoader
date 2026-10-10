using System;
using System.IO;
using CardSkinMod;
using Xunit;

namespace CardSkinMod.Tests
{
    public sealed class CardSkinCatalogTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CardSkinCatalogTests_" + Guid.NewGuid().ToString("N"));

        public CardSkinCatalogTests() { Directory.CreateDirectory(_directory); }
        public void Dispose() { Directory.Delete(_directory, true); }
        private string Add(string name)
        {
            string path = Path.Combine(_directory, name + ".png");
            File.WriteAllText(path, "fixture");
            return path;
        }
        private CardSkinModel Model()
        {
            var model = new CardSkinModel();
            model.LoadSkinDirectory(_directory);
            return model;
        }

        [Theory]
        [InlineData(30001, "事件测试", "UT_Event_30001")]
        [InlineData(40001, "命运测试", "UT_Destiny_40001")]
        [InlineData(31001, "地图事件测试", "UT_MapEvent_31001")]
        public void EventFamilies_AcceptAssetKeyNumericIdAndChineseName(int id, string name, string key)
        {
            // 分别模拟三种方案，验证它们都映射到游戏实际加载的素材 key。
            foreach (string fileName in new[] { key, id.ToString(), name })
            {
                string path = Add(fileName);
                var model = Model();
                var catalog = new CardSkinCatalog(model);
                catalog.Register(id, name, key);
                Assert.True(catalog.TryGetFile(key, out var resolved));
                Assert.Equal(path, resolved);
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("UT_Event_30001", 30001)]
        [InlineData("UT_MapEvent_3100401", 3100401)]
        [InlineData("UT_Event_30006_sfw", 30006)]
        public void NewPrefixes_ExtractActualNumericId(string key, int id)
        {
            Assert.Equal(id, CardSkinModel.ExtractCardId(key, out _));
        }

        [Fact]
        public void EventWithSharedImage_UsesConfiguredImageRatherThanInventedIdKey()
        {
            string path = Add("UT_Event_12702");
            var catalog = new CardSkinCatalog(Model());
            catalog.Register(30050, "共享事件", "UT_Event_12702");
            Assert.True(catalog.TryGetFile("UT_Event_12702", out var resolved));
            Assert.Equal(path, resolved);
        }

        [Fact]
        public void ExplicitSfwImage_WinsInAngelModeAndRemainsDistinct()
        {
            string normal = Add("UT_Event_30006");
            string sfw = Add("UT_Event_30006_sfw");
            var model = Model();
            var catalog = new CardSkinCatalog(model);
            catalog.Register(30006, "事件", "UT_Event_30006", "UT_Event_30006_sfw");
            Assert.True(catalog.TryGetFile("UT_Event_30006", out var normalResult));
            Assert.Equal(normal, normalResult);
            Assert.True(catalog.TryGetFile("UT_Event_30006_sfw", out var sfwResult));
            Assert.Equal(sfw, sfwResult);
            Assert.True(model.TryGetSkinForCard(30006, "事件", "UT_Event_30006_sfw", out var selected));
            Assert.Equal(sfw, selected);
        }

        [Fact]
        public void BaseSkin_DoesNotReplaceStaticOrVideoAlternateKeys()
        {
            string path = Add("10001");
            var catalog = new CardSkinCatalog(Model());
            catalog.Register(10001, "卡牌", "UT_HandCard_10001");
            catalog.ProtectAlternate("UT_AltCard_10001", "UT_AltCard_10001_sfw", "MV_Card_10001", "MV_Card_10001_sfw");
            Assert.True(catalog.TryGetFile("UT_HandCard_10001", out var normal));
            Assert.Equal(path, normal);
            foreach (var key in new[] { "UT_AltCard_10001", "UT_AltCard_10001_sfw", "MV_Card_10001", "MV_Card_10001_sfw" })
            {
                Assert.True(catalog.IsProtectedAsset(key));
                Assert.False(catalog.TryGetFile(key, out _));
            }
        }

        [Fact]
        public void ExplicitAlternateFiles_AreExcludedFromCacheAndVisibleBindings()
        {
            Add("UT_AltCard_10001");
            Add("UT_AltCard_10001_sfw");
            Add("MV_Card_10001");
            var catalog = new CardSkinCatalog(Model());
            catalog.ProtectAlternate("UT_AltCard_10001", "UT_AltCard_10001_sfw", "MV_Card_10001");
            Assert.False(catalog.AssetFiles.ContainsKey("UT_AltCard_10001"));
            Assert.False(catalog.TryGetFile("UT_AltCard_10001_sfw", out _));
            Assert.False(catalog.TryGetFile("MV_Card_10001", out _));
        }

        [Fact]
        public void AlternateProtection_SurvivesLaterConfigurationRegistration()
        {
            Add("10001");
            var catalog = new CardSkinCatalog(Model());
            catalog.ProtectAlternate("UT_SharedArt_1");
            catalog.Register(10001, "卡牌", "UT_SharedArt_1");
            Assert.False(catalog.TryGetFile("UT_SharedArt_1", out _));
        }

        [Fact]
        public void AlternateIcons_AreNeverInjectedEvenBeforeConfigurationLoads()
        {
            Add("UT_Item_AltArt_13010001");
            Add("UT_IAltArt_13010001");
            var catalog = new CardSkinCatalog(Model());
            Assert.False(catalog.TryGetFile("UT_Item_AltArt_13010001", out _));
            Assert.False(catalog.TryGetFile("UT_IAltArt_13010001", out _));
        }

        [Fact]
        public void MissingSkin_DoesNotBindImages()
        {
            Add("unrelated");
            var catalog = new CardSkinCatalog(Model());
            catalog.Register(30001, "事件", "UT_Event_30001");
            Assert.False(catalog.TryGetFile("UT_Event_30001", out _));
        }

        [Fact]
        public void DuplicateNamesWithDifferentSkins_AreNotUsedToGuessVideoIdentity()
        {
            Add("20028");
            Add("20029");
            var catalog = new CardSkinCatalog(Model());
            catalog.Register(20028, "同名卡", "UT_HandCard_20028");
            catalog.Register(20029, "同名卡", "UT_HandCard_20028");
            Assert.False(catalog.TryGetFileByName("同名卡", out _));
        }

        [Fact]
        public void LateConfigurationRegistration_CanAddPreviouslyUnknownEventBinding()
        {
            string path = Add("事件名字");
            var model = Model();
            var first = new CardSkinCatalog(model);
            Assert.False(first.TryGetFile("UT_Event_30001", out _));
            var ready = new CardSkinCatalog(model);
            ready.Register(30001, "事件名字", "UT_Event_30001");
            Assert.True(ready.TryGetFile("UT_Event_30001", out var resolved));
            Assert.Equal(path, resolved);
        }
    }
}
