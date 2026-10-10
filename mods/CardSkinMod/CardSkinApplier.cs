using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using FairyGUI;
using GameLogic;
using Tools;
using UI;

namespace CardSkinMod
{
    /// <summary>维护所有场景的图片缓存，并刷新手牌、图鉴、预览和事件窗口中的卡面。</summary>
    public sealed class CardSkinApplier
    {
        private readonly CardSkinModel _model;
        private readonly CardTextureLoader _loader;
        private CardSkinCatalog _catalog;
        private bool _catalogReady;
        private bool _forceFullCard;
        private FieldInfo _textureCacheField;
        private static readonly MethodInfo ExternalLoadSuccess = typeof(GLoader).GetMethod(
            "onExternalLoadSuccess", BindingFlags.NonPublic | BindingFlags.Instance);
        private readonly Dictionary<string, NTexture> _injected = new Dictionary<string, NTexture>();
        private long _nextWarning;

        public CardSkinApplier(CardSkinModel model, CardTextureLoader loader, bool forceFullCard)
        {
            _model = model;
            _loader = loader;
            _forceFullCard = forceFullCard;
            _catalog = new CardSkinCatalog(model);
        }

        public bool ForceFullCard { get => _forceFullCard; set => _forceFullCard = value; }

        public void InjectTextureManager()
        {
            try
            {
                // 先等卡牌配置就绪，避免尚未识别异画时把原始文件误注入。
                if (StaticConfigure.Card == null) return;
                // 配置可能晚于模组初始化就绪；每轮重建绑定，不依赖是否正在对局。
                var catalog = new CardSkinCatalog(_model);
                if (StaticConfigure.Card != null)
                {
                    foreach (var config in StaticConfigure.Card.Infos)
                    {
                        string name = GetName(config.NameID, UIStringType.Card);
                        catalog.Register(config.Id, name, config.GetImage(), config.ImageSfw);
                        if (!StaticConfigure.Card.AltArtDict.TryGetValue(config.Id, out var variants)) continue;
                        foreach (var art in variants.CardAltArtConfigureItems)
                        {
                            if (art.AltArtId == 0 || art.AltArtId == config.Id) continue;
                            // 用户要求保留所有异画：图片、视频和叠加卡面都不注入。
                            catalog.ProtectAlternate(art.AccountBackground, art.AccountBackgroundSfw,
                                art.AccountBackgroundVideo, art.AccountBackgroundVideoSfw, art.CardFront);
                        }
                    }
                }
                if (StaticConfigure.Event != null)
                {
                    foreach (var config in StaticConfigure.Event.Infos)
                        catalog.Register(config.Id, GetName(config.NameID, UIStringType.Event), config.GetImage(), config.ImageSfw);
                }
                if (StaticConfigure.Destiny != null)
                {
                    foreach (var config in StaticConfigure.Destiny.Infos)
                        catalog.Register(config.Id, GetName(config.NameID, UIStringType.Destiny), config.Image);
                }
                if (StaticConfigure.MapEvent != null)
                {
                    foreach (var config in StaticConfigure.MapEvent.MapEventCards)
                        catalog.Register(config.Id, GetName(config.NameID, UIStringType.MapEvent), config.GetImage(), config.ImageSfw);
                }
                _catalog = catalog;
                _catalogReady = true;

                var tm = SimpleSingletonProvider<TextureManager>.inst;
                if (tm == null) return;
                if (_textureCacheField == null)
                    _textureCacheField = typeof(TextureManager).GetField("_cachedTextureDict", BindingFlags.NonPublic | BindingFlags.Instance);
                var cache = _textureCacheField?.GetValue(tm) as Dictionary<string, NTexture>;
                if (cache == null) return;

                int changed = 0;
                foreach (var entry in catalog.AssetFiles)
                {
                    // 只注入图片素材；数字、中文名和视频 key 用配置映射/可见控件处理。
                    if (!entry.Key.StartsWith("UT_", StringComparison.OrdinalIgnoreCase) || catalog.IsProtectedAsset(entry.Key)) continue;
                    var texture = _loader.GetOrCreateTexture(entry.Value, entry.Key);
                    if (texture == null) continue;
                    if (!cache.TryGetValue(entry.Key, out var old) || old != texture)
                    {
                        cache[entry.Key] = texture;
                        changed++;
                    }
                    _injected[entry.Key] = texture;
                }
                if (changed > 0)
                    SdkLog.Info("CardSkin", $"已更新 {changed} 个卡面缓存：手牌、事件、命运、地图事件与图鉴（场景切换后自动恢复）。");
            }
            catch (Exception ex) { Warn("维护卡面缓存失败: " + ex.Message); }
        }

