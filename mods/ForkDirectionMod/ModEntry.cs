using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Engine;
using CesiumLoader.SDK.Events;
using Core.Scene;
using Core.Unit;
using FairyGUI;
using GameLogic;
using Tools;
using UnityEngine;

namespace ForkDirectionMod
{
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new ForkDirectionController()); }
    }

    public sealed class ForkDirectionController : ModBase
    {
        private sealed class Marker
        {
            public NeighborLabel Data;
            public GTextField Text;
            public int Stack;
        }
        private Dictionary<int, LandSnapshot> _snapshot = new Dictionary<int, LandSnapshot>();
        private readonly List<Marker> _markers = new List<Marker>();
        private GComponent _layer;
        private DateTime _nextScan, _nextError;
        private bool _enabled = true;
        private float _height = .8f;
        private int _fontSize = 24;
        public override string Name { get { return "岔路地块标记"; } }
        public override string Version { get { return "1.2.0"; } }

        public override void OnInitialize()
        {
            ReadConfig(isInitial: true);
            UpdateEvents.LateUpdate += Draw;
            Context?.RegisterCleanup(Stop);
            Log.Info("已启用岔路邻接地块标记：A 红色、B 黄色、C 蓝色，附带地块 ID。");
        }

        private void ReadConfig(bool isInitial = false)
        {
            if (Config != null)
            {
                if (isInitial)
                {
                    ForkDirectionConfigDocs.Apply(Config);
                    Config.SetDefault("enabled", true);
                    Config.SetDefault("heightOffset", .8f);
                    Config.SetDefault("fontSize", 24);
                }
                _enabled = Config.GetBool("enabled", true);
                _height = Clamp(Config.GetFloat("heightOffset", .8f), 0f, 10f, .8f);
                _fontSize = Math.Max(14, Math.Min(48, Config.GetInt("fontSize", 24)));
                if (isInitial) Config.SaveIfDirty();
            }
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            Clear();
            Log.Info("岔路标记配置已热重载: enabled=" + _enabled + ", heightOffset=" + _height + ", fontSize=" + _fontSize);
        }

        private static float Clamp(float x, float min, float max, float fallback)
        { return float.IsNaN(x) || float.IsInfinity(x) ? fallback : Math.Max(min, Math.Min(max, x)); }

        private bool TryBoard(out LandManager manager, out Camera camera)
        {
            manager = null; camera = null;
            if (!_enabled) return false;
            if (SimpleSingletonProvider<Core.Scene.SceneManager>.inst.currentType.Value != SceneType.Battle) return false;
            var room = SimpleSingletonProvider<GameLogicManager>.inst.room;
            if (room == null || room.curRoomInfo == null || !room.curRoomInfo.IsPVE()) return false;
            var battle = BattleSceneController.inst;
            if (battle == null) return false;
            camera = battle.mainCamera;
            manager = SimpleSingletonProvider<LandManager>.inst;
            return UnityObject.IsAlive(camera) && manager != null && manager.NodeDict != null && manager.NodeDict.Count > 0;
        }

        private Dictionary<int, LandSnapshot> Capture(LandManager manager)
        {
            var result = new Dictionary<int, LandSnapshot>();
            foreach (var pair in manager.NodeDict)
            {
                var land = pair.Value;
                if (!UnityObject.IsAlive(land)) continue;
                var position = TransformService.GetPosition(UnityObject.GetTransform(land));
                var raw = land.GetNodeInfo().neighborLandIds;
                var ids = new int[raw.Count];
                for (int i=0;i<ids.Length;i++) ids[i]=raw[i];
                result[pair.Key] = new LandSnapshot { Id=pair.Key, Disabled=land.Disable, X=position.x, Y=position.y, Z=position.z, Neighbors=ids };
            }
            return result;
        }

        private void EnsureLayer()
        {
            if (_layer != null && _layer.parent == GRoot.inst) return;
            Clear();
            _layer = new GComponent { name="ForkDirectionMod", touchable=false, sortingOrder=50 };
            GRoot.inst.AddChild(_layer);
        }

        private void Rebuild(Dictionary<int, LandSnapshot> snapshot)
        {
            ClearMarkers();
            _snapshot = snapshot;
            var labels = NeighborLabels.Build(snapshot);
            var occurrences = new Dictionary<int,int>();
            foreach (var data in labels)
            {
                int count;
                occurrences.TryGetValue(data.Land.Id,out count);
                occurrences[data.Land.Id]=count+1;
            }
            var stacks = new Dictionary<int,int>();
            foreach (var data in labels)
            {
                int stack;
                stacks.TryGetValue(data.Land.Id,out stack);
                stacks[data.Land.Id]=stack+1;
                var color = data.Rank==0 ? new Color(1f,.27f,.27f,1f) :
                    data.Rank==1 ? new Color(1f,.85f,.15f,1f) : new Color(.27f,.61f,1f,1f);
                var text = new GTextField { touchable=false, autoSize=AutoSizeType.Both };
                text.textFormat = new TextFormat { size=_fontSize, bold=true, color=color, outline=2f, outlineColor=Color.black };
                // A land shared by junctions gets separate rows with junction IDs.
                text.text = data.Letter + "  " + data.Land.Id +
                    (occurrences[data.Land.Id]>1 ? " ("+data.Junction.Id+")" : "");
                _layer.AddChild(text);
                _markers.Add(new Marker { Data=data, Text=text, Stack=stack });
            }
            Log.Info("已刷新岔路地块标记，标签数=" + _markers.Count);
        }

        private void Draw()
        {
            try
            {
                LandManager manager; Camera camera;
                if (!TryBoard(out manager,out camera)) { Clear(); return; }
                EnsureLayer();
                _layer.SetSize(GRoot.inst.width,GRoot.inst.height);
                if(DateTime.UtcNow>=_nextScan)
                {
                    _nextScan=DateTime.UtcNow.AddMilliseconds(500);
                    var snapshot=Capture(manager);
                    if(!RoutePlanner.Same(_snapshot,snapshot)) Rebuild(snapshot);
                }
                var transform=UnityObject.GetTransform(camera);
                var cameraPos=TransformService.GetPosition(transform);
                var forward=TransformService.GetForward(transform);
                foreach(var marker in _markers)
                {
                    var land=marker.Data.Land;
                    var world=new Vector3(land.X,land.Y+_height,land.Z);
                    if(Vector3.Dot(world-cameraPos,forward)<=.1f) { marker.Text.visible=false; continue; }
                    var point=_layer.WorldToLocal(world,camera);
                    if(float.IsNaN(point.x)||float.IsInfinity(point.x)||float.IsNaN(point.y)||float.IsInfinity(point.y))
                    { marker.Text.visible=false; continue; }
                    marker.Text.visible=point.x>=0f&&point.x<=_layer.width&&point.y>=0f&&point.y<=_layer.height;
                    marker.Text.SetXY(point.x-marker.Text.width*.5f,point.y-marker.Text.height-6f-marker.Stack*(_fontSize+5f));
                }
            }
            catch(Exception e)
            {
                Clear();
                if(DateTime.UtcNow>=_nextError)
                { _nextError=DateTime.UtcNow.AddSeconds(15); Log.Warn("岔路地块标记暂不可用："+e.Message); }
            }
        }
        private void ClearMarkers()
        {
            foreach(var marker in _markers) marker.Text.Dispose();
            _markers.Clear();
        }
        private void Clear()
        {
            ClearMarkers();
            if(_layer!=null) _layer.Dispose();
            _layer=null;
            _snapshot.Clear(); _nextScan=DateTime.MinValue;
        }
        private void Stop() { UpdateEvents.LateUpdate-=Draw; Clear(); }
        public override void OnUnload() { Stop(); }
    }
}
