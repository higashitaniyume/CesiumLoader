using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Engine;
using Core.Unit;
using FairyGUI;
using UnityEngine;

namespace PveWaveForecastMod
{
    internal sealed class WaveOverlay
    {
        private sealed class Marker
        {
            public int LandId;
            public GTextField Text;
            public int Stack;
        }

        private readonly List<Marker> _markers = new List<Marker>();
        private GComponent _layer;
        private GTextField _panel;
        private int _fontSize = 22;
        private bool _showPanel = true;
        private bool _showRandom = true;
        private int _labelLift = 36;
        private int _lastSignature;

        internal void Configure(int fontSize, bool showPanel, bool showRandom, int labelLift)
        {
            _fontSize = fontSize;
            _showPanel = showPanel;
            _showRandom = showRandom;
            _labelLift = labelLift;
        }

        internal void Clear()
        {
            for (int i = 0; i < _markers.Count; i++) _markers[i].Text.Dispose();
            _markers.Clear();
            if (_panel != null) { _panel.Dispose(); _panel = null; }
            if (_layer != null) { _layer.Dispose(); _layer = null; }
            _lastSignature = 0;
        }

        internal void Render(List<WaveInfo> waves, int progress, LandManager lands, Camera camera,
            float heightOffset, bool onlyNextWave)
        {
            if (waves == null || waves.Count == 0 || lands == null || !UnityObject.IsAlive(camera))
            {
                Clear();
                return;
            }

            EnsureLayer();
            _layer.SetSize(GRoot.inst.width, GRoot.inst.height);

            var selected = SelectWaves(waves, progress, onlyNextWave);
            int signature = Signature(selected, progress);
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                Rebuild(selected, progress, lands);
            }

            Position(lands, camera, heightOffset);
            UpdatePanel(waves, progress);
        }

        private static List<WaveInfo> SelectWaves(List<WaveInfo> waves, int progress, bool onlyNextWave)
        {
            var result = new List<WaveInfo>();
            if (onlyNextWave)
            {
                WaveInfo current = null;
                WaveInfo next = null;
                for (int i = 0; i < waves.Count; i++)
                {
                    var wave = waves[i];
                    if (wave.Progress <= progress) current = wave;
                    else if (next == null) next = wave;
                }
                if (current != null) result.Add(current);
                if (next != null) result.Add(next);
                if (result.Count == 0 && waves.Count > 0) result.Add(waves[waves.Count - 1]);
                return result;
            }

            for (int i = 0; i < waves.Count; i++) result.Add(waves[i]);
            return result;
        }

        private static int Signature(List<WaveInfo> waves, int progress)
        {
            int hash = progress * 397;
            for (int i = 0; i < waves.Count; i++)
            {
                hash = hash * 31 + waves[i].EventId;
                hash = hash * 31 + waves[i].LandIds.Count;
                hash = hash * 31 + (waves[i].ExactLands ? 1 : 0);
                hash = hash * 31 + waves[i].OrderedMonsterIds.Count;
                for (int j = 0; j < waves[i].LandIds.Count; j++) hash = hash * 31 + waves[i].LandIds[j];
            }
            return hash;
        }

        private void EnsureLayer()
        {
            if (_layer != null && _layer.parent == GRoot.inst) return;
            Clear();
            _layer = new GComponent { name = "PveWaveForecastMod", touchable = false, sortingOrder = 50 };
            GRoot.inst.AddChild(_layer);
        }

