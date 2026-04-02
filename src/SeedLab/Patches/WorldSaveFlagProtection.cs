using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TerrariaModder.Core.Logging;

namespace SeedLab.Patches
{
    /// <summary>
    /// Protects world seed flags from being permanently stripped during auto-save.
    ///
    /// Problem: When SeedLab disables all features for a seed, RecalculateGlobalFlags()
    /// sets the Main.* flag to false. If Terraria auto-saves while that flag is false,
    /// the world permanently loses its seed identity.
    ///
    /// Solution: Harmony prefix on WorldFile.SaveWorld restores original seed flags
    /// before save, and postfix re-applies the SeedLab overrides afterward.
    /// Same pattern used by Core's WorldSavePatches for custom item extraction.
    /// </summary>
    internal static class WorldSaveFlagProtection
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static FeatureManager _featureManager;
        private static bool _applied;

        // Snapshot of overridden flag values captured in prefix, restored in postfix
        private static Dictionary<string, bool> _overriddenValues;

        public static void Apply(Harmony harmony, FeatureManager featureManager, ILogger log)
        {
            if (_applied) return;

            _harmony = harmony;
            _featureManager = featureManager;
            _log = log;

            try
            {
                var worldFileType = typeof(Terraria.IO.WorldFile);

                // Try all known SaveWorld signatures (varies by Terraria version)
                var saveMethod = worldFileType.GetMethod("SaveWorld",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(bool), typeof(bool), typeof(bool) }, null);

                if (saveMethod == null)
                    saveMethod = worldFileType.GetMethod("SaveWorld",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(bool), typeof(bool) }, null);

                if (saveMethod == null)
                    saveMethod = worldFileType.GetMethod("SaveWorld",
                        BindingFlags.Public | BindingFlags.Static, null,
                        Type.EmptyTypes, null);

                if (saveMethod == null)
                {
                    _log?.Warn("[SeedLab] WorldFile.SaveWorld not found — flag protection disabled");
                    return;
                }

                _harmony.Patch(saveMethod,
                    prefix: new HarmonyMethod(typeof(WorldSaveFlagProtection), nameof(SaveWorld_Prefix)),
                    postfix: new HarmonyMethod(typeof(WorldSaveFlagProtection), nameof(SaveWorld_Postfix)));

                _applied = true;
                _log?.Info("[SeedLab] World save flag protection applied");
            }
            catch (Exception ex)
            {
                _log?.Error($"[SeedLab] Failed to apply save flag protection: {ex.Message}");
            }
        }

        /// <summary>
        /// Before world save: capture current (overridden) flag values, then restore originals
        /// so the save file contains the world's true seed identity.
        /// </summary>
        private static void SaveWorld_Prefix()
        {
            if (_featureManager == null || !_featureManager.Initialized) return;

            try
            {
                _overriddenValues = new Dictionary<string, bool>();

                foreach (var seed in SeedFeatures.Seeds)
                {
                    // Capture the current (possibly overridden) value
                    bool currentValue = FeatureManager.GetFlag(seed.FlagField);
                    _overriddenValues[seed.FlagField] = currentValue;

                    // Restore the world's original value for save
                    bool originalValue = _featureManager.GetWorldOriginalFlag(seed.FlagField);
                    if (currentValue != originalValue)
                    {
                        FeatureManager.SetFlag(seed.FlagField, originalValue);
                    }
                }

                _log?.Debug("[SeedLab] Restored original seed flags for world save");
            }
            catch (Exception ex)
            {
                _log?.Error($"[SeedLab] SaveWorld_Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// After world save: re-apply the SeedLab overrides so gameplay continues unaffected.
        /// </summary>
        private static void SaveWorld_Postfix()
        {
            if (_overriddenValues == null) return;

            try
            {
                foreach (var kvp in _overriddenValues)
                {
                    FeatureManager.SetFlag(kvp.Key, kvp.Value);
                }

                _log?.Debug("[SeedLab] Re-applied SeedLab flag overrides after world save");
            }
            catch (Exception ex)
            {
                _log?.Error($"[SeedLab] SaveWorld_Postfix error: {ex.Message}");
            }
            finally
            {
                _overriddenValues = null;
            }
        }
    }
}
