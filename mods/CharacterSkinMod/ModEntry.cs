using System;
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
using Core;
using Core.Unit;
using FairyGUI;
using GameLogic;
using Tools;
using UI;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

[assembly: ModManifest("角色皮肤", "0.2.1", "CesiumLoader", "多角色皮肤方案、官方动画与自定义资源覆盖", Permissions = ModPermission.ReadGameState | ModPermission.UI | ModPermission.ModifyGameState | ModPermission.FileWrite, SdkVersion = "2.4.3")]
namespace CharacterSkinMod
{
    public sealed class ModEntry : ModBase
    {
        private sealed class Package { public CharacterSkinManifest Manifest; public string Root; }
        private sealed class Patch
        {
            public object Object; public PropertyInfo Property; public object Original; public object Applied;
            public void Restore() { if (Equals(Property.GetValue(Object, null), Applied)) Property.SetValue(Object, Original, null); }
        }
        private sealed class Target
        {
            public SchemeRuntimeTarget Plan; public Package Package;
            public UnityEngine.Object OriginalController; public UnityEngine.Object SourceController;
        }
        private sealed class SkinPatch
        {
            public System.Collections.IList List; public object[] Original; public object[] Applied;
            public static object[] Snapshot(System.Collections.IList list) { var result = new object[list.Count]; list.CopyTo(result, 0); return result; }
            public static bool Matches(System.Collections.IList list, object[] values)
            {
                if (list.Count != values.Length) return false;
                for (int i = 0; i < values.Length; i++) if (!Equals(list[i], values[i])) return false;
                return true;
            }
            public static void Set(System.Collections.IList list, object[] values) { list.Clear(); foreach (var value in values) list.Add(value); }
            public void Restore() { if (Matches(List, Applied)) Set(List, Original); }
        }
        private sealed class Actor
        {
            public Component Component; public SpriteRenderer Renderer; public Target Target;
            public Sprite Original; public Sprite Applied; public string Action; public string LastCustomFile; public double Started;
            public UnityEngine.Object OriginalController; public UnityEngine.Object AppliedController;
            public object[] OriginalStates;
        }
        private readonly Dictionary<string, Package> _packages = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Target> _targets = new List<Target>();
        private readonly List<Patch> _patches = new List<Patch>();
        private readonly List<SkinPatch> _skinPatches = new List<SkinPatch>();
        private readonly List<Actor> _actors = new List<Actor>();
        private readonly Dictionary<string, NTexture> _images = new Dictionary<string, NTexture>();
        private readonly Dictionary<string, NTexture> _cacheOriginals = new Dictionary<string, NTexture>();
        private readonly List<GLoader> _loaders = new List<GLoader>();
        private readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        private readonly HashSet<NTexture> _ownedTextures = new HashSet<NTexture>();
        private readonly Dictionary<string, CharacterAsset> _assets = new Dictionary<string, CharacterAsset>();
        private readonly List<AsyncOperationHandle<object>> _handles = new List<AsyncOperationHandle<object>>();
        private readonly CardTextureLoader _textures = new CardTextureLoader();
        private readonly HashSet<string> _warnings = new HashSet<string>();
        private readonly SchemeConflictRegistry _conflicts = new SchemeConflictRegistry();
        private readonly SkinLayoutAliases _layoutAliases = new SkinLayoutAliases();
        private readonly LayoutAliasCaches<HeroSkinOffset> _layoutCaches = new LayoutAliasCaches<HeroSkinOffset>();
        private static readonly MethodInfo ExternalSuccess = typeof(GLoader).GetMethod("onExternalLoadSuccess", BindingFlags.Instance | BindingFlags.NonPublic);
        private CharacterSkinScheme _scheme;
        private string _state = "Disabled", _error = "", _statusPath;
        private bool _started, _ready, _unloaded;
        private long _nextScan, _nextStatus;
        private int _imageApplications, _spriteApplications, _controllerApplications;
        public override string Name => "角色皮肤";
        public override string Version => "0.2.1";
        public static void Main() { SdkManifest.ExportSidecar(); Run(new ModEntry(), tag: "CharacterSkin"); }
        public override void OnInitialize()
        {
            try
            {
                string mods = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
                if (string.IsNullOrEmpty(mods)) return;
                _statusPath = Path.Combine(mods, "CharacterSkinMod", "status.json");
                string config = Path.Combine(mods, "CharacterSkinMod", "config.json");
                var settings = File.Exists(config) ? CesiumJson.Deserialize<CharacterSkinSettings>(File.ReadAllText(config)) : new CharacterSkinSettings();
                if (settings == null || !settings.Enabled) return;
                string root = Path.Combine(Path.GetDirectoryName(mods), "character-skins");
                if (!string.IsNullOrEmpty(settings.ActiveSchemeId))
                {
                    CharacterSkinSchemeRules.ValidateId(settings.ActiveSchemeId);
                    string dir = CharacterSkinRules.SafePath(Path.Combine(Path.GetDirectoryName(mods), "character-skin-schemes"), settings.ActiveSchemeId);
                    _scheme = CesiumJson.Deserialize<CharacterSkinScheme>(File.ReadAllText(Path.Combine(dir, "character-scheme.json")));
                    CharacterSkinSchemeRules.Validate(_scheme);
                    if (_scheme.Id != settings.ActiveSchemeId) throw new Exception("方案目录与标识不一致");
                    foreach (var rule in _scheme.Rules) if (rule.CustomPackageId.Length > 0) LoadPackage(root, rule.CustomPackageId);
                }
                else
                {
                    _scheme = new CharacterSkinScheme { Id = "legacy", Name = "旧版启用包" };
                    if (settings.ActivePackages != null) foreach (string id in settings.ActivePackages)
                    {
                        var package = LoadPackage(root, id);
                        _scheme.Rules.Add(new CharacterSkinBinding { Id = "rule-" + _scheme.Rules.Count, HeroId = package.Manifest.HeroId, TargetSkinItemId = package.Manifest.SkinItemId, CustomPackageId = id });
                    }
                    CharacterSkinSchemeRules.Validate(_scheme);
                }
                foreach (var rule in _scheme.Rules)
                    if (rule.CustomPackageId.Length > 0 && _packages[rule.CustomPackageId].Manifest.HeroId != rule.HeroId) throw new Exception("自定义包与规则角色不匹配: " + rule.CustomPackageId);
                var manifests = new Dictionary<string, CharacterSkinManifest>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in _packages) manifests[pair.Key] = pair.Value.Manifest;
                CharacterSkinSchemeRules.ValidateSharedOverrides(_scheme, manifests);
                _state = _scheme.Rules.Count == 0 ? "Disabled" : "Pending";
                SdkLog.Info("CharacterSkin", "已读取方案 " + _scheme.Id + "，配置修改需重启游戏。");
            }
            catch (Exception ex) { Fail(ex); }
        }
        private Package LoadPackage(string root, string id)
        {
            if (_packages.TryGetValue(id, out var existing)) return existing;
            CharacterSkinSchemeRules.ValidateId(id);
            string dir = CharacterSkinRules.SafePath(root, id);
            var manifest = CesiumJson.Deserialize<CharacterSkinManifest>(File.ReadAllText(Path.Combine(dir, "character-skin.json")));
            CharacterSkinRules.Validate(manifest, dir, true);
            if (manifest.Id != id) throw new Exception("自定义包目录与标识不一致");
            var package = new Package { Manifest = manifest, Root = dir }; _packages.Add(id, package); return package;
        }
        private void Warn(string text) { if (_warnings.Add(text)) SdkLog.Warn("CharacterSkin", text); }
        private void Guard(string area, Action action) { try { action(); } catch (Exception ex) { Warn(area + ": " + ex.Message); } }
        private void Fail(Exception ex) { _error = ex.Message; _state = "Failed"; _ready = false; Warn("方案应用失败: " + ex); Rollback(); WriteStatus(); }
        private void WriteStatus()
        {
            if (_statusPath == null) return;
            Guard("状态写入", () => File.WriteAllText(_statusPath, CesiumJson.SerializePretty(new Dictionary<string, object> {
                { "Version", Version }, { "UtcTicks", DateTime.UtcNow.Ticks }, { "State", _state }, { "Error", _error },
                { "ActiveSchemeId", _scheme == null ? "" : _scheme.Id }, { "Rules", _scheme == null ? 0 : _scheme.Rules.Count },
                { "Targets", _targets.Count }, { "Packages", _packages.Count }, { "Images", _images.Count }, { "LayoutAliases", _layoutAliases.Count }, { "Actors", _actors.Count }, { "Sprites", _sprites.Count },
                { "ImageApplications", _imageApplications }, { "SpriteApplications", _spriteApplications }, { "ControllerApplications", _controllerApplications }, { "Warnings", _warnings.Count }, { "WarningMessages", new List<string>(_warnings) }
            })));
        }
        public override void OnUpdate()
        {
            foreach (var a in _actors) if (a.Renderer != null && a.Applied != null && a.Renderer.sprite == a.Applied) a.Renderer.sprite = a.Original;
            if (!_started && _state == "Pending" && StaticConfigure.Skin != null && StaticConfigure.Character != null && TextTablesReady())
            { _started = true; _state = "Loading"; Prepare(); }
            long now = DateTime.UtcNow.Ticks / 10000;
            if (now >= _nextStatus) { _nextStatus = now + 2000; WriteStatus(); }
            if (!_ready || now < _nextScan) return;
            _nextScan = now + 500;
            try { SyncLayoutCache(); InjectImages(); Discover(); Visit(GRoot.inst, 0); } catch (Exception ex) { Fail(ex); }
        }
        private bool TextTablesReady()
        {
            foreach (var package in _packages.Values) foreach (string slot in package.Manifest.Texts.Keys)
            {
                if (slot.StartsWith("Skin", StringComparison.Ordinal)) { if (StaticConfigure.STRSkin == null) return false; }
                else if (StaticConfigure.STRCharacter == null) return false;
            }
            return true;
        }
        private async void Prepare()
        {
            try
            {
                var heroes = new Dictionary<int, List<SkinStandingPaintingConfigureItem>>();
                foreach (var rule in _scheme.Rules)
                {
                    if (heroes.ContainsKey(rule.HeroId)) continue;
                    if (!StaticConfigure.Character.InfoDict.TryGetValue(rule.HeroId, out var hero) || !StaticConfigure.Skin.StandingPaintingDict.TryGetValue(hero.StandingPainting, out var group)) throw new Exception("方案角色或皮肤组不存在: " + rule.HeroId);
                    var snapshots = new List<SkinStandingPaintingConfigureItem>();
                    foreach (var skin in group.SkinStandingPaintingConfigureItems) snapshots.Add(skin.Clone());
                    heroes.Add(rule.HeroId, snapshots);
                }
                var plans = SchemeRuntimePlan.Build(_scheme, heroes);
                // Resolve global collisions and decode images before touching configuration.
                foreach (var plan in plans)
                {
                    var target = new Target { Plan = plan, Package = plan.Binding.CustomPackageId.Length == 0 ? null : _packages[plan.Binding.CustomPackageId] };
                    if (target.Package != null) PrepareCustom(target, heroes[plan.HeroId]);
                    _targets.Add(target);
                }
                foreach (var target in _targets)
                {
                    if (target.Plan.Source == null) continue;
                    foreach (string label in new[] { target.Plan.Original.GetCharacterLabel(), target.Plan.Source == null ? target.Plan.Original.GetCharacterLabel() : target.Plan.Source.GetCharacterLabel() })
                    {
                        if (_assets.ContainsKey(label)) continue;
                        var handle = await AddressableHelper.LoadAssetAsync<object>(label + "_" + AddressableLabel.GetPlatformLabel());
                        if (_unloaded) { if (handle.IsValid()) Addressables.Release(handle); return; }
                        _handles.Add(handle);
                        var asset = handle.Result as CharacterAsset;
                        if (asset == null || ProbeReflection.Read(asset, "animationController") == null) throw new Exception("官方角色动画资源无效: " + label);
                        _assets.Add(label, asset);
                    }
                    target.OriginalController = ProbeReflection.Read(_assets[target.Plan.Original.GetCharacterLabel()], "animationController") as UnityEngine.Object;
                    if (target.Plan.Source != null) target.SourceController = ProbeReflection.Read(_assets[target.Plan.Source.GetCharacterLabel()], "animationController") as UnityEngine.Object;
                }
                if (_unloaded) return;
                _layoutAliases.Apply(StaticConfigure.Skin);
                SyncLayoutCache();
                foreach (var target in _targets) Commit(target);
                InjectImages();
                _ready = true; _state = "Applied"; WriteStatus();
            }
            catch (Exception ex) { if (!_unloaded) Fail(ex); }
        }
        private void PrepareCustom(Target target, List<SkinStandingPaintingConfigureItem> originals)
        {
            var plan = target.Plan; var p = target.Package; var m = p.Manifest;
            var packageSource = SkinLayoutAliases.PackageSource(originals, m.SkinItemId);
            var hero = StaticConfigure.Character.InfoDict[plan.HeroId];
            foreach (var image in m.Images)
            {
                string asset;
                string path = CharacterSkinRules.SafePath(p.Root, image.Value);
                if (image.Key.StartsWith("asset:", StringComparison.Ordinal)) asset = image.Key.Substring(6);
                else if (image.Key == "CharacterMap") asset = hero.CharacterMap;
                else
                {
                    asset = "CesiumCharacter_" + _scheme.Id + "_" + plan.HeroId + "_" + plan.Applied.ItemID + "_" + image.Key;
                    _layoutAliases.Register(asset, image.Key, StaticConfigure.Skin, packageSource, plan.Source, plan.Original);
                    if (image.Key.StartsWith("Bust", StringComparison.Ordinal) || image.Key.StartsWith("SfwBust", StringComparison.Ordinal))
                    {
                        var list = image.Key.StartsWith("Sfw", StringComparison.Ordinal) ? plan.Applied.SfwBust : plan.Applied.Bust;
                        int index = image.Key.EndsWith("1", StringComparison.Ordinal) ? 1 : 0;
                        while (list.Count <= index) list.Add("");
                        list[index] = asset;
                    }
                    else
                    {
                        var property = typeof(SkinStandingPaintingConfigureItem).GetProperty(image.Key);
                        if (property == null || !property.CanWrite) throw new Exception("不支持的皮肤图片槽: " + image.Key);
                        property.SetValue(plan.Applied, asset, null);
                    }
                }
                if (string.IsNullOrEmpty(asset)) throw new Exception("图片地址为空: " + image.Key);
                _conflicts.Add("image:" + asset, path);
                if (!_images.ContainsKey(asset))
                {
                    var texture = _textures.GetOrCreateTexture(path, asset);
                    if (texture == null) throw new Exception("无法加载自定义图片: " + path);
                    _images.Add(asset, texture); _ownedTextures.Add(texture);
                }
            }
            foreach (var text in m.Texts)
            {
                int id = TextId(hero, plan.Applied, text.Key);
                if (id == 0) throw new Exception("文案槽没有有效ID: " + text.Key);
                _conflicts.Add((text.Key.StartsWith("Skin", StringComparison.Ordinal) ? "STRSkin:" : "STRCharacter:") + id, text.Value);
            }
            if (m.Media.Count > 0) Warn("媒体资源尚未接入，自定义语音/视频/特效保留原版: " + m.Name);
        }
        private static int TextId(CharacterInfoConfigure hero, SkinStandingPaintingConfigureItem skin, string slot)
        {
            switch (slot) { case "Name": return hero.NameID; case "Nick": return hero.NickID; case "Biography": return hero.BiographyID; case "Lines": return hero.LinesID; case "SkinLong": return skin.LongTextID; case "SkinShort": return skin.ShortTextID; default: return 0; }
        }
        private void PatchValue(object obj, string property, object value)
        {
            if (obj == null) throw new Exception("待覆盖配置不存在");
            var info = obj.GetType().GetProperty(property);
            if (info == null || !info.CanWrite) throw new Exception("配置字段不可写: " + property);
            var patch = new Patch { Object = obj, Property = info, Original = info.GetValue(obj, null), Applied = value };
            _patches.Add(patch); info.SetValue(obj, value, null);
        }
        private void PatchChecked(object obj, string property, object expected, object value)
        {
            foreach (var patch in _patches) if (ReferenceEquals(patch.Object, obj) && patch.Property.Name == property)
            { if (!Equals(patch.Applied, value)) throw new Exception("多个角色共享配置字段冲突: " + property); return; }
            var info = obj.GetType().GetProperty(property);
            if (!Equals(info.GetValue(obj, null), expected)) throw new Exception("准备期间配置已被其他组件修改: " + property);
            if (!Equals(expected, value)) PatchValue(obj, property, value);
        }
        private void PatchList(System.Collections.IList list, object[] expected, object[] value)
        {
            foreach (var patch in _skinPatches) if (ReferenceEquals(patch.List, list))
            { if (!SkinPatch.Matches(list, value)) throw new Exception("多个角色共享皮肤列表冲突"); return; }
            if (!SkinPatch.Matches(list, expected)) throw new Exception("准备期间皮肤列表已被其他组件修改");
            if (SkinPatch.Matches(list, value)) return;
            var item = new SkinPatch { List = list, Original = expected, Applied = value };
            _skinPatches.Add(item); SkinPatch.Set(list, value);
        }
        private void Commit(Target target)
        {
            var plan = target.Plan; var hero = StaticConfigure.Character.InfoDict[plan.HeroId];
            var group = StaticConfigure.Skin.StandingPaintingDict[hero.StandingPainting];
            int index = -1;
            for (int i = 0; i < group.SkinStandingPaintingConfigureItems.Count; i++) if (group.SkinStandingPaintingConfigureItems[i].ItemID == plan.Applied.ItemID) { index = i; break; }
            if (index < 0) throw new Exception("提交时目标皮肤不存在");
            // Protobuf list and map deserialize to separate objects. HeroCardLogic caches the list copy.
            foreach (var skin in SchemeRuntimePlan.FindSkinCopies(StaticConfigure.Skin, hero.StandingPainting, plan.Applied.ItemID)) PatchSkin(skin, plan);
            if (target.Package == null) return;
            foreach (var text in target.Package.Manifest.Texts)
            {
                object table = text.Key.StartsWith("Skin", StringComparison.Ordinal) ? (object)StaticConfigure.STRSkin : StaticConfigure.STRCharacter;
                var dict = ProbeReflection.Read(table, "LocalDict");
                int id = TextId(hero, plan.Applied, text.Key);
                var entry = dict.GetType().GetProperty("Item").GetValue(dict, new object[] { id });
                foreach (string language in new[] { "Simplified", "Traditional", "English", "Japanese", "Korean" }) PatchValue(entry, language, text.Value);
            }
        }
        private void PatchSkin(SkinStandingPaintingConfigureItem original, SchemeRuntimeTarget plan)
        {
            // Preserve identity for all existing HeroCardData and RoomPlayer references.
            foreach (string property in SchemeRuntimePlan.VisualProperties)
            {
                var info = typeof(SkinStandingPaintingConfigureItem).GetProperty(property);
                PatchChecked(original, property, info.GetValue(plan.Original, null), info.GetValue(plan.Applied, null));
            }
            foreach (string property in new[] { "Bust", "SfwBust", "SkinScale", "Actions" })
            {
                var info = typeof(SkinStandingPaintingConfigureItem).GetProperty(property);
                var list = (System.Collections.IList)info.GetValue(original, null);
                var expected = SkinPatch.Snapshot((System.Collections.IList)info.GetValue(plan.Original, null));
                var applied = SkinPatch.Snapshot((System.Collections.IList)info.GetValue(plan.Applied, null));
                PatchList(list, expected, applied);
            }
        }
        private static bool MatchesLayout(HeroSkinOffset actual, SkinOffsetConfigure expected)
        {
            return actual != null && actual.skinOffset.x == expected.OffsetX && actual.skinOffset.y == expected.OffsetY
                && actual.bustSkinOffset.x == expected.CharacterStateSkinOffsetX && actual.bustSkinOffset.y == expected.CharacterStateSkinOffsetY
                && actual.bustSkinScale == expected.CharacterStateSkinScale && actual.reversalAxisX == expected.IsCharacterStateSkinFlipX;
        }
        private void SyncLayoutCache()
        {
            // Do not instantiate GameLogicManager or HeroCardLogic early during configuration loading.
            if (!SimpleSingletonProvider<GameLogicManager>.hasInstance) return;
            var heroCard = SimpleSingletonProvider<GameLogicManager>.inst.heroCard;
            if (heroCard == null) return;
            _layoutCaches.Sync(heroCard.heroSkinOffsetDict, _layoutAliases.Entries, value => new HeroSkinOffset(value), MatchesLayout);
        }
        private Dictionary<string, NTexture> ImageCache() => (Dictionary<string, NTexture>)typeof(TextureManager).GetField("_cachedTextureDict", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(SimpleSingletonProvider<TextureManager>.inst);
        private void InjectImages()
        {
            var cache = ImageCache();
            foreach (var pair in _images)
            {
                if (!_cacheOriginals.ContainsKey(pair.Key)) { cache.TryGetValue(pair.Key, out var old); _cacheOriginals.Add(pair.Key, old); cache[pair.Key] = pair.Value; }
                else if (cache.TryGetValue(pair.Key, out var current) && current != pair.Value) Warn("图片缓存已被其他组件修改，保留其所有权: " + pair.Key);
                else cache[pair.Key] = pair.Value;
            }
        }
        private void Visit(GObject node, int depth)
        {
            if (node == null || node.isDisposed || !node.visible || depth > 24) return;
            if (node is GLoader loader && loader.url != null && _images.TryGetValue(loader.url, out var image) && loader.texture != image)
            {
                if (!ImageCache().TryGetValue(loader.url, out var owned) || owned != image) return;
                if (!_loaders.Contains(loader)) _loaders.Add(loader);
                loader.ClearContent(); image.AddRef();
                try { ExternalSuccess.Invoke(loader, new object[] { image }); }
                catch { if (loader.texture != image) image.ReleaseRef(); throw; }
                _imageApplications++;
            }
            if (node is GComponent component) for (int i = 0; i < component.numChildren; i++) Visit(component.GetChildAt(i), depth + 1);
        }
        private Target Match(int hero, int skin) { foreach (var target in _targets) if (target.Plan.HeroId == hero && target.Plan.Applied.ItemID == skin) return target; return null; }
        private Target MatchUi(CharacterAnimator component)
        {
            Target match = null; int count = 0;
            string atlas = component.GetComponent<SpriteRenderer>()?.sprite?.texture?.name;
            var animator = ProbeReflection.Read(component, "animator");
            var controller = animator == null ? null : ProbeReflection.Read(animator, "runtimeAnimatorController") as UnityEngine.Object;
            foreach (var target in _targets)
            {
                string label = target.Plan.Original.GetCharacterLabel();
                if (!string.IsNullOrEmpty(atlas) && (atlas == label || atlas.StartsWith(label + "_", StringComparison.Ordinal))) { match = target; count++; }
            }
            if (count == 0 && controller != null)
                foreach (var target in _targets) if (controller == target.OriginalController) { match = target; count++; }
            if (count > 1) { Warn("无owner界面角色匹配歧义，保留原控制器/精灵: " + (controller == null ? "null" : controller.name)); return null; }
            return match;
        }
        private void Discover()
        {
            var old = new Dictionary<int, Actor>(); foreach (var a in _actors) if (a.Renderer != null) old[a.Renderer.GetInstanceID()] = a;
            var discovered = new List<Actor>();
            foreach (var component in UnityEngine.Object.FindObjectsOfType<CharacterAnimator>())
            {
                var mapRenderer = component.GetComponent<SpriteRenderer>();
                Actor actor = null;
                if (mapRenderer != null) old.TryGetValue(mapRenderer.GetInstanceID(), out actor);
                var player = component.owner?.player;
                if (component.owner != null && (player == null || player.Hero == null || player.standingPainting == null)) continue;
                var currentAnimator = ProbeReflection.Read(component, "animator");
                var currentController = currentAnimator == null ? null : ProbeReflection.Read(currentAnimator, "runtimeAnimatorController") as UnityEngine.Object;
                Target target = player != null ? Match(player.Hero.HeroId, player.standingPainting.ItemID) : actor != null && actor.AppliedController != null && currentController == actor.AppliedController ? actor.Target : MatchUi(component);
                var renderer = component.GetComponent<SpriteRenderer>();
                if (target == null || renderer == null) { if (actor != null) RestoreActor(actor); old.Remove(mapRenderer == null ? 0 : mapRenderer.GetInstanceID()); continue; }
                if (actor == null) actor = new Actor { Component = component, Renderer = renderer };
                if (actor.Target != target) { RestoreActor(actor); actor.Target = target; actor.Action = null; actor.Applied = null; }
                if (!_actors.Contains(actor)) _actors.Add(actor);
                discovered.Add(actor); old.Remove(mapRenderer == null ? 0 : mapRenderer.GetInstanceID());
                var animator = ProbeReflection.Read(component, "animator");
                if (target.SourceController != null && animator != null)
                {
                    var current = ProbeReflection.Read(animator, "runtimeAnimatorController") as UnityEngine.Object;
                    if (actor.AppliedController != null && current != actor.AppliedController) { actor.OriginalController = null; actor.AppliedController = null; }
                    if (actor.AppliedController == null)
                    {
                        actor.OriginalController = current == target.SourceController ? target.OriginalController : current;
                        actor.OriginalStates = States(animator);
                        actor.AppliedController = target.SourceController;
                        SetController(animator, target.SourceController, actor.OriginalStates); _controllerApplications++;
                    }
                }
            }
            var renderers = new HashSet<int>();
            foreach (var actor in discovered) renderers.Add(actor.Renderer.GetInstanceID());
            foreach (var battle in UnityEngine.Object.FindObjectsOfType<BattleActor>())
            {
                var data = ProbeReflection.Read(battle, "playerData") as BattlePlayerData;
                var player = data == null ? null : data.player;
                if (player == null || player.Hero == null || player.standingPainting == null || battle.actorRenderer == null) continue;
                var target = Match(player.Hero.HeroId, player.standingPainting.ItemID);
                if (target == null || target.Package == null) continue;
                var battleRenderer = battle.actorRenderer.GetComponent<SpriteRenderer>();
                if (battleRenderer == null) continue;
                foreach (var renderer in new[] { battleRenderer })
                {
                    if (!renderers.Add(renderer.GetInstanceID())) continue;
                    Actor actor = null;
                    foreach (var candidate in old.Values) if (candidate.Renderer == renderer) { actor = candidate; break; }
                    if (actor == null) actor = new Actor { Component = battle, Renderer = renderer };
                    else old.Remove(renderer.GetInstanceID());
                    if (actor.Target != target) { RestoreActor(actor); actor.Target = target; actor.Action = null; actor.Applied = null; actor.LastCustomFile = null; }
                    discovered.Add(actor);
                }
            }
            foreach (var actor in old.Values) RestoreActor(actor);
            _actors.Clear(); _actors.AddRange(discovered);
        }
        // AnimationModule is an AOT assembly absent from the extracted hot-update reference set.
        // Reflection resolves the game's real Animator without shipping a conflicting stub assembly.
        private static object[] States(object animator)
        {
            int count = (int)ProbeReflection.Read(animator, "layerCount");
            var states = new object[count];
            var method = animator.GetType().GetMethod("GetCurrentAnimatorStateInfo", new[] { typeof(int) });
            for (int i = 0; i < count; i++) states[i] = method.Invoke(animator, new object[] { i });
            return states;
        }
        private static void SetController(object animator, UnityEngine.Object controller, object[] states)
        {
            var savedParameters = new Dictionary<string, object>();
            foreach (var parameter in (Array)ProbeReflection.Read(animator, "parameters"))
            {
                string kind = ProbeReflection.Read(parameter, "type").ToString();
                string getter = kind == "Float" ? "GetFloat" : kind == "Int" ? "GetInteger" : kind == "Bool" ? "GetBool" : null;
                if (getter == null) continue;
                int hash = (int)ProbeReflection.Read(parameter, "nameHash");
                savedParameters[kind + ":" + hash] = animator.GetType().GetMethod(getter, new[] { typeof(int) }).Invoke(animator, new object[] { hash });
            }
            animator.GetType().GetMethod("set_runtimeAnimatorController").Invoke(animator, new object[] { controller });
            if (controller == null) return;
            foreach (var parameter in (Array)ProbeReflection.Read(animator, "parameters"))
            {
                string kind = ProbeReflection.Read(parameter, "type").ToString();
                int hash = (int)ProbeReflection.Read(parameter, "nameHash");
                if (!savedParameters.TryGetValue(kind + ":" + hash, out var value)) continue;
                string setter = kind == "Float" ? "SetFloat" : kind == "Int" ? "SetInteger" : "SetBool";
                animator.GetType().GetMethod(setter, new[] { typeof(int), value.GetType() }).Invoke(animator, new object[] { hash, value });
            }
            if (states == null) return;
            int count = (int)ProbeReflection.Read(animator, "layerCount");
            var hasState = animator.GetType().GetMethod("HasState", new[] { typeof(int), typeof(int) });
            var play = animator.GetType().GetMethod("Play", new[] { typeof(int), typeof(int), typeof(float) });
            for (int i = 0; i < states.Length && i < count; i++)
            {
                int hash = (int)ProbeReflection.Read(states[i], "fullPathHash");
                float time = (float)ProbeReflection.Read(states[i], "normalizedTime");
                if ((bool)hasState.Invoke(animator, new object[] { i, hash })) play.Invoke(animator, new object[] { hash, i, time });
            }
        }
        private void RestoreActor(Actor actor)
        {
            if (actor.Renderer != null && actor.Applied != null && actor.Renderer.sprite == actor.Applied) actor.Renderer.sprite = actor.Original;
            var animator = actor.Component is CharacterAnimator ? ProbeReflection.Read(actor.Component, "animator") : null;
            if (animator != null && actor.AppliedController != null && ProbeReflection.Read(animator, "runtimeAnimatorController") as UnityEngine.Object == actor.AppliedController)
                SetController(animator, actor.OriginalController, States(animator));
            actor.AppliedController = null; actor.OriginalController = null;
        }
        public override void OnLateUpdate()
        {
            if (!_ready) return;
            try { Discover(); foreach (var a in _actors) if (a.Target.Package != null && a.Renderer != null && a.Component != null && a.Renderer.gameObject.activeInHierarchy) ApplyActor(a); }
            catch (Exception ex) { Fail(ex); }
        }
        private static CharacterAnimation FindAnimation(CharacterSkinManifest manifest, string action)
        {
            if (manifest == null || manifest.Animations == null || string.IsNullOrEmpty(action)) return null;
            foreach (var pair in manifest.Animations)
                if (string.Equals(pair.Key, action, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }
        private void ApplyActor(Actor a)
        {
            Sprite source = a.Renderer.sprite;
            if (source == null || source == a.Applied) return;
            a.Original = source;
            string sourceAction = AnimationPlayback.ActionName(source.name);
            double now = Time.time;
            string file = null; var m = a.Target.Package.Manifest;
            CharacterAnimation animation = FindAnimation(m, sourceAction);
            string action = sourceAction;

            // A complete custom sequence owns the renderer until it finishes. This prevents a
            // short sequence from falling back to an original frame and gives a long one a
            // chance to reach its last frame when the game's controller advances early.
            if (!string.Equals(a.Action, sourceAction, StringComparison.Ordinal) && !string.IsNullOrEmpty(a.Action))
            {
                CharacterAnimation previous = FindAnimation(m, a.Action);
                double previousElapsed = Math.Max(0, now - a.Started) * 1000;
                if (AnimationPlayback.ShouldFinish(previous, previousElapsed))
                {
                    action = a.Action;
                    animation = previous;
                }
            }
            if (!string.Equals(action, a.Action, StringComparison.Ordinal)) { a.Action = action; a.Started = now; a.LastCustomFile = null; }

            // A whole-action sequence takes precedence over SpriteMappings. Mappings remain
            // useful for individual original frames when no sequence was supplied.
            bool mapped = animation == null && m.SpriteMappings.TryGetValue(source.name, out file);
            if (animation == null && !mapped && string.Equals(a.Action, sourceAction, StringComparison.Ordinal) && !string.IsNullOrEmpty(a.LastCustomFile))
            {
                // The original controller may expose more frames than the package mapped.
                // Hold the last custom frame for this action instead of revealing the original.
                file = a.LastCustomFile;
                mapped = true;
            }
            if (animation != null)
            {
                double elapsed = Math.Max(0, now - a.Started) * 1000;
                file = animation.Frames[AnimationPlayback.Frame(animation, elapsed)];
            }
            if (animation == null && !mapped)
            {
                if (a.Applied != null) { a.Renderer.sprite = a.Applied; return; }
                var idle = FindAnimation(m, "Idle");
                if (idle == null) return;
                file = idle.Frames[0];
            }
            if (mapped) a.LastCustomFile = file;
            string path = CharacterSkinRules.SafePath(a.Target.Package.Root, file);
            bool originalLayout = mapped && m.UseOriginalSpriteLayout;
            string key = path + ":" + m.HeroId + ":" + a.Target.Plan.Applied.ItemID + (originalLayout ? ":" + source.GetInstanceID() : "");
            if (!_sprites.TryGetValue(key, out var sprite))
            {
                var owned = _textures.GetOrCreateTexture(path, "sprite:" + key);
                if (owned == null) throw new Exception("无法加载自定义动画帧: " + path);
                _ownedTextures.Add(owned);
                var texture = owned.nativeTexture as Texture2D;
                if (texture == null) throw new Exception("自定义动画纹理类型无效");
                var l = m.Layout;
                var pivot = originalLayout ? new Vector2(source.pivot.x / source.rect.width, source.pivot.y / source.rect.height) : new Vector2(l.PivotX, l.PivotY);
                float ppu = originalLayout ? source.pixelsPerUnit : l.PixelsPerUnit;
                sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, ppu / l.Scale, 0, SpriteMeshType.FullRect);
                sprite.name = "Cesium_" + source.name; _sprites[key] = sprite;
            }
            a.Applied = sprite; a.Renderer.sprite = sprite; if (a.Renderer.sprite == sprite) _spriteApplications++;
        }
        private void Rollback()
        {
            // A newly built hero-card cache may contain aliases even before the next scan.
            Guard("记录布局缓存", SyncLayoutCache);
            Guard("恢复人物布局缓存", () => _layoutCaches.Restore(MatchesLayout));
            Guard("恢复人物布局配置", _layoutAliases.Restore);
            foreach (var actor in _actors) Guard("恢复角色", () => RestoreActor(actor)); _actors.Clear();
            for (int i = _patches.Count - 1; i >= 0; i--) Guard("恢复文案", _patches[i].Restore); _patches.Clear();
            for (int i = _skinPatches.Count - 1; i >= 0; i--) Guard("恢复皮肤配置", _skinPatches[i].Restore); _skinPatches.Clear();
            // Detach custom GLoader references before disposing any owned textures.
            var reload = new Dictionary<GLoader, string>();
            foreach (var loader in _loaders) Guard("解除界面图片引用", () => { if (loader != null && !loader.isDisposed && _textures.OwnsTexture(loader.texture)) { reload[loader] = loader.url; loader.ClearContent(); loader.url = ""; } }); _loaders.Clear();
            Guard("恢复图片缓存", () => {
                var cache = ImageCache();
                foreach (var pair in _images) if (cache.TryGetValue(pair.Key, out var current) && current == pair.Value)
                { if (_cacheOriginals.TryGetValue(pair.Key, out var old) && old != null && !old.disposed) cache[pair.Key] = old; else cache.Remove(pair.Key); }
            });
            _cacheOriginals.Clear(); _images.Clear();
            foreach (var loader in reload) Guard("重新加载原界面图片", () => { if (!loader.Key.isDisposed) loader.Key.url = loader.Value; });
            foreach (var sprite in _sprites.Values) if (sprite != null) UnityEngine.Object.Destroy(sprite); _sprites.Clear();
            foreach (var texture in _ownedTextures) if (!texture.disposed) texture.destroyMethod = DestroyMethod.Destroy;
            _textures.Dispose(); _ownedTextures.Clear();
            foreach (var handle in _handles) Guard("释放官方动画", () => { if (handle.IsValid()) Addressables.Release(handle); }); _handles.Clear(); _assets.Clear();
        }
        public override void OnUnload() { _unloaded = true; _ready = false; Rollback(); _state = "Unloaded"; WriteStatus(); }
    }
}
