using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;
using Core.Unit;
using FairyGUI;
using GameLogic;
using Tools;
using UI;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: ModManifest("角色资源只读探针", "0.1.2", "CesiumLoader",
    "只读采集角色图片、进度条头像、动画与精灵元数据",
    Permissions = ModPermission.ReadGameState | ModPermission.FileWrite, SdkVersion = "2.3.2")]

namespace CharacterProbeMod
{
    public sealed class ModEntry : ModBase
    {
        private sealed class Actor
        {
            public CharacterAnimator Component;
            public SpriteRenderer Renderer;
            public int Id;
            public int LastSprite;
            public string LastState;
            public bool StateFailed;
            public string Context;
            public object Animator;
        }
        private readonly List<Actor> _actors = new List<Actor>();
        private readonly HashSet<int> _sprites = new HashSet<int>();
        private readonly HashSet<int> _controllers = new HashSet<int>();
        private readonly HashSet<int> _clips = new HashSet<int>();
        private readonly HashSet<string> _skinRows = new HashSet<string>();
        private readonly HashSet<string> _uiRows = new HashSet<string>();
        private readonly HashSet<string> _errors = new HashSet<string>();
        private readonly Dictionary<int, string> _actorContexts = new Dictionary<int, string>();
        private ProbeLog _log;
        private string _sessionDir;
        private string _scene;
        private long _nextDiscover;
        private long _nextFlush;
        private bool _running;
        private bool _fashionCaptured;
        private int _frameChanges;
        public override string Name => "角色资源只读探针";
        public override string Version => "0.1.2";
        public static void Main() { SdkManifest.ExportSidecar(); Run(new ModEntry(), tag: "CharacterProbe"); }

        public override void OnInitialize()
        {
            string mods = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
            if (string.IsNullOrEmpty(mods)) { SdkLog.Warn("CharacterProbe", "CESIUM_MODS_DIR 缺失，探针未启动。"); return; }
            _sessionDir = Path.Combine(mods, "CharacterProbeMod", "captures", DateTime.UtcNow.Ticks.ToString());
            Directory.CreateDirectory(_sessionDir);
            _log = new ProbeLog(Path.Combine(_sessionDir, "observations.jsonl"));
            _running = true;
            Emit(Row("session").S("version", Version).S("unity", Application.unityVersion)
                .S("note", "Read-only; sampled after animation; metadata only; no texture pixel export"));
            FlushAndStatus();
            SdkLog.Info("CharacterProbe", "只读角色探针已启动，记录目录: " + _sessionDir);
        }

        // Every capture row is submitted explicitly. This helper adds timing but does not write yet.
        private JsonRow Row(string kind)
        {
            return new JsonRow().S("kind", kind).N("utcTicks", DateTime.UtcNow.Ticks).N("frame", Time.frameCount).S("scene", _scene);
        }
        private void Emit(JsonRow row)
        {
            if (!_running) return;
            try { _log.Add(row); }
            catch (Exception ex)
            {
                _running = false;
                SdkLog.Warn("CharacterProbe", "探针写盘失败，已停止采集: " + ex.Message);
            }
        }

        public override void OnLateUpdate()
        {
            if (!_running) return;
            long now = DateTime.UtcNow.Ticks / 10000;
            if (now >= _nextDiscover)
            {
                _nextDiscover = now + 2000;
                Guard("scene", CaptureScene);
                Guard("roster", CaptureRoster);
                Guard("fashion", CaptureFashion);
                Guard("discover", DiscoverActors);
                Guard("ui", () => { if (GRoot.inst != null) VisitUI(GRoot.inst, "root", 0); });
            }
            foreach (var actor in _actors)
            {
                if (actor.Component == null || actor.Renderer == null || !actor.Component.gameObject.activeInHierarchy) continue;
                try { CaptureActor(actor); }
                catch (Exception ex) { Error("actor-" + actor.Id, ex); }
            }
            if (now >= _nextFlush)
            {
                _nextFlush = now + 2000;
                try { FlushAndStatus(); }
                catch (Exception ex)
                {
                    _running = false;
                    SdkLog.Warn("CharacterProbe", "探针写盘失败，已停止采集: " + ex.Message);
                }
            }
        }

