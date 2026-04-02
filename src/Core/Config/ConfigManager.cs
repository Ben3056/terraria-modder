using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Loads, saves, and manages ModConfig instances.
    /// Central serialization engine for the class-based config system.
    /// </summary>
    public static class ConfigManager
    {
        /// <summary>
        /// True when this process is a dedicated server (TerrariaServer.exe).
        /// Checks if running as dedicated server via environment variable.
        /// </summary>
        public static bool IsServerProcess()
        {
            return Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";
        }

        /// <summary>
        /// The config scope appropriate for this process (Server or Client).
        /// </summary>
        public static ConfigScope GetCurrentScope()
            => IsServerProcess() ? ConfigScope.Server : ConfigScope.Client;

        /// <summary>
        /// Returns the scoped config file path for this process:
        /// {modId}.server.json on a dedicated server, {modId}.client.json otherwise.
        /// </summary>
        private static string GetScopedFilePath(string configsFolder, string modId)
        {
            string suffix = IsServerProcess() ? ".server.json" : ".client.json";
            return Path.Combine(configsFolder, modId + suffix);
        }

        /// <summary>
        /// Find a ModConfig subclass in the given assembly, instantiate it,
        /// load values from disk, and return it ready for use.
        /// Returns null if the assembly has no ModConfig subclass.
        /// </summary>
        public static ModConfig LoadForMod(string modId, Assembly assembly, string configsFolder, string oldModFolder, ILogger log)
        {
            // Find ModConfig subclass
            Type configType = null;
            try
            {
                var types = assembly.GetTypes();
                configType = types.FirstOrDefault(t =>
                    t.IsClass && !t.IsAbstract &&
                    typeof(ModConfig).IsAssignableFrom(t));
            }
            catch (ReflectionTypeLoadException rtle)
            {
                // Use what we could load
                foreach (var t in rtle.Types)
                {
                    if (t != null && t.IsClass && !t.IsAbstract && typeof(ModConfig).IsAssignableFrom(t))
                    {
                        configType = t;
                        break;
                    }
                }
            }

            if (configType == null) return null;

            // Instantiate
            ModConfig config;
            try
            {
                config = (ModConfig)Activator.CreateInstance(configType);
            }
            catch (Exception ex)
            {
                log?.Warn($"[{modId}] Failed to create config instance: {ex.Message}");
                return null;
            }

            // Set framework internals — use scoped file path
            string scopedPath = GetScopedFilePath(configsFolder, modId);
            config.FilePath = scopedPath;
            config.ModId = modId;
            config.Log = log;

            // Ensure configs folder exists
            try
            {
                if (!Directory.Exists(configsFolder))
                    Directory.CreateDirectory(configsFolder);
            }
            catch (Exception ex)
            {
                log?.Warn($"[{modId}] Could not create configs folder: {ex.Message}");
            }

            // Load from disk (with backward compat migration from old locations)
            LoadFromDisk(config, oldModFolder);

            // Warn about untagged properties (console only)
            WarnUntaggedProperties(config, log);

            // Snapshot baseline for restart-required tracking
            config.SnapshotBaseline();

            return config;
        }

        /// <summary>
        /// Load config values from disk into config object.
        /// Migration priority (highest to lowest):
        ///   1. Scoped file ({modId}.server.json or {modId}.client.json) — current format
        ///   2. Unified file ({modId}.json) — pre-Phase3 format, migrates on first read
        ///   3. Old per-mod file (mods/{mod-id}/config.json) — legacy, migrates on first read
        /// </summary>
        internal static void LoadFromDisk(ModConfig config, string oldModFolder = null)
        {
            string scopedPath = config.FilePath; // Already set to scoped path by LoadForMod
            bool scopedExists = File.Exists(scopedPath);

            if (scopedExists)
            {
                // Normal load from scoped file
                try
                {
                    string json = File.ReadAllText(scopedPath);
                    ApplyJsonToConfig(config, json);
                }
                catch (Exception ex)
                {
                    config.Log?.Warn($"[{config.ModId}] Failed to load config, using defaults: {ex.Message}");
                }
                return;
            }

            // Scoped file doesn't exist — check for unified file to migrate from
            string configsFolder = Path.GetDirectoryName(scopedPath);
            string unifiedPath = Path.Combine(configsFolder, config.ModId + ".json");
            if (File.Exists(unifiedPath))
            {
                MigrateFromUnifiedFile(config, unifiedPath);
                return;
            }

            // Neither scoped nor unified exist — check legacy per-mod location
            if (oldModFolder != null)
            {
                string legacyPath = Path.Combine(oldModFolder, "config.json");
                if (File.Exists(legacyPath))
                {
                    MigrateFromOldLocation(config, legacyPath);
                    return;
                }
            }

            // No file at all — save defaults to scoped file
            Save(config);
        }

        /// <summary>
        /// Migrate from pre-Phase3 unified {modId}.json to scoped file.
        /// Reads all values from unified file, saves scope-appropriate ones to scoped file,
        /// then deletes the unified file.
        /// </summary>
        private static void MigrateFromUnifiedFile(ModConfig config, string unifiedPath)
        {
            try
            {
                string json = File.ReadAllText(unifiedPath);
                ApplyJsonToConfig(config, json); // Apply all values into memory
                config.Log?.Info($"[{config.ModId}] Migrating unified config → {config.FilePath}");
                Save(config); // Writes only scope-appropriate properties to scoped file

                // Delete unified file (best-effort; concurrent migration from server is fine)
                try { File.Delete(unifiedPath); }
                catch { /* other process may have deleted it already */ }
            }
            catch (Exception ex)
            {
                config.Log?.Warn($"[{config.ModId}] Failed to migrate from {unifiedPath}: {ex.Message}");
                Save(config); // Save defaults to scoped file
            }
        }

        /// <summary>
        /// One-time migration from old mods/{mod-id}/config.json location.
        /// Reads old file with case-insensitive key matching to handle camelCase -> PascalCase.
        /// </summary>
        private static void MigrateFromOldLocation(ModConfig config, string oldPath)
        {
            try
            {
                string json = File.ReadAllText(oldPath);
                var raw = ParseJsonRaw(json);

                // Map old camelCase keys to PascalCase property names
                foreach (var meta in config.GetPropertyMetadata())
                {
                    // Try exact match first, then case-insensitive
                    if (TryGetRawValue(raw, meta.Key, out object val) ||
                        TryGetRawValueCaseInsensitive(raw, meta.Key, out val))
                    {
                        try { meta.SetValue(config, val); }
                        catch { }
                    }

                    // Also try former names
                    foreach (var former in meta.FormerNames)
                    {
                        if (TryGetRawValue(raw, former, out val) ||
                            TryGetRawValueCaseInsensitive(raw, former, out val))
                        {
                            try { meta.SetValue(config, val); }
                            catch { }
                            break;
                        }
                    }
                }

                Save(config); // Write to new location
                try { File.Delete(oldPath); } catch { }
                config.Log?.Info($"[{config.ModId}] Migrated config from {oldPath} → {config.FilePath} (old file deleted)");
            }
            catch (Exception ex)
            {
                config.Log?.Warn($"[{config.ModId}] Failed to migrate old config from {oldPath}: {ex.Message}");
                Save(config); // Save defaults to new location
            }
        }

        /// <summary>
        /// Parse JSON and apply values to config properties.
        /// Handles version check and migration.
        /// </summary>
        private static void ApplyJsonToConfig(ModConfig config, string json)
        {
            var raw = ParseJsonRaw(json);

            // Read stored version
            int storedVersion = 0;
            if (TryGetRawValue(raw, "_version", out object vObj))
            {
                try { storedVersion = Convert.ToInt32(vObj); }
                catch { }
            }

            // Apply raw values to properties (before migration so Migrate() has current values)
            ApplyRawToProperties(config, raw);

            // Run migration if needed
            if (storedVersion < config.Version)
            {
                config.Log?.Debug($"[{config.ModId}] Migrating config v{storedVersion} → v{config.Version}");
                config.ApplyMigration(raw, storedVersion);
                Save(config); // Resave with new version
            }
        }

        /// <summary>
        /// Apply raw JSON values to config properties by name.
        /// </summary>
        private static void ApplyRawToProperties(ModConfig config, Dictionary<string, object> raw)
        {
            foreach (var meta in config.GetPropertyMetadata())
            {
                // Try property name, then former names
                object val;
                bool found = TryGetRawValue(raw, meta.Key, out val);

                if (!found)
                {
                    foreach (var former in meta.FormerNames)
                    {
                        if (TryGetRawValue(raw, former, out val))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (found)
                {
                    try { meta.SetValue(config, val); }
                    catch (Exception ex)
                    {
                        config.Log?.Debug($"[{config.ModId}] Failed to set {meta.Key}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Save config property values to the scoped JSON file.
        /// Only writes properties matching the current process scope (Client or Server).
        /// </summary>
        public static void Save(ModConfig config)
        {
            try
            {
                var scope = GetCurrentScope();

                // Only persist properties belonging to this process's scope
                var metadata = config.GetPropertyMetadata()
                    .Where(m => m.Scope == scope)
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"_version\": {config.Version},");

                for (int i = 0; i < metadata.Count; i++)
                {
                    var meta = metadata[i];
                    object value = meta.GetValue(config);
                    string jsonValue = SerializeValue(value);
                    string comma = i < metadata.Count - 1 ? "," : "";
                    sb.AppendLine($"  \"{meta.Key}\": {jsonValue}{comma}");
                }

                sb.AppendLine("}");

                var dir = Path.GetDirectoryName(config.FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Atomic write: tmp → backup → rename (crash-safe)
                string tmpPath = config.FilePath + ".tmp";
                File.WriteAllText(tmpPath, sb.ToString());

                if (File.Exists(config.FilePath))
                {
                    try { File.Copy(config.FilePath, config.FilePath + ".bak", true); }
                    catch { /* backup is best-effort */ }
                    File.Delete(config.FilePath);
                }

                File.Move(tmpPath, config.FilePath);
                config.Log?.Debug($"[{config.ModId}] Config saved: {config.FilePath}");
            }
            catch (Exception ex)
            {
                config.Log?.Error($"[{config.ModId}] Failed to save config: {ex.Message}");
            }
        }

        /// <summary>
        /// Reload property values from disk.
        /// </summary>
        public static void Reload(ModConfig config)
        {
            try
            {
                if (File.Exists(config.FilePath))
                {
                    string json = File.ReadAllText(config.FilePath);
                    ApplyJsonToConfig(config, json);
                    config.Log?.Debug($"[{config.ModId}] Config reloaded from {config.FilePath}");
                }
            }
            catch (Exception ex)
            {
                config.Log?.Error($"[{config.ModId}] Failed to reload config: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset all properties to their constructor-defined defaults, then save.
        /// </summary>
        public static void ResetToDefaults(ModConfig config)
        {
            try
            {
                // Create a fresh instance to get default values
                var fresh = (ModConfig)Activator.CreateInstance(config.GetType());

                // Copy defaults from fresh instance to current
                foreach (var meta in config.GetPropertyMetadata())
                {
                    object defaultVal = meta.Property.GetValue(fresh);
                    meta.Property.SetValue(config, defaultVal);
                }

                Save(config);
            }
            catch (Exception ex)
            {
                config.Log?.Error($"[{config.ModId}] Failed to reset config: {ex.Message}");
            }
        }

        // ---- JSON parsing ----

        /// <summary>
        /// Parse a flat JSON object into a raw string->object dictionary.
        /// Supports bool, int, double, string, and null.
        /// </summary>
        public static Dictionary<string, object> ParseJsonRaw(string json)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return result;

            // Match key-value pairs. Handles: "key": value
            // Value can be: true/false, null, number, "string"
            int i = 0;
            int len = json.Length;

            while (i < len)
            {
                // Find next key (quoted string)
                i = SkipToChar(json, i, '"');
                if (i >= len) break;
                i++; // skip opening quote

                string key = ReadString(json, ref i);

                // Skip whitespace and colon
                i = SkipWhitespace(json, i);
                if (i >= len || json[i] != ':') continue;
                i++; // skip ':'
                i = SkipWhitespace(json, i);
                if (i >= len) break;

                object value = ReadValue(json, ref i);
                result[key] = value;
            }

            return result;
        }

        private static int SkipToChar(string s, int i, char c)
        {
            while (i < s.Length && s[i] != c) i++;
            return i;
        }

        private static int SkipWhitespace(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        private static string ReadString(string s, ref int i)
        {
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char esc = s[i++];
                    if (esc == '"') sb.Append('"');
                    else if (esc == '\\') sb.Append('\\');
                    else if (esc == 'n') sb.Append('\n');
                    else if (esc == 't') sb.Append('\t');
                    else sb.Append(esc);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static object ReadValue(string s, ref int i)
        {
            if (i >= s.Length) return null;
            char c = s[i];

            // String
            if (c == '"')
            {
                i++; // skip opening quote
                return ReadString(s, ref i);
            }

            // Boolean / null
            if (i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }

            // Number
            int start = i;
            if (c == '-' || char.IsDigit(c))
            {
                if (c == '-') i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                bool isFloat = false;
                if (i < s.Length && s[i] == '.') { isFloat = true; i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E')) { isFloat = true; i++; if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++; while (i < s.Length && char.IsDigit(s[i])) i++; }

                string numStr = s.Substring(start, i - start);
                if (isFloat) { if (double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) return d; }
                else { if (int.TryParse(numStr, out int iv)) return iv; if (long.TryParse(numStr, out long lv)) return lv; }
            }

            // Skip unknown token
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']') i++;
            return null;
        }

        private static bool TryGetRawValue(Dictionary<string, object> raw, string key, out object value)
        {
            return raw.TryGetValue(key, out value);
        }

        private static bool TryGetRawValueCaseInsensitive(Dictionary<string, object> raw, string key, out object value)
        {
            foreach (var kvp in raw)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        // ---- Serialization ----

        private static string SerializeValue(object value)
        {
            if (value == null) return "null";
            if (value is bool b) return b ? "true" : "false";
            if (value is int iv) return iv.ToString();
            if (value is double d) return d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            if (value is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return "\"" + value.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // ---- Diagnostics ----

        private static void WarnUntaggedProperties(ModConfig config, ILogger log)
        {
            foreach (var meta in config.GetPropertyMetadata())
            {
                var prop = meta.Property;
                bool hasServer = prop.GetCustomAttribute<ServerAttribute>() != null;
                bool hasClient = prop.GetCustomAttribute<ClientAttribute>() != null;

                if (!hasServer && !hasClient)
                {
                    log?.Info($"[{config.ModId}] Config property '{prop.Name}' has no [Client] or [Server] tag — defaulting to Client");
                }
            }
        }
    }
}
