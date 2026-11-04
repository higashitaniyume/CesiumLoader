using System;
using System.Collections.Generic;
using Core.Unit;
using GameLogic;
using Tools;
using UnityEngine;

namespace CharacterTransparencyMod
{
    /// <summary>
    /// 角色半透明化应用器。
    /// 负责将场景/战斗中的角色精灵透明度调整为目标值，并在关闭时精准恢复初始透明度。
    /// </summary>
    internal sealed class CharacterTransparencyApplier
    {
        private readonly Dictionary<SpriteRenderer, float> _originalAlphas = new Dictionary<SpriteRenderer, float>();
        private readonly HashSet<Character> _modifiedCharacters = new HashSet<Character>();
        private readonly List<SpriteRenderer> _trackedRenderers = new List<SpriteRenderer>();

        private float _nextFullScanTime;
        private const float FullScanInterval = 0.5f;

        public bool HasActiveModifications
        {
            get { return _originalAlphas.Count > 0 || _modifiedCharacters.Count > 0; }
        }

        /// <summary>
        /// 应用半透明（在 Update / LateUpdate 中持续调用以维持状态并捕获新生成的角色）。
        /// </summary>
        /// <param name="targetAlpha">目标不透明度 (0.0 - 1.0)。</param>
        public void Apply(float targetAlpha)
        {
            float now = Time.unscaledTime;

            // 1. 战斗内的玩家与怪物角色（通过战斗数据直接遍历，无层级遍历开销）
            try
            {
                var battle = SimpleSingletonProvider<GameLogicManager>.inst?.battle;
                if (battle != null && battle.PlayerDatas != null)
                {
                    var pds = battle.PlayerDatas;
                    for (int i = 0; i < pds.Count; i++)
                    {
                        var pd = pds[i];
                        var ch = pd != null ? pd.CharacterInst : null;
                        if (ch != null && ch.gameObject != null && ch.gameObject.activeInHierarchy)
                        {
                            ApplyToCharacter(ch, targetAlpha);
                        }
                    }
                }
            }
            catch { }

            // 2. 周期性全局扫描（涵盖房间大厅、选人界面等非战斗场景或动态生成的角色）
            if (now >= _nextFullScanTime)
            {
                _nextFullScanTime = now + FullScanInterval;
                try
                {
                    var animators = UnityEngine.Object.FindObjectsOfType<CharacterAnimator>();
                    if (animators != null)
                    {
                        for (int i = 0; i < animators.Length; i++)
                        {
                            var anim = animators[i];
                            if (anim == null || anim.gameObject == null || !anim.gameObject.activeInHierarchy) continue;
                            if (anim.owner != null)
                            {
                                ApplyToCharacter(anim.owner, targetAlpha);
                            }
                            else
                            {
                                ApplyToAnimator(anim, targetAlpha);
                            }
                        }
                    }
                }
                catch { }
            }

            // 3. 保持已跟踪的渲染器 alpha 为目标值（应对动画或帧刷新重置 color 的情况）
            MaintainTrackedAlphas(targetAlpha);
        }

        /// <summary>
        /// 仅维持已跟踪渲染器的透明度（用于 LateUpdate 避免重复全量遍历与 GetComponentsInChildren 产生 GC 压力）。
        /// </summary>
        public void MaintainTrackedAlphas(float targetAlpha)
        {
            for (int i = _trackedRenderers.Count - 1; i >= 0; i--)
            {
                var sr = _trackedRenderers[i];
                if (sr == null || sr.gameObject == null)
                {
                    _trackedRenderers.RemoveAt(i);
                    continue;
                }
                var c = sr.color;
                if (!Mathf.Approximately(c.a, targetAlpha))
                {
                    sr.color = new Color(c.r, c.g, c.b, targetAlpha);
                }
            }
        }

        private void ApplyToCharacter(Character ch, float targetAlpha, bool force = false)
        {
            if (ch == null || ch.gameObject == null) return;

            bool isNew = _modifiedCharacters.Add(ch);
            if (!isNew && !force) return;

            // ★ 关键顺序：必须在执行任何修改前，先记录原本的真实 alpha！
            var srs = ch.GetComponentsInChildren<SpriteRenderer>(true);
            if (srs != null)
            {
                for (int i = 0; i < srs.Length; i++)
                {
                    RecordOriginalAlpha(srs[i], targetAlpha);
                    SetAlpha(srs[i], targetAlpha);
                }
            }

            // 记录并设置 Character 内部机制（支持修女/邦妮等原生隐身技能）
            try
            {
                ch.SetSpriteAlpha(Character.AlphaSourceType.None, targetAlpha);
            }
            catch { }
        }

