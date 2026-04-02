using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Shimmer transform registry (Phase J3).
    /// Mods register custom shimmer transforms via context.RegisterShimmer(ShimmerDefinition).
    ///
    /// At ApplyPatches() time, a prefix on Item.GetShimmerEquivalentType() is applied:
    /// if the item's type has a registered transform, the output type is returned early
    /// (bypassing vanilla logic). A prefix on Item.CanShimmer() returns true for custom
    /// items with a registered transform.
    ///
    /// Server-safe: shimmer transforms are processed server-side in multiplayer.
    /// </summary>
    public static class ShimmerRegistry
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        // inputType → ShimmerDefinition (resolved type IDs)
        private static readonly Dictionary<int, ResolvedShimmer> _transforms
            = new Dictionary<int, ResolvedShimmer>();

        // Pending registrations (registered before types assigned — resolved in ApplyPatches)
        private static readonly List<ShimmerDefinition> _pending
            = new List<ShimmerDefinition>();

        private struct ResolvedShimmer
        {
            public int OutputType;
            public int InputStack;
            public int OutputStack;
        }

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.shimmer");
        }

        /// <summary>Register a shimmer transform. Can be called during Initialize or OnContentReady.</summary>
        public static void Register(ShimmerDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.InputId) || string.IsNullOrEmpty(def.OutputId)) return;
            _pending.Add(def);
        }

        /// <summary>Resolve pending registrations and apply patches. Called from AssetSystem.ApplyPatches().</summary>
        public static void ApplyPatches()
        {
            if (_applied) return;
            ResolvePending();

            if (_transforms.Count == 0)
            {
                _log?.Info("[ShimmerRegistry] No shimmer transforms registered");
                _applied = true;
                return;
            }

            try
            {
                PatchGetShimmerEquivalentType();
                PatchCanShimmer();
                _applied = true;
                _log?.Info($"[ShimmerRegistry] Applied shimmer patches ({_transforms.Count} transform(s))");
            }
            catch (Exception ex)
            {
                _log?.Error($"[ShimmerRegistry] Failed to apply patches: {ex.Message}");
            }
        }

        private static void ResolvePending()
        {
            foreach (var def in _pending)
            {
                int inputType = ItemRegistry.ResolveItemType(def.InputId);
                int outputType = ItemRegistry.ResolveItemType(def.OutputId);

                if (inputType < 0)
                {
                    _log?.Warn($"[ShimmerRegistry] Cannot resolve input '{def.InputId}' — skipping");
                    continue;
                }
                if (outputType < 0)
                {
                    _log?.Warn($"[ShimmerRegistry] Cannot resolve output '{def.OutputId}' — skipping");
                    continue;
                }

                _transforms[inputType] = new ResolvedShimmer
                {
                    OutputType  = outputType,
                    InputStack  = Math.Max(1, def.InputStack),
                    OutputStack = Math.Max(1, def.OutputStack),
                };
                _log?.Debug($"[ShimmerRegistry] Registered: {def.InputId} (type {inputType}) → {def.OutputId} (type {outputType})");
            }
            _pending.Clear();
        }

        /// <summary>Check if a type has a registered shimmer transform.</summary>
        public static bool TryGetTransform(int inputType, out int outputType)
        {
            if (_transforms.TryGetValue(inputType, out var resolved))
            {
                outputType = resolved.OutputType;
                return true;
            }
            outputType = -1;
            return false;
        }

        /// <summary>Check if a type has a registered shimmer transform and return full details.</summary>
        public static bool TryGetTransform(int inputType, out int outputType, out int inputStack, out int outputStack)
        {
            if (_transforms.TryGetValue(inputType, out var resolved))
            {
                outputType  = resolved.OutputType;
                inputStack  = resolved.InputStack;
                outputStack = resolved.OutputStack;
                return true;
            }
            outputType = -1; inputStack = 1; outputStack = 1;
            return false;
        }

        private static void PatchGetShimmerEquivalentType()
        {
            // Item.GetShimmerEquivalentType(bool forDecrafting = false) — one optional bool param
            var method = typeof(Terraria.Item).GetMethod("GetShimmerEquivalentType",
                BindingFlags.Public | BindingFlags.Instance, null,
                new Type[] { typeof(bool) }, null);

            if (method == null)
            {
                _log?.Warn("[ShimmerRegistry] Item.GetShimmerEquivalentType not found — shimmer transform prefix not applied");
                return;
            }

            var prefix = typeof(ShimmerRegistry).GetMethod(nameof(GetShimmerEquivalentType_Prefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            _log?.Debug("[ShimmerRegistry] Patched Item.GetShimmerEquivalentType");
        }

        private static void PatchCanShimmer()
        {
            // Item.CanShimmer() — public instance method, no params
            var method = typeof(Terraria.Item).GetMethod("CanShimmer",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

            if (method == null)
            {
                _log?.Warn("[ShimmerRegistry] Item.CanShimmer not found — CanShimmer prefix not applied");
                return;
            }

            var prefix = typeof(ShimmerRegistry).GetMethod(nameof(CanShimmer_Prefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            _log?.Debug("[ShimmerRegistry] Patched Item.CanShimmer");
        }

        private static bool GetShimmerEquivalentType_Prefix(Terraria.Item __instance, ref int __result)
        {
            if (TryGetTransform(__instance.type, out int outputType))
            {
                __result = outputType;
                return false; // skip vanilla
            }
            return true;
        }

        private static bool CanShimmer_Prefix(Terraria.Item __instance, ref bool __result)
        {
            if (_transforms.ContainsKey(__instance.type))
            {
                __result = true;
                return false; // skip vanilla (which returns false for unknown types)
            }
            return true;
        }

        public static void Clear()
        {
            _transforms.Clear();
            _pending.Clear();
        }
    }
}