        private void Rebuild(List<WaveInfo> waves, int progress, LandManager lands)
        {
            for (int i = 0; i < _markers.Count; i++) _markers[i].Text.Dispose();
            _markers.Clear();

            var stacks = new Dictionary<int, int>();
            for (int w = 0; w < waves.Count; w++)
            {
                var wave = waves[w];
                bool past = wave.Progress < progress;
                for (int i = 0; i < wave.LandIds.Count; i++)
                {
                    int landId = wave.LandIds[i];
                    if (!lands.NodeDict.ContainsKey(landId)) continue;

                    int stack;
                    stacks.TryGetValue(landId, out stack);
                    stacks[landId] = stack + 1;

                    var text = new GTextField { touchable = false, autoSize = AutoSizeType.Both };
                    text.textFormat = new TextFormat
                    {
                        size = _fontSize,
                        bold = true,
                        color = ColorFor(wave.Category),
                        outline = 2f,
                        outlineColor = Color.black
                    };

                    string head = "R" + wave.Progress + " " + (string.IsNullOrEmpty(wave.Title) ? "波次" : wave.Title);
                    string body;
                    if (wave.ExactLands)
                    {
                        // One land = one assigned monster (or the few that really stack).
                        body = wave.MonsterLineForLand(landId);
                        if (string.IsNullOrEmpty(body)) continue;
                    }
                    else
                    {
                        // Never put the whole wave on every candidate land.
                        body = _showRandom ? "可能刷新（位置随机）" : "可能刷新";
                    }
                    if (past) text.alpha = 0.45f;
                    text.text = head + "\n" + body;

                    _layer.AddChild(text);
                    _markers.Add(new Marker { LandId = landId, Text = text, Stack = stack });
                }
            }
        }

        private void Position(LandManager lands, Camera camera, float heightOffset)
        {
            var transform = UnityObject.GetTransform(camera);
            var cameraPos = TransformService.GetPosition(transform);
            var forward = TransformService.GetForward(transform);

            for (int i = 0; i < _markers.Count; i++)
            {
                var marker = _markers[i];
                UnitLand land;
                if (!lands.NodeDict.TryGetValue(marker.LandId, out land) || !UnityObject.IsAlive(land))
                {
                    marker.Text.visible = false;
                    continue;
                }

                var world = new Vector3(land.X, land.Y + heightOffset, land.Z);
                if (Vector3.Dot(world - cameraPos, forward) <= 0.1f)
                {
                    marker.Text.visible = false;
                    continue;
                }

                var point = _layer.WorldToLocal(world, camera);
                if (float.IsNaN(point.x) || float.IsInfinity(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.y))
                {
                    marker.Text.visible = false;
                    continue;
                }

                marker.Text.visible = point.x >= 0f && point.x <= _layer.width && point.y >= 0f && point.y <= _layer.height;
                marker.Text.SetXY(
                    point.x - marker.Text.width * 0.5f,
                    point.y - marker.Text.height - 6f - _labelLift - marker.Stack * (_fontSize + 5f));
            }
        }

        private void UpdatePanel(List<WaveInfo> waves, int progress)
        {
            if (!_showPanel)
            {
                if (_panel != null) _panel.visible = false;
                return;
            }

            if (_panel == null)
            {
                _panel = new GTextField { touchable = false, autoSize = AutoSizeType.Height, width = 560f };
                _panel.textFormat = new TextFormat
                {
                    size = Math.Max(14, _fontSize - 4),
                    bold = true,
                    color = new Color(0.95f, 0.95f, 0.95f, 1f),
                    outline = 2f,
                    outlineColor = Color.black
                };
                _layer.AddChild(_panel);
            }

            _panel.visible = true;
            _panel.text = BuildPanelText(waves, progress);
            _panel.SetXY(18f, 18f);
        }

        private static string BuildPanelText(List<WaveInfo> waves, int progress)
        {
            var lines = new List<string>();
            lines.Add("PVE 波次预告  进度 " + progress);
            for (int i = 0; i < waves.Count; i++)
            {
                var wave = waves[i];
                string marker = wave.Progress == progress ? "→ " : "  ";
                string line = marker + "R" + wave.Progress + " " + (string.IsNullOrEmpty(wave.Title) ? "波次" : wave.Title);
                if (!wave.SpawnsMonsters) line += "（不刷新怪物）";
                if (!string.IsNullOrEmpty(wave.Description)) line += "\n    " + Shorten(wave.Description, 64);
                lines.Add(line);
            }
            return string.Join("\n", lines.ToArray());
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max) + "…";
        }

        private static Color ColorFor(WaveCategory category)
        {
            if (category == WaveCategory.Boss) return new Color(1f, 0.35f, 0.35f, 1f);
            if (category == WaveCategory.Elite) return new Color(0.8f, 0.5f, 1f, 1f);
            if (category == WaveCategory.Normal) return new Color(0.4f, 0.75f, 1f, 1f);
            return new Color(1f, 0.82f, 0.3f, 1f);
        }
    }
}