        private void ApplyToAnimator(CharacterAnimator anim, float targetAlpha)
        {
            if (anim == null || anim.gameObject == null) return;
            var srs = anim.GetComponentsInChildren<SpriteRenderer>(true);
            if (srs == null) return;
            for (int i = 0; i < srs.Length; i++)
            {
                RecordOriginalAlpha(srs[i], targetAlpha);
                SetAlpha(srs[i], targetAlpha);
            }
        }

        private void RecordOriginalAlpha(SpriteRenderer sr, float targetAlpha)
        {
            if (sr == null || sr.gameObject == null) return;
            if (!_originalAlphas.ContainsKey(sr))
            {
                float currentAlpha = sr.color.a;
                // 保护机制：如果当前透明度已经是处于 targetAlpha 状态（例如残留未恢复），
                // 则回退到默认 1.0f，绝不能把 0.3 误当成原始值保存！
                float original = (Mathf.Approximately(currentAlpha, targetAlpha) || currentAlpha <= targetAlpha + 0.05f)
                    ? 1.0f
                    : currentAlpha;

                _originalAlphas[sr] = original;
                _trackedRenderers.Add(sr);
            }
        }

        private static void SetAlpha(SpriteRenderer sr, float targetAlpha)
        {
            if (sr == null || sr.gameObject == null) return;
            var c = sr.color;
            if (!Mathf.Approximately(c.a, targetAlpha))
            {
                sr.color = new Color(c.r, c.g, c.b, targetAlpha);
            }
        }

        /// <summary>
        /// 恢复所有角色的初始透明度。
        /// </summary>
        public void Restore()
        {
            _nextFullScanTime = 0f;

            // 1. 恢复 Character 的原生 alpha（移除 None 来源，触发游戏重新计算）
            foreach (var ch in _modifiedCharacters)
            {
                if (ch != null && ch.gameObject != null)
                {
                    try
                    {
                        ch.RemoveSpriteAlpha(Character.AlphaSourceType.None);
                        ch.UpdateSpriteAlpha();
                    }
                    catch { }
                }
            }
            _modifiedCharacters.Clear();

            // 2. 兜底清除当前战斗中所有角色可能残留的 None 来源
            try
            {
                var battle = SimpleSingletonProvider<GameLogicManager>.inst?.battle;
                if (battle != null && battle.PlayerDatas != null)
                {
                    for (int i = 0; i < battle.PlayerDatas.Count; i++)
                    {
                        var ch = battle.PlayerDatas[i]?.CharacterInst;
                        if (ch != null && ch.gameObject != null)
                        {
                            try
                            {
                                ch.RemoveSpriteAlpha(Character.AlphaSourceType.None);
                                ch.UpdateSpriteAlpha();
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // 3. 恢复所有单独跟踪的 SpriteRenderer
            foreach (var pair in _originalAlphas)
            {
                var sr = pair.Key;
                if (sr != null && sr.gameObject != null)
                {
                    try
                    {
                        var c = sr.color;
                        float restoreAlpha = pair.Value > 0.05f ? pair.Value : 1.0f;
                        sr.color = new Color(c.r, c.g, c.b, restoreAlpha);
                    }
                    catch { }
                }
            }
            _originalAlphas.Clear();
            _trackedRenderers.Clear();

            // 4. 兜底同步：遍历场景中的 CharacterAnimator，确保所有关联 Character 都移除 None 来源并重算原生透明度
            try
            {
                var animators = UnityEngine.Object.FindObjectsOfType<CharacterAnimator>();
                if (animators != null)
                {
                    for (int i = 0; i < animators.Length; i++)
                    {
                        var anim = animators[i];
                        if (anim != null && anim.owner != null)
                        {
                            try
                            {
                                anim.owner.RemoveSpriteAlpha(Character.AlphaSourceType.None);
                                anim.owner.UpdateSpriteAlpha();
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