        private void Guard(string area, Action action)
        {
            try { action(); }
            catch (Exception ex) { Error(area, ex); }
        }
        private void Error(string area, Exception ex)
        {
            string key = area + ":" + ex.GetType().Name + ":" + ex.Message;
            if (!_errors.Add(key)) return;
            Emit(Row("error").S("area", area).S("exception", ex.GetType().Name).S("message", ex.Message));
            SdkLog.Warn("CharacterProbe", key);
        }
        private void CaptureScene()
        {
            string name = SceneManager.GetActiveScene().name;
            if (name == _scene) return;
            _scene = name;
            Emit(Row("scene"));
        }
        private void CaptureRoster()
        {
            var battle = GameLogicManager.inst?.battle;
            if (battle == null) return;
            foreach (var data in battle.PlayerDatas)
            {
                if (data?.player == null) continue;
                var p = data.player;
                var skin = p.standingPainting;
                if (skin == null) continue;
                string signature = p.Id + ":" + p.Hero.HeroId + ":" + skin.ItemID + ":" + _scene;
                if (_skinRows.Add(signature))
                {
                    var row = Row("skin").N("player", p.Id).N("hero", p.Hero.HeroId).N("skinItem", skin.ItemID)
                        .S("label", skin.CharacterLabel).S("characterMap", p.characterConfig?.CharacterMap)
                        .S("profilePhoto", skin.ProfilePhoto).S("character", skin.Character).S("sfwCharacter", skin.SfwCharacter)
                        .S("hud", skin.InGameCharacter).S("hudSfw", skin.InGameCharacterSFW)
                        .S("rolePhoto", skin.RolePhoto).S("ready", skin.CharacterReady).S("thin", skin.CharacterThin)
                        .S("landIcon", skin.LandIcon).N("longTextId", skin.LongTextID).N("shortTextId", skin.ShortTextID);
                    string bust = "";
                    foreach (string b in skin.Bust) bust += (bust.Length == 0 ? "" : "|") + b;
                    row.S("bust", bust);
                    try { row.S("accountHead", p.HeadURL()); } catch (Exception ex) { Error("account-head", ex); }
                    Emit(row);
                }
            }
        }
        private void CaptureFashion()
        {
            if (_fashionCaptured || StaticConfigure.Fashion == null || StaticConfigure.Fashion.AccountHeadShotDict.Count == 0) return;
            foreach (var pair in StaticConfigure.Fashion.AccountHeadShotDict)
            {
                string url = pair.Value.GetHeadShot();
                if (url != null && url.IndexOf("70914", StringComparison.Ordinal) >= 0)
                    Emit(Row("accountHeadMapping").N("fashionId", pair.Key).S("url", url));
            }
            _fashionCaptured = true;
        }
        private string ContextFor(CharacterAnimator component)
        {
            var p = component.owner?.player;
            if (p == null) return "ui-or-unowned";
            return "player=" + p.Id + ";hero=" + p.Hero.HeroId + ";skin=" + (p.standingPainting?.ItemID ?? 0);
        }
        private void DiscoverActors()
        {
            // One scan every two seconds. Observe only CharacterAnimator instances, never every SpriteRenderer in the scene.
            var found = UnityEngine.Object.FindObjectsOfType<CharacterAnimator>();
            var previous = new Dictionary<int, Actor>();
            foreach (var actor in _actors) previous[actor.Id] = actor;
            _actors.Clear();
            foreach (var component in found)
            {
                if (component == null) continue;
                var renderer = component.GetComponent<SpriteRenderer>();
                if (renderer == null) continue;
                int id = component.GetInstanceID();
                string context = ContextFor(component);
                if (!previous.TryGetValue(id, out var actor) || actor.Component != component)
                    actor = new Actor { Component = component, Renderer = renderer, Id = id };
                actor.Context = context;
                _actors.Add(actor);
                UnityEngine.Object controller = null;
                Guard("actor-controller", () =>
                {
                    actor.Animator = typeof(CharacterAnimator).GetField("animator").GetValue(component);
                    controller = Get(actor.Animator, "runtimeAnimatorController") as UnityEngine.Object;
                });
                if (!_actorContexts.TryGetValue(id, out var old) || old != context)
                {
                    _actorContexts[id] = context;
                    Emit(Row("actor").N("actor", id).S("context", context).S("path", TransformPath(component.transform))
                        .N("renderer", renderer.GetInstanceID()).S("controller", controller?.name)
                        .F("scaleX", component.transform.localScale.x).F("scaleY", component.transform.localScale.y));
                }
                if (controller != null) Guard("controller", () => CaptureController(controller));
            }
        }
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();
        private static object Get(object obj, string name) { return ProbeReflection.Read(obj, name); }
        private static object LayerCall(object obj, string name)
        {
            string key = obj.GetType().FullName + ":" + name;
            if (!Methods.TryGetValue(key, out var method))
            {
                method = obj.GetType().GetMethod(name, new[] { typeof(int) });
                if (method == null) throw new Exception("Probe method unavailable: " + obj.GetType().FullName + "." + name);
                Methods[key] = method;
            }
            return method.Invoke(obj, new object[] { 0 });
        }
        private void CaptureController(UnityEngine.Object controller)
        {
            if (!_controllers.Add(controller.GetInstanceID())) return;
            var clips = (Array)Get(controller, "animationClips");
            Emit(Row("controller").N("controllerId", controller.GetInstanceID()).S("name", controller.name).N("clipCount", clips.Length));
            foreach (UnityEngine.Object clip in clips) Guard("clip", () => CaptureClip(clip, controller.name));
        }
        private void CaptureClip(UnityEngine.Object clip, string controller)
        {
            if (clip == null || !_clips.Add(clip.GetInstanceID())) return;
            Emit(Row("clip").N("clipId", clip.GetInstanceID()).S("name", clip.name).S("controller", controller)
                .F("length", (float)Get(clip, "length")).F("frameRate", (float)Get(clip, "frameRate")).B("loop", (bool)Get(clip, "isLooping")));
            try
            {
                foreach (var evt in (IEnumerable)Get(clip, "events"))
                    Emit(Row("animationEvent").N("clipId", clip.GetInstanceID()).S("function", (string)Get(evt, "functionName"))
                        .F("time", (float)Get(evt, "time")).S("stringArg", (string)Get(evt, "stringParameter"))
                        .F("floatArg", (float)Get(evt, "floatParameter")).N("intArg", (int)Get(evt, "intParameter")));
            }
            catch (Exception ex) { Error("clip-events", ex); }
        }
        private void CaptureActor(Actor actor)
        {
            var sprite = actor.Renderer.sprite;
            int sid = sprite == null ? 0 : sprite.GetInstanceID();
            if (sid != actor.LastSprite)
            {
                actor.LastSprite = sid;
                _frameChanges++;
                if (sprite != null && !_sprites.Contains(sid)) Guard("sprite", () => CaptureSprite(sprite));
                Emit(Row("spriteChange").N("actor", actor.Id).S("context", actor.Context).N("spriteId", sid)
                    .S("sprite", sprite?.name).B("flipX", actor.Renderer.flipX).B("flipY", actor.Renderer.flipY));
            }
            var animator = actor.Animator;
            if (animator == null || actor.StateFailed) return;
            try
            {
                var controller = Get(animator, "runtimeAnimatorController") as UnityEngine.Object;
                if (controller == null) return; // UI Playables still provide sprite changes without a controller.
                // Base layer only. Sprite observations remain per rendered frame; reflective state sampling is throttled.
                if (Time.frameCount % 6 != 0) return;
                var state = LayerCall(animator, "GetCurrentAnimatorStateInfo");
                var infos = (IEnumerable)LayerCall(animator, "GetCurrentAnimatorClipInfo");
                var names = new StringBuilder();
                foreach (var info in infos)
                {
                    var clip = Get(info, "clip") as UnityEngine.Object;
                    if (clip == null) continue;
                    names.Append(clip.name).Append('|');
                    CaptureClip(clip, controller.name);
                }
                int hash = (int)Get(state, "fullPathHash");
                string signature = hash + ":" + names;
                if (signature != actor.LastState)
                {
                    actor.LastState = signature;
                    Emit(Row("animationState").N("actor", actor.Id).N("stateHash", hash)
                        .N("shortNameHash", (int)Get(state, "shortNameHash")).S("clips", names.ToString())
                        .F("normalizedTime", (float)Get(state, "normalizedTime"))
                        .F("speed", (float)Get(animator, "speed")).B("transition", (bool)LayerCall(animator, "IsInTransition")));
                }
            }
            catch (Exception ex) { actor.StateFailed = true; Error("animator-state-" + actor.Id, ex); }
        }
        private void CaptureSprite(Sprite sprite)
        {
            _sprites.Add(sprite.GetInstanceID());
            var rect = sprite.rect;
            var pivot = sprite.pivot;
            var texture = sprite.texture;
            Emit(Row("sprite").N("spriteId", sprite.GetInstanceID()).S("name", sprite.name)
                .F("x", rect.x).F("y", rect.y).F("width", rect.width).F("height", rect.height)
                .F("pivotX", pivot.x).F("pivotY", pivot.y).F("ppu", sprite.pixelsPerUnit).B("packed", sprite.packed)
                .N("textureId", texture == null ? 0 : texture.GetInstanceID()).S("texture", texture?.name)
                .N("textureWidth", texture == null ? 0 : texture.width).N("textureHeight", texture == null ? 0 : texture.height));
            try
            {
                var tr = sprite.textureRect;
                Emit(Row("spriteTextureRect").N("spriteId", sprite.GetInstanceID())
                    .F("x", tr.x).F("y", tr.y).F("width", tr.width).F("height", tr.height));
            }
            catch (Exception ex) { Error("sprite-texture-rect", ex); }
        }
        private void VisitUI(GObject node, string path, int depth)
        {
            if (node == null || node.isDisposed || !node.visible || depth > 20) return;
            if (node is GLoader loader)
            {
                string url = loader.url;
                if (!string.IsNullOrEmpty(url) && (url.StartsWith("UT_Hero_", StringComparison.Ordinal)
                    || url.StartsWith("UT_AccountHeadShot_", StringComparison.Ordinal) || url.StartsWith("UT_Platform_", StringComparison.Ordinal)))
                {
                    string signature = _scene + ":" + path + ":" + url;
                    if (_uiRows.Add(signature))
                        Emit(Row("uiImage").S("path", path).S("parentType", node.parent?.GetType().FullName).S("url", url)
                            .F("x", node.x).F("y", node.y).F("width", node.width).F("height", node.height));
                }
            }
            if (node is UIOperationTime_Progress progress)
                Emit(Row("progressAvatar").S("path", path).S("url", progress.loader_Head?.url)
                    .F("x", progress.loader_Head?.x ?? 0).F("y", progress.loader_Head?.y ?? 0)
                    .F("value", (float)progress.value).F("max", (float)progress.max));
            if (node is UIBattleInfo_Com_MiniMap_Icon icon)
                Emit(Row("mapAvatar").S("path", path).S("url", icon.loader_Head?.url)
                    .F("x", icon.x).F("y", icon.y).N("slotStyle", icon.player?.selectedIndex ?? -1));
            if (node is GComponent component)
                for (int i = 0; i < component.numChildren; i++)
                {
                    var child = component.GetChildAt(i);
                    VisitUI(child, path + "/" + child.GetType().Name + "[" + i + "]:" + child.name, depth + 1);
                }
        }
        private static string TransformPath(Transform t)
        {
            string path = "";
            int depth = 0;
            while (t != null && depth++ < 20) { path = t.name + (path.Length == 0 ? "" : "/" + path); t = t.parent; }
            return path;
        }
        private void FlushAndStatus()
        {
            _log.Flush();
            var status = new JsonRow().S("version", Version).S("state", _running ? "recording" : "stopped")
                .N("utcTicks", DateTime.UtcNow.Ticks).S("scene", _scene).N("records", _log.Records)
                .N("characters", _log.Characters).N("dropped", _log.Dropped).N("actors", _actors.Count)
                .N("sprites", _sprites.Count).N("clips", _clips.Count).N("spriteChanges", _frameChanges).N("errors", _errors.Count);
            File.WriteAllText(Path.Combine(_sessionDir, "status.json"), status.ToString());
        }
        public override void OnUnload()
        {
            if (_log == null) return;
            _running = false;
            try { FlushAndStatus(); } catch { }
        }
    }
}
