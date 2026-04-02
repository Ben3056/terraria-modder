using System.Collections.Generic;

namespace TerrariaModder.Core.Manifest
{
    /// <summary>
    /// How a mod participates in multiplayer sessions.
    /// </summary>
    public enum MultiplayerCategory
    {
        /// <summary>Mod may be present or absent on either side — no enforcement.</summary>
        Optional,
        /// <summary>All connected clients must have this mod. Missing → connection blocked.</summary>
        Required,
        /// <summary>Client-only mod; not relevant on server side and never enforced.</summary>
        ClientOnly,
    }

    /// <summary>
    /// Represents the parsed mod.json manifest.
    /// </summary>
    public class ModManifest
    {
        // Required fields
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }

        // Optional version constraints
        public string TerrariaVersion { get; set; }
        public string FrameworkVersion { get; set; }

        // Optional fields
        public string EntryDll { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public List<string> OptionalDependencies { get; set; } = new List<string>();
        public List<string> IncompatibleWith { get; set; } = new List<string>();
        public List<string> LoadAfter { get; set; } = new List<string>();
        public List<string> LoadBefore { get; set; } = new List<string>();
        public List<KeybindDefinition> Keybinds { get; set; } = new List<KeybindDefinition>();
        public string Homepage { get; set; }
        public string Icon { get; set; }
        public List<string> Tags { get; set; } = new List<string>();

        // Multiplayer enforcement
        public MultiplayerCategory Multiplayer { get; set; } = MultiplayerCategory.Optional;

        /// <summary>
        /// True when the "multiplayer" key was explicitly present in manifest.json.
        /// Mods with custom items that do NOT set this are auto-upgraded to Required.
        /// Mods that explicitly declare "multiplayer": "optional" are truly optional —
        /// clients without them can connect and see placeholder items.
        /// </summary>
        public bool MultiplayerExplicit { get; set; }

        // Runtime properties (set by loader, not from JSON)
        public string FolderPath { get; set; }
        public string DllPath { get; set; }
        public bool IsValid { get; set; }
        public List<string> ValidationErrors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Keybind definition from manifest.
    /// </summary>
    public class KeybindDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string DefaultKey { get; set; }
    }
}