        private static string GetName(int id, UIStringType type)
        {
            try { return id == 0 ? null : id.GetLocal(type); }
            catch { return null; }
        }

        public void ApplyToHandCardPanel()
        {
            try
            {
                var ui = GameLogicManager.inst?.battle?.handCard?.UIObject;
                if (ui?.container_Card != null) VisitCards(ui.container_Card, 0);
            }
            catch (Exception ex) { Warn("刷新手牌失败: " + ex.Message); }
        }

        // 保留原入口，统一扫描包括战斗窗口在内的可见界面。
        public void ApplyToFightWindow() => ApplyToVisibleCards();

        public void ApplyToVisibleCards()
        {
            if (!_catalogReady) return;
            try
            {
                var root = GRoot.inst;
                if (root != null) VisitCards(root, 0);
            }
            catch (Exception ex) { Warn("刷新可见卡面失败: " + ex.Message); }
        }

        private void VisitCards(GObject node, int depth)
        {
            if (node == null || node.isDisposed || !node.visible || depth > 20) return;
            if (node is UIHandCard_Button_Card handCard)
            {
                ApplyToCardButton(handCard);
                return;
            }
            if (node is NewGameLibraryWindow gallery) ApplyToGallery(gallery);
            if (node is UICom_Card card)
            {
                ApplyToVisibleCard(card);
                return;
            }
            if (node is GComponent component)
                for (int i = 0; i < component.numChildren; i++) VisitCards(component.GetChildAt(i), depth + 1);
        }

        private void ApplyToGallery(NewGameLibraryWindow window)
        {
            if (!(window.contentPane is UINewGameLibraryWindow ui)) return;
            var list = ui.com_Cords?.list_Cards;
            if (list == null) return;
            for (int i = 0; i < list.numChildren; i++)
            {
                var item = list.GetChildAt(i) as UINewGameLibrary_Button_Card;
                if (item == null || !(item.data is int index) || !(item.com_card is UICom_Card card)) continue;
                string file = null;
                if (ui.label.selectedIndex == 1 && index >= 0 && index < window.CardInfos.Count)
                {
                    var config = window.CardInfos[index];
                    var view = config.GetMainPlayerCardView();
                    if (view.IsAltArtCard || view.IsVideo) continue;
                    if (!_catalog.TryGetFile(view.Key, out file))
                        _model.TryGetSkinForCard(config.Id, GetName(config.NameID, UIStringType.Card), config.GetImage(), out file);
                }
                else if (ui.label.selectedIndex == 5 && index >= 0 && index < window.EventInfos.Count)
                {
                    var config = window.EventInfos[index];
                    _model.TryGetSkinForCard(config.Id, GetName(config.NameID, UIStringType.Event), config.GetImage(), out file);
                }
                ApplyFile(card, file);
            }
        }

        private void ApplyToVisibleCard(UICom_Card card)
        {
            if (IsProtectedCard(card)) return;
            string file;
            string frontUrl = card.loader_FrontCard?.url;
            string fullUrl = card.loader_FullCard?.url;
            if (!string.IsNullOrEmpty(frontUrl) || !string.IsNullOrEmpty(fullUrl))
            {
                if (_catalog.TryGetFile(frontUrl, out file) || _catalog.TryGetFile(fullUrl, out file)) ApplyFile(card, file);
                return;
            }
            // 直接设置 texture 会清空 url；维持已应用卡面，并压住异步视频的迟到回调。
            var texture = card.loader_FrontCard?.texture;
            if (!_loader.OwnsTexture(texture)) texture = card.loader_FullCard?.texture;
            if (_loader.OwnsTexture(texture)) ApplyToComCard(card, texture);
        }

