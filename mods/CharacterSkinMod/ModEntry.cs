using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CardSkinMod;
using CharacterProbeMod;
using CesiumLoader.CharacterSkins;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;
using Core.Unit;
using FairyGUI;
using GameLogic;
using Tools;
using UI;
using UnityEngine;

[assembly: ModManifest("角色皮肤", "0.1.2", "CesiumLoader", "本地角色图片、文案、地图精灵覆盖", Permissions = ModPermission.ReadGameState | ModPermission.UI | ModPermission.ModifyGameState | ModPermission.FileWrite, SdkVersion = "2.4.3")]
namespace CharacterSkinMod
{
    public sealed class ModEntry : ModBase
    {
        private sealed class Package { public CharacterSkinManifest Manifest; public string Root; }
        private sealed class Patch { public object Object; public PropertyInfo Property; public object Original; public void Restore() { Property.SetValue(Object, Original, null); } }
        private sealed class Actor
        {
            public CharacterAnimator Component; public SpriteRenderer Renderer; public Package Package;
            public Sprite Original; public Sprite Applied; public string Action; public double Started;
        }
        private readonly List<Package> _packages = new List<Package>();
        private readonly List<Patch> _patches = new List<Patch>();
        private readonly List<Actor> _actors = new List<Actor>();
        private readonly Dictionary<string, NTexture> _images = new Dictionary<string, NTexture>();
        private readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        private readonly CardTextureLoader _textures = new CardTextureLoader();
        private readonly HashSet<string> _warnings = new HashSet<string>();
        private static readonly MethodInfo ExternalSuccess = typeof(GLoader).GetMethod("onExternalLoadSuccess", BindingFlags.Instance | BindingFlags.NonPublic);
        private long _nextScan;
        private long _nextStatus;
        private string _statusPath;
        private int _imageApplications;
        private int _spriteApplications;
        private bool _ready;
        public override string Name => "角色皮肤";
        public override string Version => "0.1.2";
        public static void Main() { SdkManifest.ExportSidecar(); Run(new ModEntry(), tag: "CharacterSkin"); }
        public override void OnInitialize()
        {
            string mods = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
            if (string.IsNullOrEmpty(mods)) return;
            _statusPath = Path.Combine(mods, "CharacterSkinMod", "status.json");
            string config = Path.Combine(mods, "CharacterSkinMod", "config.json");
            var settings = File.Exists(config) ? CesiumJson.Deserialize<CharacterSkinSettings>(File.ReadAllText(config)) : new CharacterSkinSettings();
            if (settings == null || !settings.Enabled || settings.ActivePackages == null) return;
            string root = Path.Combine(Path.GetDirectoryName(mods), "character-skins");
            var targets = new HashSet<string>();
            foreach (string id in settings.ActivePackages)
            {
                try
                {
                    string dir = CharacterSkinRules.SafePath(root, id);
                    var manifest = CesiumJson.Deserialize<CharacterSkinManifest>(File.ReadAllText(Path.Combine(dir, "character-skin.json")));
                    CharacterSkinRules.Validate(manifest, dir, true);
                    string key = manifest.HeroId + ":" + manifest.SkinItemId;
                    if (!targets.Add(key)) throw new Exception("同一角色和原皮肤有重复启用包");
                    _packages.Add(new Package { Manifest = manifest, Root = dir });
                }
                catch (Exception ex) { Warn("包 " + id + ": " + ex.Message); }
            }
            SdkLog.Info("CharacterSkin", "已加载 " + _packages.Count + " 个角色皮肤包，修改配置后重启游戏生效。");
        }
        private void Warn(string text) { if (_warnings.Add(text)) SdkLog.Warn("CharacterSkin", text); }
        private void Guard(string area, Action action) { try { action(); } catch (Exception ex) { Warn(area + ": " + ex.Message); } }
        public override void OnUpdate()
        {
            // Restore the source frame before Animator runs again, including static states.
            foreach (var a in _actors) if (a.Renderer != null && a.Applied != null && a.Renderer.sprite == a.Applied) a.Renderer.sprite = a.Original;
            if (_packages.Count == 0) return;
            if (!_ready && StaticConfigure.Skin != null && StaticConfigure.Character != null)
            {
                foreach (var p in _packages) Guard("配置", () => ApplyConfiguration(p));
                _ready = true;
            }
            long now = DateTime.UtcNow.Ticks / 10000;
            if (_statusPath != null && now >= _nextStatus)
            {
                _nextStatus = now + 2000;
                Guard("状态写入", () => File.WriteAllText(_statusPath, CesiumJson.SerializePretty(new Dictionary<string, object> {
                    { "Version", Version }, { "UtcTicks", DateTime.UtcNow.Ticks }, { "Packages", _packages.Count },
                    { "Images", _images.Count }, { "Actors", _actors.Count }, { "Sprites", _sprites.Count },
                    { "ImageApplications", _imageApplications }, { "SpriteApplications", _spriteApplications }, { "Warnings", _warnings.Count }
                })));
            }
            if (!_ready || now < _nextScan) return;
            _nextScan = now + 500;
            Guard("图片缓存", InjectImages);
            Guard("人物扫描", Discover);
            Guard("界面图片", () => Visit(GRoot.inst, 0));
        }
        private void PatchValue(object obj, string property, object value)
        {
            if (obj == null) return;
            var info = obj.GetType().GetProperty(property);
            if (info == null || !info.CanWrite) return;
            _patches.Add(new Patch { Object = obj, Property = info, Original = info.GetValue(obj, null) });
            info.SetValue(obj, value, null);
        }
        private void ApplyConfiguration(Package p)
        {
            var m = p.Manifest;
            if (!StaticConfigure.Character.InfoDict.TryGetValue(m.HeroId, out var hero)) throw new Exception("角色不存在: " + m.HeroId);
            if (!StaticConfigure.Skin.StandingPaintingDict.TryGetValue(hero.StandingPainting, out var group)) throw new Exception("角色皮肤组不存在");
            bool matched = false;
            foreach (var skin in group.SkinStandingPaintingConfigureItems)
            {
                if (m.SkinItemId != 0 && skin.ItemID != m.SkinItemId) continue;
                matched = true;
                foreach (var image in m.Images)
                {
                    string asset;
                    if (image.Key.StartsWith("asset:", StringComparison.Ordinal)) asset = image.Key.Substring(6);
                    else if (image.Key == "CharacterMap") asset = hero.CharacterMap;
                    else if (image.Key.StartsWith("Bust", StringComparison.Ordinal) || image.Key.StartsWith("SfwBust", StringComparison.Ordinal))
                    {
                        var list = image.Key.StartsWith("Sfw", StringComparison.Ordinal) ? skin.SfwBust : skin.Bust;
                        int index = image.Key.EndsWith("1", StringComparison.Ordinal) ? 1 : 0;
                        asset = index < list.Count ? list[index] : "";
                    }
                    else
                    {
                        asset = typeof(SkinStandingPaintingConfigureItem).GetProperty(image.Key)?.GetValue(skin, null) as string;
                        // Assign a private address when an optional slot was empty so it can differ from the full portrait.
                        if (string.IsNullOrEmpty(asset))
                        {
                            asset = "CesiumCharacter_" + m.Id + "_" + skin.ItemID + "_" + image.Key;
                            PatchValue(skin, image.Key, asset);
                        }
                    }
                    if (string.IsNullOrEmpty(asset)) continue;
                    var texture = _textures.GetOrCreateTexture(CharacterSkinRules.SafePath(p.Root, image.Value), asset);
                    if (texture != null) _images[asset] = texture;
                }
                foreach (var text in m.Texts)
                {
                    int id = 0;
                    object table = StaticConfigure.STRCharacter;
                    switch (text.Key)
                    {
                        case "Name": id = hero.NameID; break;
                        case "Nick": id = hero.NickID; break;
                        case "Biography": id = hero.BiographyID; break;
                        case "Lines": id = hero.LinesID; break;
                        case "SkinLong": id = skin.LongTextID; table = StaticConfigure.STRSkin; break;
                        case "SkinShort": id = skin.ShortTextID; table = StaticConfigure.STRSkin; break;
                    }
                    if (id == 0) { Warn("原文案槽位没有 ID，保留原版: " + text.Key); continue; }
                    var dict = ProbeReflection.Read(table, "LocalDict");
                    var indexer = dict.GetType().GetProperty("Item");
                    var entry = indexer.GetValue(dict, new object[] { id });
                    foreach (string language in new[] { "Simplified", "Traditional", "English", "Japanese", "Korean" }) PatchValue(entry, language, text.Value);
                }
            }
            if (!matched) throw new Exception("原皮肤不属于该角色");
            if (m.Media.Count > 0) Warn("媒体资源尚未接入运行时，保留原版语音/视频/特效: " + m.Name);
        }
        private void InjectImages()
        {
            var tm = SimpleSingletonProvider<TextureManager>.inst;
            var cache = (Dictionary<string, NTexture>)typeof(TextureManager).GetField("_cachedTextureDict", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tm);
            foreach (var pair in _images) cache[pair.Key] = pair.Value;
        }
        private void Visit(GObject node, int depth)
        {
            if (node == null || node.isDisposed || !node.visible || depth > 24) return;
            if (node is GLoader loader && loader.url != null && _images.TryGetValue(loader.url, out var image) && loader.texture != image)
            {
                loader.ClearContent();
                image.AddRef();
                ExternalSuccess.Invoke(loader, new object[] { image });
                _imageApplications++;
            }
            if (node is GComponent component) for (int i = 0; i < component.numChildren; i++) Visit(component.GetChildAt(i), depth + 1);
        }
        private Package Match(int hero, int skin)
        {
            Package fallback = null;
            foreach (var p in _packages) if (p.Manifest.HeroId == hero) { if (p.Manifest.SkinItemId == skin) return p; if (p.Manifest.SkinItemId == 0) fallback = p; }
            return fallback;
        }
        private void Discover()
        {
            var old = new Dictionary<int, Actor>();
            foreach (var a in _actors) if (a.Component != null) old[a.Component.GetInstanceID()] = a;
            _actors.Clear();
            foreach (var component in UnityEngine.Object.FindObjectsOfType<CharacterAnimator>())
            {
                var player = component.owner?.player;
                Package package = null;
                if (player != null) package = Match(player.Hero.HeroId, player.standingPainting.ItemID);
                else
                {
                    // UI Playables have no owner. Identify the actual original atlas, never guess by action name.
                    var uiSprite = component.GetComponent<SpriteRenderer>()?.sprite;
                    string atlas = uiSprite?.texture?.name;
                    if (!string.IsNullOrEmpty(atlas))
                    {
                        foreach (var candidate in _packages)
                        {
                            if (!StaticConfigure.Character.InfoDict.TryGetValue(candidate.Manifest.HeroId, out var hero)) continue;
                            if (!StaticConfigure.Skin.StandingPaintingDict.TryGetValue(hero.StandingPainting, out var group)) continue;
                            foreach (var skin in group.SkinStandingPaintingConfigureItems)
                            {
                                if (candidate.Manifest.SkinItemId != 0 && skin.ItemID != candidate.Manifest.SkinItemId) continue;
                                string label = skin.GetCharacterLabel();
                                if (atlas == label || atlas.StartsWith(label + "_", StringComparison.Ordinal)) package = candidate;
                            }
                        }
                    }
                }
                if (package == null) continue;
                var renderer = component.GetComponent<SpriteRenderer>();
                if (renderer == null) continue;
                if (!old.TryGetValue(component.GetInstanceID(), out var a)) a = new Actor { Component = component, Renderer = renderer, Package = package };
                _actors.Add(a);
            }
        }
        public override void OnLateUpdate()
        {
            foreach (var a in _actors) if (a.Renderer != null && a.Component != null && a.Component.gameObject.activeInHierarchy) Guard("精灵替换", () => ApplyActor(a));
        }
        private void ApplyActor(Actor a)
        {
            Sprite source = a.Renderer.sprite;
            if (source == null || source == a.Applied) return;
            a.Original = source;
            string action = source.name;
            int underscore = action.LastIndexOf('_');
            if (underscore > 0) action = action.Substring(0, underscore);
            double now = Time.time;
            if (!string.Equals(action, a.Action, StringComparison.Ordinal)) { a.Action = action; a.Started = now; }
            string file = null;
            var m = a.Package.Manifest;
            bool mapped = m.SpriteMappings.TryGetValue(source.name, out file);
            if (!mapped)
            {
                CharacterAnimation animation = null;
                foreach (var pair in m.Animations) if (string.Equals(pair.Key, action, StringComparison.OrdinalIgnoreCase)) { animation = pair.Value; break; }
                if (animation == null) { a.Applied = null; return; }
                double elapsed = Math.Max(0, now - a.Started) * 1000;
                double length = 0;
                for (int i = 0; i < animation.Frames.Count; i++) length += animation.DurationsMs.Count == 0 ? 1000.0 / animation.Fps : animation.DurationsMs[i];
                elapsed = animation.Loop ? elapsed % length : Math.Min(elapsed, length - 0.001);
                int frame = 0;
                while (frame < animation.Frames.Count - 1)
                {
                    double duration = animation.DurationsMs.Count == 0 ? 1000.0 / animation.Fps : animation.DurationsMs[frame];
                    if (elapsed < duration) break;
                    elapsed -= duration; frame++;
                }
                file = animation.Frames[frame];
            }
            string path = CharacterSkinRules.SafePath(a.Package.Root, file);
            bool originalLayout = mapped && m.UseOriginalSpriteLayout;
            string key = path + ":" + m.HeroId + ":" + m.SkinItemId + (originalLayout ? ":" + source.GetInstanceID() : "");
            if (!_sprites.TryGetValue(key, out var sprite))
            {
                var texture = _textures.GetOrCreateTexture(path, "sprite:" + key)?.nativeTexture as Texture2D;
                if (texture == null) return;
                var l = m.Layout;
                var pivot = originalLayout ? new Vector2(source.pivot.x / source.rect.width, source.pivot.y / source.rect.height) : new Vector2(l.PivotX, l.PivotY);
                float ppu = originalLayout ? source.pixelsPerUnit : l.PixelsPerUnit;
                sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, ppu / l.Scale, 0, SpriteMeshType.FullRect);
                sprite.name = "Cesium_" + source.name;
                _sprites[key] = sprite;
            }
            a.Applied = sprite;
            a.Renderer.sprite = sprite;
            if (a.Renderer.sprite == sprite) _spriteApplications++;
        }
        public override void OnUnload()
        {
            foreach (var a in _actors) if (a.Renderer != null && a.Renderer.sprite == a.Applied) a.Renderer.sprite = a.Original;
            for (int i = _patches.Count - 1; i >= 0; i--) Guard("恢复文案", _patches[i].Restore);
            try
            {
                var cache = (Dictionary<string, NTexture>)typeof(TextureManager).GetField("_cachedTextureDict", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(SimpleSingletonProvider<TextureManager>.inst);
                foreach (var image in _images) if (cache.TryGetValue(image.Key, out var existing) && existing == image.Value) cache.Remove(image.Key);
            }
            catch { }
            foreach (var sprite in _sprites.Values) if (sprite != null) UnityEngine.Object.Destroy(sprite);
            _textures.Dispose();
        }
    }
}
