using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Mods;
using Core;
using Core.Unit;
using GameLogic;
using Tools;
using UI;
using UnityEngine;

namespace FacingHighlightMod
{
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new FacingOverlay()); }
    }

    public sealed class FacingOverlay : ModBase
    {
        private const float PollInterval = 0.12f;
        private const float HighlightScale = 2.4f;
        private static readonly Color HighlightColor = new Color(1f, 0.92f, 0.08f, 1f);
        private float _nextPoll;
        private string _walkEffectName;
        private bool _loggedEffectName;
        private bool _loggedHighlight;

        public override string Name { get { return "单位朝向高亮"; } }
        public override string Version { get { return "2.2.5"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description { get { return "高亮游戏原有的方向箭头，不创建额外箭头"; } }

        public override void OnInitialize()
        {
            try
            {
                var config = 37.GetEffectDataConfigure();
                _walkEffectName = config != null ? config.EffectName : null;
                _loggedEffectName = true;
                Log.Info("朝向高亮已启用: 仅增强原有 Effect 37，资源名=" + (_walkEffectName ?? "未找到"));
            }
            catch (Exception e) { Log.Warn("读取原生方向特效配置失败: " + e.Message); }
        }

        public override void OnUpdate()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollInterval;
            ApplyHighlight();
        }

        private void ApplyHighlight()
        {
            try
            {
                if (string.IsNullOrEmpty(_walkEffectName))
                {
                    if (!_loggedEffectName)
                    {
                        _loggedEffectName = true;
                        var config = 37.GetEffectDataConfigure();
                        _walkEffectName = config != null ? config.EffectName : null;
                        Log.Info("原生方向特效资源名: " + (_walkEffectName ?? "未找到"));
                    }
                    if (string.IsNullOrEmpty(_walkEffectName)) return;
                }

                var players = Players.All();
                if (players == null) return;
                int highlighted = 0;
                for (int i = 0; i < players.Count; i++)
                {
                    var data = players[i];
                    var character = data != null ? data.CharacterInst : null;
                    if (data == null || data.player == null || character == null || character.EffectContainer == null ||
                        !character.gameObject.activeInHierarchy) continue;

                    var effects = character.EffectContainer.GetComponentsInChildren<Effect>(true);
                    for (int j = 0; j < effects.Length; j++)
                    {
                        var effect = effects[j];
                        if (effect == null || !effect.IsActive || effect.effectPoolKey != _walkEffectName) continue;
                        effect.SetColor(HighlightColor);
                        float pulse = 1f + 0.18f * (float)Math.Sin(Time.unscaledTime * 4.5f);
                        effect.transform.localScale = Vector3.one * (HighlightScale * pulse);
                        highlighted++;
                    }
                }

                if (!_loggedHighlight && highlighted > 0)
                {
                    _loggedHighlight = true;
                    Log.Info("已增强游戏原有方向箭头: " + highlighted + " 个; 未创建额外箭头");
                }
            }
            catch (Exception e) { Log.Warn("增强原有方向箭头失败: " + e.Message); }
        }
    }
}
