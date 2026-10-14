using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using Cysharp.Threading.Tasks;
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
        private long _nextDiscovery;
        private int _lastUpdateFrame = -1;
        private bool _updatingAnimations;
        private Dictionary<string, NTexture> _currentTextureCache;
        private object _cardConfig, _eventConfig, _destinyConfig, _mapEventConfig;
        private readonly List<KeyValuePair<UICom_Card, AnimatedCard>> _animationSnapshot = new List<KeyValuePair<UICom_Card, AnimatedCard>>();
        private readonly List<UICom_Card> _unusedAnimations = new List<UICom_Card>();
        private readonly List<string> _unusedAnimationKeys = new List<string>();
        private readonly CardAnimationPlayer _animations;
        private sealed class AnimatedCard
        {
            public CardAnimation Animation;
            public string Key;
            public int Video;
            public int Layout;
            public string FrontUrl, FullUrl, FrontAssetKey, FullAssetKey, FrontSequenceKey, FullSequenceKey;
            public NTexture FrontTexture, FullTexture;
            public int FrameIndex = -1;
            public bool Applied;
        }
        private readonly Dictionary<UICom_Card, AnimatedCard> _animatedCards = new Dictionary<UICom_Card, AnimatedCard>();
        private readonly HashSet<UICom_Card> _visitedAnimations = new HashSet<UICom_Card>();
        private readonly HashSet<string> _animationInjected = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _activeAnimationKeys = new HashSet<string>(StringComparer.Ordinal);

        public CardSkinApplier(CardSkinModel model, CardTextureLoader loader, bool forceFullCard, int animationCacheMiB = 512)
        {
            _animations = new CardAnimationPlayer(animationCacheMiB);
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
                // 配表可能晚到或替换；只在对象变更时重建，避免每秒重复整表映射。
                var catalog = _catalog;
                if (!_catalogReady || !ReferenceEquals(_cardConfig, StaticConfigure.Card)
                    || !ReferenceEquals(_eventConfig, StaticConfigure.Event)
                    || !ReferenceEquals(_destinyConfig, StaticConfigure.Destiny)
                    || !ReferenceEquals(_mapEventConfig, StaticConfigure.MapEvent))
                {
                catalog = new CardSkinCatalog(_model);
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
                            catalog.RegisterAlternate(art.AltArtId, art.AccountBackground, art.AccountBackgroundSfw,
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
                _cardConfig = StaticConfigure.Card;
                _eventConfig = StaticConfigure.Event;
                _destinyConfig = StaticConfigure.Destiny;
                _mapEventConfig = StaticConfigure.MapEvent;
                _nextDiscovery = 0;
                }

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
                    // The animated frame already owns this resource key. Do not replace it with
                    // its static poster every second; the animation update maintains it instead.
                    if (catalog.TryGetAnimation(entry.Key, out _) && cache.TryGetValue(entry.Key, out var animated)
                        && _animations.OwnsTexture(animated) && animated.nativeTexture != null) continue;
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

        // Hand events invalidate discovery; never re-enter card enumeration from load callbacks.
        public void ApplyToHandCardPanel() { _nextDiscovery = 0; }

        // 保留原入口，统一扫描包括战斗窗口在内的可见界面。
        public void ApplyToFightWindow() => ApplyToVisibleCards();

        public void ApplyToVisibleCards()
        {
            if (!_catalogReady || _updatingAnimations) return;
            int frame = UnityEngine.Time.frameCount;
            if (_lastUpdateFrame == frame) return;
            _lastUpdateFrame = frame;
            long now = DateTime.UtcNow.Ticks / 10000;
            _animations.BeginUpdate(now);
            _updatingAnimations = true;
            try
            {
                _activeAnimationKeys.Clear();
                _currentTextureCache = _textureCacheField?.GetValue(SimpleSingletonProvider<TextureManager>.inst) as Dictionary<string, NTexture>;
                bool discover = now >= _nextDiscovery;
                if (discover)
                {
                    _nextDiscovery = now + 100;
                    _visitedAnimations.Clear();
                    var root = GRoot.inst;
                    if (root != null) VisitCards(root, 0);
                    _unusedAnimations.Clear();
                    foreach (var pair in _animatedCards)
                        if (!_visitedAnimations.Contains(pair.Key)) _unusedAnimations.Add(pair.Key);
                    foreach (var card in _unusedAnimations) { ResetAnimatedLoaders(card); _animatedCards.Remove(card); }
                }
                // Snapshot because applying a GLoader texture can invoke game completion callbacks.
                _animationSnapshot.Clear();
                foreach (var pair in _animatedCards) _animationSnapshot.Add(pair);
                bool removed = false;
                foreach (var pair in _animationSnapshot)
                {
                    if (!_animatedCards.TryGetValue(pair.Key, out var current) || current != pair.Value) continue;
                    if (!IsVisibleCard(pair.Key) || !SameAnimationBinding(pair.Key, current) || _animations.IsFailed(current.Animation))
                    {
                        ResetAnimatedLoaders(pair.Key);
                        _animatedCards.Remove(pair.Key);
                        _nextDiscovery = 0;
                        removed = true;
                        continue;
                    }
                    ApplyAnimatedFrame(pair.Key, current);
                }
                if (discover || removed) RemoveUnusedAnimationCache();
                _animations.Sweep();
                _animations.Pump();
            }
            catch (Exception ex) { Warn("刷新可见卡面失败: " + ex.Message); }
            finally { _animationSnapshot.Clear(); _unusedAnimations.Clear(); _updatingAnimations = false; }
        }

        private static bool IsVisibleCard(UICom_Card card)
        {
            if (card == null || card.isDisposed || !card.onStage) return false;
            GObject node = card;
            for (int depth = 0; node != null && depth <= 20; depth++)
            {
                if (node.isDisposed || !node.visible || (node.group != null && !node.group.visible)) return false;
                var parent = node.parent;
                if (parent != null && !parent.IsChildInView(node)) return false;
                node = parent;
            }
            return node == null;
        }

        private static bool SameAnimationBinding(UICom_Card card, AnimatedCard state)
        {
            if (card == null || card.isDisposed || card.loader_FrontCard?.isDisposed == true || card.loader_FullCard?.isDisposed == true) return false;
            if (!string.Equals(card.loader_FrontCard?.url, state.FrontUrl, StringComparison.Ordinal)
                || !string.Equals(card.loader_FullCard?.url, state.FullUrl, StringComparison.Ordinal)) return false;
            // Video rerenders can change only VideoKey while keeping both old image URLs.
            if (card.isVideo != null && card.isVideo.selectedIndex != 0)
            {
                string key = card.frontState?.selectedIndex == 1 ? card.video_FrontCard?.VideoKey : card.video_FullCard?.VideoKey;
                if (string.IsNullOrEmpty(key) || !string.Equals(key, state.Key, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private void VisitCards(GObject node, int depth)
        {
            if (node == null || node.isDisposed || !node.visible || depth > 20
                || (node.group != null && !node.group.visible)
                || (node.parent != null && !node.parent.IsChildInView(node))) return;
            if (node is UIHandCard_Button_Card handCard)
            {
                ApplyToCardButton(handCard);
                return;
            }
            if (node is NewGameLibraryWindow gallery) ApplyToGallery(gallery);
            if (node is DisplayCardWindow preview) ApplyToPreview(preview);
            if (node is UICom_Card card)
            {
                ApplyToVisibleCard(card);
                return;
            }
            if (node is GComponent component)
                for (int i = 0; i < component.numChildren; i++) VisitCards(component.GetChildAt(i), depth + 1);
        }

        private static readonly FieldInfo PreviewCards = typeof(DisplayCardWindow).GetField("_HandCardInfos", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo PreviewIndex = typeof(DisplayCardWindow).GetField("_CurIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo PreviewAlternates = typeof(DisplayCardWindow).GetField("currentAltCardInfos", BindingFlags.NonPublic | BindingFlags.Instance);
        private void ApplyToPreview(DisplayCardWindow window)
        {
            if (!(window.contentPane is UIDisplayCardWindow ui) || ui.state.selectedIndex != 1 || !(ui.AltArtCard is UICom_Card card)) return;
            var configs = PreviewCards?.GetValue(window) as List<CardInfoConfigure>;
            if (configs == null || !(PreviewIndex?.GetValue(window) is int index) || index < 0 || index >= configs.Count) return;
            var config = configs[index];
            var view = config.GetMainPlayerCardView();
            var variants = PreviewAlternates?.GetValue(window) as List<(int, bool)>;
            if (variants != null && ui.list_altCard != null)
                for (int i = 0; i < ui.list_altCard.numChildren && i < variants.Count; i++)
                    if (ui.list_altCard.GetChildAt(i) is UIDisplayCard_Button_AltArt item && item.isSelected.selectedIndex == 1)
                    { view = config.GetCardView(variants[i].Item1); break; }
            TryApplyAnimation(card, view.Key, view.IsAltArtCard);
        }

        private void ApplyToGallery(NewGameLibraryWindow window)
        {
            if (!(window.contentPane is UINewGameLibraryWindow ui)) return;
            var list = ui.com_Cords?.list_Cards;
            if (list == null) return;
            for (int i = 0; i < list.numChildren; i++)
            {
                var item = list.GetChildAt(i) as UINewGameLibrary_Button_Card;
                if (item == null || !(item.data is int index) || !(item.com_card is UICom_Card card) || !IsVisibleCard(card)) continue;
                string file = null;
                if (ui.label.selectedIndex == 1 && index >= 0 && index < window.CardInfos.Count)
                {
                    var config = window.CardInfos[index];
                    var view = config.GetMainPlayerCardView();
                    if (TryApplyAnimation(card, view.Key, view.IsAltArtCard)) continue;
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
            if (_visitedAnimations.Contains(card)) return;
            if (card.isVideo != null && card.isVideo.selectedIndex != 0)
            {
                string videoKey = card.frontState?.selectedIndex == 1 ? card.video_FrontCard?.VideoKey : card.video_FullCard?.VideoKey;
                TryApplyAnimation(card, videoKey, true);
                return;
            }
            else if (_animatedCards.TryGetValue(card, out var previous) && previous.Video != 0
                && (_animations.OwnsTexture(card.loader_FrontCard?.texture) || _animations.OwnsTexture(card.loader_FullCard?.texture)))
            {
                TryApplyAnimation(card, previous.Key, true);
                return;
            }
            if (TryApplyAnimation(card, card.loader_FullCard?.url, false)
                || TryApplyAnimation(card, card.loader_FrontCard?.url, false)) return;
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
            var self = GameLogicManager.inst?.battle?.GetSelfPlayerData();
            var view = self?.player != null ? cardBtn._config.GetBattlePlayerCardView(self.player.Id) : cardBtn._config.GetMainPlayerCardView();
            if (TryApplyAnimation(card, view.Key, view.IsAltArtCard)) return;
            if (view.IsAltArtCard || IsProtectedCard(card)) return;
            string key = card.loader_FrontCard?.url;
            if (!_catalog.TryGetFile(key, out var file))
                _model.TryGetSkinForCard(cardBtn.CardData.CardId, GetName(cardBtn._config?.NameID ?? 0, UIStringType.Card), cardBtn._config?.GetImage(), out file);
            ApplyFile(card, file);
        }

        private bool TryApplyAnimation(UICom_Card card, string key, bool alternate)
        {
            if (!IsVisibleCard(card) || !_catalog.TryGetAnimation(key, out var animation)) return false;
            if (_animations.IsFailed(animation))
            {
                ResetAnimatedLoaders(card);
                _animatedCards.Remove(card);
                return false;
            }
            if ((alternate || IsProtectedCard(card)) && !_catalog.IsAlternateAnimation(key)) return false;
            if (!_animatedCards.TryGetValue(card, out var state) || state.Animation != animation
                || !string.Equals(state.Key, key, StringComparison.Ordinal) || !SameAnimationBinding(card, state))
            {
                if (state != null) { ResetAnimatedLoaders(card); _animatedCards.Remove(card); }
                string front = card.loader_FrontCard?.url, full = card.loader_FullCard?.url;
                state = new AnimatedCard { Animation = animation, Key = key,
                    Video = card.isVideo?.selectedIndex ?? 0, Layout = card.frontState?.selectedIndex ?? 0,
                    FrontUrl = front, FullUrl = full,
                    FrontAssetKey = string.IsNullOrEmpty(front) ? key : front,
                    FullAssetKey = string.IsNullOrEmpty(full) ? key : full };
                state.FrontSequenceKey = animation.ManifestPath + "\n" + state.FrontAssetKey;
                state.FullSequenceKey = animation.ManifestPath + "\n" + state.FullAssetKey;
                _animatedCards[card] = state;
            }
            _visitedAnimations.Add(card);
            ApplyAnimatedFrame(card, state);
            return true;
        }

        private void ApplyAnimatedFrame(UICom_Card card, AnimatedCard state)
        {
            var animation = state.Animation;
            int index = _animations.FrameIndex(animation);
            // Refresh demand even for one-frame animations and fetch newly prepared frames immediately.
            var frontTexture = _animations.GetFrame(animation, state.FrontAssetKey, state.FrontSequenceKey);
            var fullTexture = state.FrontAssetKey == state.FullAssetKey ? frontTexture
                : _animations.GetFrame(animation, state.FullAssetKey, state.FullSequenceKey);
            if (frontTexture == null || fullTexture == null) return;
            if (state.Applied && state.FrameIndex == index && state.FrontTexture == frontTexture && state.FullTexture == fullTexture && state.FrontTexture != null && !state.FrontTexture.disposed
                && state.FullTexture != null && !state.FullTexture.disposed
                && card.loader_FrontCard?.texture == state.FrontTexture && card.loader_FullCard?.texture == state.FullTexture
                && (card.isVideo == null || card.isVideo.selectedIndex == 0)
                && card.video_FrontCard?.visible != true && card.video_FullCard?.visible != true
                && (card.frontState == null || card.frontState.selectedIndex == 0))
            {
                InjectAnimationFrame(state.FrontUrl, state.FrontTexture, animation);
                InjectAnimationFrame(state.FullUrl, state.FullTexture, animation);
                return;
            }
            // Both frames are ready; until then the existing poster/video remains intact.
            // Stop both native movie targets, including late game callbacks after a render reorder.
            if ((card.isVideo != null && card.isVideo.selectedIndex != 0)
                || card.video_FrontCard?.visible == true || card.video_FullCard?.visible == true)
            {
                var movies = SimpleSingletonProvider<CriMovieManager>.inst;
                if (card.video_FrontCard != null) { movies?.StopAuto(card.video_FrontCard); movies?.StopAndDestroy(card.video_FrontCard); }
                if (card.video_FullCard != null) { movies?.StopAuto(card.video_FullCard); movies?.StopAndDestroy(card.video_FullCard); }
            }
            if (card.isVideo != null) card.isVideo.selectedIndex = 0;
            // Animated skins use the ordinary card frame, including explicit video alternates.
            // ForceFullCard applies only to static images; Toys crops every frame to 404 x 400.
            if (card.frontState != null) card.frontState.selectedIndex = 0;
            ApplyLoaderTexture(card.loader_FrontCard, frontTexture);
            ApplyLoaderTexture(card.loader_FullCard, fullTexture);
            state.FrontUrl = card.loader_FrontCard?.url;
            state.FullUrl = card.loader_FullCard?.url;
            state.FrontTexture = frontTexture;
            state.FullTexture = fullTexture;
            state.FrameIndex = index;
            state.Applied = true;
            InjectAnimationFrame(state.FrontUrl, frontTexture, animation);
            InjectAnimationFrame(state.FullUrl, fullTexture, animation);
        }

        private void InjectAnimationFrame(string key, NTexture texture, CardAnimation animation)
        {
            if (string.IsNullOrEmpty(key) || !_catalog.TryGetAnimation(key, out var bound) || bound != animation) return;
            var cache = _currentTextureCache;
            if (cache == null) return;
            if (!cache.TryGetValue(key, out var current) || current != texture) cache[key] = texture;
            if (!_injected.TryGetValue(key, out var injected) || injected != texture) _injected[key] = texture;
            _animationInjected.Add(key);
            _activeAnimationKeys.Add(key);
        }

        private void RemoveUnusedAnimationCache()
        {
            var cache = _currentTextureCache;
            _unusedAnimationKeys.Clear();
            foreach (string key in _animationInjected)
                if (!_activeAnimationKeys.Contains(key)) _unusedAnimationKeys.Add(key);
            foreach (string key in _unusedAnimationKeys)
            {
                if (cache != null && _injected.TryGetValue(key, out var ours)
                    && cache.TryGetValue(key, out var current) && current == ours) cache.Remove(key);
                _injected.Remove(key);
                _animationInjected.Remove(key);
            }
        }

        private void ResetAnimatedLoaders(UICom_Card card)
        {
            if (card == null || card.isDisposed) return;
            // A pooled/rebound card belongs to the game again: never restore its old content/video.
            if (!_animatedCards.TryGetValue(card, out var original) || !original.Applied || !SameAnimationBinding(card, original)) return;
            bool frontOwned = _animations.OwnsTexture(card.loader_FrontCard?.texture);
            bool fullOwned = _animations.OwnsTexture(card.loader_FullCard?.texture);
            if (!frontOwned && !fullOwned) return;
            if (frontOwned) ResetLoader(card.loader_FrontCard);
            if (fullOwned) ResetLoader(card.loader_FullCard);
            if (card.frontState != null) card.frontState.selectedIndex = original.Layout;
            if (card.isVideo != null) card.isVideo.selectedIndex = original.Video;
            if (original.Video != 0)
            {
                var graph = original.Layout == 1 ? card.video_FrontCard : card.video_FullCard;
                var movies = SimpleSingletonProvider<CriMovieManager>.inst;
                if (graph != null && movies != null) movies.PlaAutoReleaseVideo(original.Key, graph, 0, false, true, 0).Forget();
            }
        }
        private static void ResetLoader(GLoader loader)
        {
            if (loader == null || loader.isDisposed) return;
            string url = loader.url;
            loader.ClearContent();
            if (!string.IsNullOrEmpty(url)) { loader.url = null; loader.url = url; }
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
                && !_loader.OwnsTexture(card.loader_FullCard?.texture)
                && !_animations.OwnsTexture(card.loader_FrontCard?.texture)
                && !_animations.OwnsTexture(card.loader_FullCard?.texture);
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
            }
            catch (Exception ex) { Warn("释放卡面缓存失败: " + ex.Message); }
            finally
            {
                _injected.Clear();
                foreach (var card in _animatedCards.Keys) { try { ResetAnimatedLoaders(card); } catch { } }
                _animatedCards.Clear();
                _animationInjected.Clear();
                _activeAnimationKeys.Clear();
                _visitedAnimations.Clear();
                _animations.Dispose();
            }
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
