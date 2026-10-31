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

[assembly: ModManifest("角色皮肤", "0.2.7", "CesiumLoader", "多角色皮肤方案、官方动画与自定义资源覆盖", Permissions = ModPermission.ReadGameState | ModPermission.UI | ModPermission.ModifyGameState | ModPermission.FileWrite, SdkVersion = "2.8.6")]
namespace CharacterSkinMod
{
    public sealed partial class ModEntry : ModBase
    {
        private struct FrameGeometry { public float PivotX; public float PivotY; public float PixelsPerUnit; }
        private sealed class FrameMetadata { public List<FrameMetadataEntry> Sprites { get; set; } }
        private sealed class FrameMetadataEntry { public string Name { get; set; } public float[] Size { get; set; } public float[] Pivot { get; set; } public float PPU { get; set; } }
        private sealed class Package
        {
            public CharacterSkinManifest Manifest; public string Root;
            // 原帧尺寸/锚点缓存（source-metadata.json 或 legacy-animation-source.json）。
            // 变体动作与沿用帧靠它拿到"该自定义帧对应的那张原帧"的 pivot/PPU。
            public Dictionary<string, FrameGeometry> FrameGeometry;
        }
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
            public Sprite Original; public Sprite Applied;
            public readonly ActorPlayback Playback = new ActorPlayback();
            public UnityEngine.Object OriginalController; public UnityEngine.Object AppliedController;
            public object[] OriginalStates;
            // 位置抖动诊断：每个上报周期内记录精灵相对 transform 的纵向偏移与 transform
            // 自身的纵向位置范围，用来区分"我们的锚点/尺寸在变""游戏在移动角色"和"贴图本身在动"。
            public bool Sampled; public float OffsetMinY, OffsetMaxY, PositionMinY, PositionMaxY; public int Dropped;
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
        public override string Version => "0.2.7";
        public static void Main() { SdkManifest.ExportSidecar(); Run(new ModEntry(), tag: "CharacterSkin"); }
        public override void OnInitialize()
        {
            try
            {
                if (Config != null && CharacterSkinConfigDocs.Apply(Config)) Config.SaveIfDirty();
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
                { "ImageApplications", _imageApplications }, { "SpriteApplications", _spriteApplications }, { "ControllerApplications", _controllerApplications }, { "Warnings", _warnings.Count }, { "WarningMessages", new List<string>(_warnings) },
                { "PositionSamples", PositionSamples() }
            })));
        }
        // 每个上报周期（约 2 秒）给出每只角色的：精灵相对 transform 的纵向偏移范围、
        // transform 自身纵向位置范围、以及被判定掉出扫描而被还原原图的次数。
        // 偏移范围大 = 我们给的锚点/尺寸在变；位置范围大 = 游戏自己在移动角色；
        // 两者都稳定但画面在抖 = 贴图内容在动。取样后清空，便于连续观察。
        private List<object> PositionSamples()
        {
            var samples = new List<object>();
            foreach (var a in _actors)
            {
                if (a.Renderer == null || !a.Sampled) continue;
                samples.Add(new Dictionary<string, object> {
                    { "Hero", a.Target.Plan.HeroId }, { "Action", a.Playback.Action },
                    { "OffsetY", new List<object> { Math.Round(a.OffsetMinY, 3), Math.Round(a.OffsetMaxY, 3) } },
                    { "PositionY", new List<object> { Math.Round(a.PositionMinY, 3), Math.Round(a.PositionMaxY, 3) } },
                    { "Restored", a.Dropped }
                });
                a.Sampled = false; a.Dropped = 0;
            }
            return samples;
        }
        private void SamplePosition(Actor a)
        {
            if (a.Renderer == null) return;
            float positionY = a.Renderer.transform.position.y;
            float offsetY = a.Renderer.bounds.min.y - positionY;
            if (!a.Sampled)
            {
                a.Sampled = true; a.OffsetMinY = a.OffsetMaxY = offsetY; a.PositionMinY = a.PositionMaxY = positionY;
                return;
            }
            if (offsetY < a.OffsetMinY) a.OffsetMinY = offsetY;
            if (offsetY > a.OffsetMaxY) a.OffsetMaxY = offsetY;
            if (positionY < a.PositionMinY) a.PositionMinY = positionY;
            if (positionY > a.PositionMaxY) a.PositionMaxY = positionY;
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
    }
}
