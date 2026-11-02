using System;
using System.Collections.Generic;

namespace AudioSkinMod
{
    public sealed class AudioSchemeRule
    {
        public int ConfigId { get; set; }
        public string EventName { get; set; } = string.Empty;
        public string Action { get; set; } = "replace"; // replace | mute | redirect
        public string File { get; set; } = string.Empty;
        public float Volume { get; set; } = 1.0f;
        public int RedirectId { get; set; }
    }

    public sealed class AudioSchemeManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<AudioSchemeRule> Rules { get; set; } = new List<AudioSchemeRule>();
    }
}