        public void ApplyToCardButton(UIHandCard_Button_Card cardBtn)
        {
            if (!_catalogReady || cardBtn?.CardData == null || !(cardBtn.com_Card is UICom_Card card)) return;
            if (IsProtectedCard(card)) return;
            var self = GameLogicManager.inst?.battle?.GetSelfPlayerData();
            if (self?.player != null && cardBtn._config.GetBattlePlayerCardView(self.player.Id).IsAltArtCard) return;
            string key = card.loader_FrontCard?.url;
            if (!_catalog.TryGetFile(key, out var file))
                _model.TryGetSkinForCard(cardBtn.CardData.CardId, GetName(cardBtn._config?.NameID ?? 0, UIStringType.Card), cardBtn._config?.GetImage(), out file);
            ApplyFile(card, file);
        }

        private void ApplyFile(UICom_Card card, string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            var texture = _loader.GetOrCreateTexture(file, card.loader_FrontCard?.url ?? card.loader_FullCard?.url);
            if (texture == null) return;
            ApplyCardLayout(card, texture);
            ApplyLoaderTexture(card.loader_FrontCard, _loader.GetOrCreateTexture(file, card.loader_FrontCard?.url));
            ApplyLoaderTexture(card.loader_FullCard, _loader.GetOrCreateTexture(file, card.loader_FullCard?.url));
        }

        public void ApplyToComCard(UICom_Card card, NTexture texture)
        {
            if (card == null || card.isDisposed || texture == null || texture.disposed || IsProtectedCard(card)) return;
            ApplyCardLayout(card, texture);
            ApplyLoaderTexture(card.loader_FrontCard, texture);
            ApplyLoaderTexture(card.loader_FullCard, texture);
        }

        private void ApplyCardLayout(UICom_Card card, NTexture texture)
        {
            if ((_forceFullCard || texture.height > texture.width * 1.15f) && card.frontState != null && card.frontState.selectedIndex != 2)
                card.frontState.selectedIndex = 2;
        }

        private static void ApplyLoaderTexture(GLoader loader, NTexture texture)
        {
            if (loader == null || texture == null || loader.texture == texture) return;
            if (string.IsNullOrEmpty(loader.url))
            {
                // 无资源地址的控件使用直接贴图，同时与 TextureLoader.FreeExternal 的 ReleaseRef 配对。
                loader.ClearContent();
                texture.AddRef();
                loader.texture = texture;
                return;
            }
            if (ExternalLoadSuccess == null || texture.nativeTexture == null
                || !string.Equals(texture.nativeTexture.name, loader.url, StringComparison.Ordinal)) return;
            // 使用游戏正常的加载成功入口，保留 url、源图尺寸与重载监听。
            // 不能使用 GLoader.texture setter：它会清空 url，导致下一次重排先清旧卡面。
            loader.ClearContent();
            texture.AddRef();
            ExternalLoadSuccess.Invoke(loader, new object[] { texture });
        }

        private bool IsProtectedCard(UICom_Card card)
        {
            if (card.isVideo != null && card.isVideo.selectedIndex != 0) return true;
            if (_catalog.IsProtectedAsset(card.loader_FrontCard?.url)
                || _catalog.IsProtectedAsset(card.loader_FullCard?.url)) return true;
            // 游戏原生异画使用 1/2 布局；模组自己的长图也使用 2，因此检查贴图归属。
            return card.frontState != null && card.frontState.selectedIndex != 0
                && !_loader.OwnsTexture(card.loader_FrontCard?.texture)
                && !_loader.OwnsTexture(card.loader_FullCard?.texture);
        }

        public void RemoveInjectedTextures()
        {
            try
            {
                var tm = SimpleSingletonProvider<TextureManager>.inst;
                var cache = _textureCacheField?.GetValue(tm) as Dictionary<string, NTexture>;
                if (cache != null)
                    foreach (var entry in _injected)
                        if (cache.TryGetValue(entry.Key, out var current) && current == entry.Value) cache.Remove(entry.Key);
                _injected.Clear();
            }
            catch (Exception ex) { Warn("释放卡面缓存失败: " + ex.Message); }
        }

        private void Warn(string message)
        {
            long now = DateTime.UtcNow.Ticks / 10000;
            if (now < _nextWarning) return;
            _nextWarning = now + 10000;
            SdkLog.Warn("CardSkin", message);
        }
    }
}
