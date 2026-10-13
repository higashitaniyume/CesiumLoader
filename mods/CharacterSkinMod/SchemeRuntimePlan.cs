#nullable disable
using System;
using System.Collections.Generic;
using CesiumLoader.CharacterSkins;

namespace CharacterSkinMod
{
    // Pure planning: source data is never modified, including A->B / B->C chains.
    internal sealed class SchemeRuntimeTarget
    {
        public int HeroId;
        public CharacterSkinBinding Binding;
        public SkinStandingPaintingConfigureItem Original;
        public SkinStandingPaintingConfigureItem Applied;
        public SkinStandingPaintingConfigureItem Source;
    }
    internal static class SchemeRuntimePlan
    {
        internal static readonly string[] VisualProperties = { "LandIcon", "Character", "SfwCharacter", "InGameCharacter", "InGameCharacterSFW", "CharacterLevelUp", "ProfilePhoto", "CharacterReady", "SfwCharacterReady", "CharacterThin", "SfwCharacterThin", "RolePhoto", "SfwRolePhoto" };
        public static List<SkinStandingPaintingConfigureItem> FindSkinCopies(SkinConfigure config, int groupId, int itemId)
        {
            var result = new List<SkinStandingPaintingConfigureItem>();
            if (config.StandingPaintingDict.TryGetValue(groupId, out var mapped))
                foreach (var skin in mapped.SkinStandingPaintingConfigureItems) if (skin.ItemID == itemId) result.Add(skin);
            foreach (var group in config.StandingPaintings) if (group.Id == groupId)
                foreach (var skin in group.SkinStandingPaintingConfigureItems) if (skin.ItemID == itemId && !result.Exists(x => ReferenceEquals(x, skin))) result.Add(skin);
            return result;
        }
        public static List<SchemeRuntimeTarget> Build(CharacterSkinScheme scheme, Dictionary<int, List<SkinStandingPaintingConfigureItem>> heroes)
        {
            CharacterSkinSchemeRules.Validate(scheme);
            foreach (var rule in scheme.Rules)
            {
                if (!heroes.TryGetValue(rule.HeroId, out var skins)) throw new InvalidOperationException("方案角色不存在: " + rule.HeroId);
                foreach (int id in new[] { rule.TargetSkinItemId, rule.OfficialSourceSkinItemId })
                    if (id != 0 && !skins.Exists(x => x.ItemID == id)) throw new InvalidOperationException("官方皮肤不属于规则角色: " + id);
            }
            var result = new List<SchemeRuntimeTarget>();
            foreach (var hero in heroes)
                foreach (var original in hero.Value)
                {
                    var rule = CharacterSkinSchemeRules.Resolve(scheme, hero.Key, original.ItemID);
                    if (rule == null) continue;
                    var applied = original.Clone();
                    SkinStandingPaintingConfigureItem source = null;
                    if (rule.OfficialSourceSkinItemId != 0)
                    {
                        source = hero.Value.Find(x => x.ItemID == rule.OfficialSourceSkinItemId).Clone();
                        foreach (string property in VisualProperties)
                        {
                            var info = typeof(SkinStandingPaintingConfigureItem).GetProperty(property);
                            info.SetValue(applied, info.GetValue(source, null), null);
                        }
                        applied.Bust.Clear(); applied.Bust.Add(source.Bust);
                        applied.SfwBust.Clear(); applied.SfwBust.Add(source.SfwBust);
                        applied.SkinScale.Clear(); applied.SkinScale.Add(source.SkinScale);
                        applied.Actions.Clear(); applied.Actions.Add(source.Actions);
                    }
                    result.Add(new SchemeRuntimeTarget { HeroId = hero.Key, Binding = rule, Original = original, Applied = applied, Source = source });
                }
            return result;
        }
    }
    internal sealed class SchemeConflictRegistry
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        public void Add(string key, string value)
        {
            if (values.TryGetValue(key, out var old) && !string.Equals(old, value, StringComparison.Ordinal)) throw new InvalidOperationException("方案共享字段冲突: " + key);
            values[key] = value;
        }
    }
}
