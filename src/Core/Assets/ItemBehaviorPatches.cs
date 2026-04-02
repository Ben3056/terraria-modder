using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Harmony patches for custom item runtime behavior hooks.
    /// Dispatches to ItemDefinition delegate fields for custom items (type >= VanillaItemCount).
    ///
    /// Patches:
    ///   1. CanUseItem      → Player.ItemCheck_CheckCanUse_Inner PREFIX
    ///   2. OnUse           → Player.ItemCheck_StartActualUse PREFIX
    ///   3. OnHitNPC        → Player.ProcessHitAgainstNPC POSTFIX (melee hit chain)
    ///   4. ModifyWeaponDamage → Player.GetWeaponDamage POSTFIX
    ///   5. Shoot           → Player.ItemCheck_Shoot PREFIX+FINALIZER
    ///                        + Player.PickAmmo POSTFIX (override after ammo resolution)
    ///   6. OnConsume       → Player.CanConsumeConsumableItem PREFIX
    ///   7. UpdateEquip     → Player.UpdateEquips POSTFIX
    ///   8. OnHoldItem      → Player.ItemCheck POSTFIX
    /// </summary>
    internal static class ItemBehaviorPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        // For Shoot override: stash original value during prefix/finalizer
        [ThreadStatic]
        private static int _shootStash;
        [ThreadStatic]
        private static bool _shootOverrideActive;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.behavior");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                var playerType = typeof(Player);
                int patchCount = 0;

                // 1. CanUseItem — Player.ItemCheck_CheckCanUse_Inner(Item, bool)
                patchCount += PatchMethod(playerType,
                    "ItemCheck_CheckCanUse_Inner",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    new[] { typeof(Item), typeof(bool) },
                    nameof(CanUseItem_Prefix), null);

                // 2. OnUse — Player.ItemCheck_StartActualUse(Item)
                patchCount += PatchMethod(playerType,
                    "ItemCheck_StartActualUse",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    new[] { typeof(Item) },
                    nameof(OnUse_Prefix), null);

                // 3. OnHitNPC — Player.ProcessHitAgainstNPC (melee sword hits)
                patchCount += PatchProcessHitAgainstNPC(playerType);

                // 4. ModifyWeaponDamage — Player.GetWeaponDamage(Item)
                patchCount += PatchMethod(playerType,
                    "GetWeaponDamage",
                    BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(Item) },
                    null, nameof(ModifyWeaponDamage_Postfix));

                // 5. Shoot — Player.ItemCheck_Shoot PREFIX+FINALIZER
                //          + Player.PickAmmo POSTFIX (ammo overrides projToShoot)
                patchCount += PatchShoot(playerType);
                patchCount += PatchPickAmmo(playerType);

                // 6. OnConsume — Player.CanConsumeConsumableItem(Item)
                patchCount += PatchMethod(playerType,
                    "CanConsumeConsumableItem",
                    BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(Item) },
                    nameof(OnConsume_Prefix), null);

                // 7. UpdateEquip — Player.UpdateEquips(int)
                patchCount += PatchMethod(playerType,
                    "UpdateEquips",
                    BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(int) },
                    null, nameof(UpdateEquip_Postfix));

                // 8. OnHoldItem — Player.ItemCheck()
                patchCount += PatchMethod(playerType,
                    "ItemCheck",
                    BindingFlags.Public | BindingFlags.Instance,
                    Type.EmptyTypes,
                    null, nameof(OnHoldItem_Postfix));

                _applied = true;
                _log?.Info($"[ItemBehaviorPatches] Applied {patchCount}/8 patches");
            }
            catch (Exception ex)
            {
                _log?.Error($"[ItemBehaviorPatches] Failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static int PatchMethod(Type type, string name, BindingFlags flags, Type[] paramTypes,
            string prefixName, string postfixName)
        {
            try
            {
                var method = type.GetMethod(name, flags, null, paramTypes, null);
                if (method == null)
                {
                    _log?.Warn($"[ItemBehaviorPatches] {type.Name}.{name} not found");
                    return 0;
                }

                HarmonyMethod prefix = prefixName != null
                    ? new HarmonyMethod(typeof(ItemBehaviorPatches).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static))
                    : null;
                HarmonyMethod postfix = postfixName != null
                    ? new HarmonyMethod(typeof(ItemBehaviorPatches).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static))
                    : null;

                _harmony.Patch(method, prefix: prefix, postfix: postfix);
                _log?.Debug($"[ItemBehaviorPatches] Patched {type.Name}.{name}");
                return 1;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemBehaviorPatches] Failed to patch {type.Name}.{name}: {ex.Message}");
                return 0;
            }
        }

        private static int PatchShoot(Type playerType)
        {
            try
            {
                var method = playerType.GetMethod("ItemCheck_Shoot",
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(int), typeof(Item), typeof(int), typeof(bool) }, null);

                if (method == null)
                {
                    _log?.Warn("[ItemBehaviorPatches] Player.ItemCheck_Shoot not found");
                    return 0;
                }

                var prefix = new HarmonyMethod(typeof(ItemBehaviorPatches).GetMethod(
                    nameof(Shoot_Prefix), BindingFlags.NonPublic | BindingFlags.Static));
                var finalizer = new HarmonyMethod(typeof(ItemBehaviorPatches).GetMethod(
                    nameof(Shoot_Finalizer), BindingFlags.NonPublic | BindingFlags.Static));

                _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                _log?.Debug("[ItemBehaviorPatches] Patched Player.ItemCheck_Shoot (prefix+finalizer)");
                return 1;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemBehaviorPatches] Failed to patch ItemCheck_Shoot: {ex.Message}");
                return 0;
            }
        }

        private static int PatchProcessHitAgainstNPC(Type playerType)
        {
            try
            {
                // ProcessHitAgainstNPC(Item sItem, Rectangle itemRectangle, int originalDamage, float knockBack, int npcIndex)
                // This is the melee hit chain: ItemCheck_MeleeHitNPCs → ProcessHitAgainstNPC → NPC.StrikeNPC
                // Find ProcessHitAgainstNPC by name since Rectangle type needs special resolution
                MethodInfo method = null;
                foreach (var m in playerType.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name == "ProcessHitAgainstNPC")
                    {
                        method = m;
                        break;
                    }
                }

                if (method == null)
                {
                    _log?.Warn("[ItemBehaviorPatches] Player.ProcessHitAgainstNPC not found");
                    return 0;
                }

                var postfix = new HarmonyMethod(typeof(ItemBehaviorPatches).GetMethod(
                    nameof(OnHitNPC_Postfix), BindingFlags.NonPublic | BindingFlags.Static));

                _harmony.Patch(method, postfix: postfix);
                _log?.Debug("[ItemBehaviorPatches] Patched Player.ProcessHitAgainstNPC");
                return 1;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemBehaviorPatches] Failed to patch ProcessHitAgainstNPC: {ex.Message}");
                return 0;
            }
        }

        private static int PatchPickAmmo(Type playerType)
        {
            try
            {
                // PickAmmo(Item sItem, ref int projToShoot, ref float speed, ref bool canShoot,
                //          ref int Damage, ref float KnockBack, out int usedAmmoItemId, bool dontConsume)
                var method = playerType.GetMethod("PickAmmo",
                    BindingFlags.Public | BindingFlags.Instance);

                if (method == null)
                {
                    _log?.Warn("[ItemBehaviorPatches] Player.PickAmmo not found");
                    return 0;
                }

                var postfix = new HarmonyMethod(typeof(ItemBehaviorPatches).GetMethod(
                    nameof(PickAmmo_Postfix), BindingFlags.NonPublic | BindingFlags.Static));

                _harmony.Patch(method, postfix: postfix);
                _log?.Debug("[ItemBehaviorPatches] Patched Player.PickAmmo");
                return 1;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ItemBehaviorPatches] Failed to patch PickAmmo: {ex.Message}");
                return 0;
            }
        }

        // ── 1. CanUseItem ──

        private static bool CanUseItem_Prefix(Player __instance, Item sItem, ref bool __result)
        {
            if (sItem == null || sItem.type < ItemRegistry.VanillaItemCount) return true;

            var def = ItemRegistry.GetDefinition(sItem.type);
            if (def?.CanUseItem == null) return true;

            try
            {
                if (!def.CanUseItem(__instance))
                {
                    __result = false;
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ItemBehaviorPatches] CanUseItem error: {ex.Message}");
            }
            return true;
        }

        // ── 2. OnUse ──

        private static bool OnUse_Prefix(Player __instance, Item sItem)
        {
            if (sItem == null || sItem.type < ItemRegistry.VanillaItemCount) return true;

            var def = ItemRegistry.GetDefinition(sItem.type);
            if (def?.OnUse == null) return true;

            try
            {
                if (!def.OnUse(__instance))
                {
                    return false; // Cancel the use
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ItemBehaviorPatches] OnUse error: {ex.Message}");
            }
            return true;
        }

        // ── 3. OnHitNPC (via ProcessHitAgainstNPC) ──

        private static void OnHitNPC_Postfix(Player __instance, Item sItem, int originalDamage, float knockBack, int npcIndex)
        {
            try
            {
                if (sItem == null || sItem.type < ItemRegistry.VanillaItemCount) return;

                var def = ItemRegistry.GetDefinition(sItem.type);
                if (def?.OnHitNPC == null) return;

                NPC npc = (npcIndex >= 0 && npcIndex < Main.maxNPCs) ? Main.npc[npcIndex] : null;
                def.OnHitNPC(__instance, npc, originalDamage, knockBack, false);
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ItemBehaviorPatches] OnHitNPC error: {ex.Message}");
            }
        }

        // ── 4. ModifyWeaponDamage ──

        private static void ModifyWeaponDamage_Postfix(Player __instance, Item sItem, ref int __result)
        {
            if (sItem == null || sItem.type < ItemRegistry.VanillaItemCount) return;

            var def = ItemRegistry.GetDefinition(sItem.type);
            if (def?.ModifyWeaponDamage == null) return;

            try
            {
                int damage = __result;
                def.ModifyWeaponDamage(__instance, ref damage);
                __result = damage;
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ItemBehaviorPatches] ModifyWeaponDamage error: {ex.Message}");
            }
        }

        // ── 5. Shoot (prefix sets sItem.shoot, finalizer restores) ──

        private static void Shoot_Prefix(Player __instance, Item sItem)
        {
            _shootOverrideActive = false;

            if (sItem == null || sItem.type < ItemRegistry.VanillaItemCount) return;

            var def = ItemRegistry.GetDefinition(sItem.type);
            if (def?.OnShoot == null) return;

            try
            {
                int? overrideType = def.OnShoot(__instance, sItem.shoot, sItem.shootSpeed);
                if (overrideType.HasValue)
                {
                    _shootStash = sItem.shoot;
                    sItem.shoot = overrideType.Value;
                    _shootOverrideActive = true;
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ItemBehaviorPatches] Shoot prefix error: {ex.Message}");
            }
        }

        private static void Shoot_Finalizer(Item sItem)
        {
            if (_shootOverrideActive && sItem != null)
            {
                sItem.shoot = _shootStash;
                _shootOverrideActive = false;
            }
        }

        // ── 5b. PickAmmo postfix (override projectile after ammo resolution) ──

        private static void PickAmmo_Postfix(Player __instance, Item sItem, ref int projToShoot)
        {
            if (sItem == null || sItem.type < ItemRegistry.VanillaItemCount) return;

            var def = ItemRegistry.GetDefinition(sItem.type);
            if (def?.OnShoot == null) return;

            try
            {
                int? overrideType = def.OnShoot(__instance, projToShoot, sItem.shootSpeed);
                if (overrideType.HasValue)
                {
                    projToShoot = overrideType.Value;
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ItemBehaviorPatches] PickAmmo OnShoot override error: {ex.Message}");
            }
        }

        // ── 6. OnConsume ──

        private static bool OnConsume_Prefix(Player __instance, Item item, ref bool __result)
        {
            if (item == null || item.type < ItemRegistry.VanillaItemCount) return true;

            var def = ItemRegistry.GetDefinition(item.type);
            if (def?.OnConsume == null) return true;

            try
            {
                if (!def.OnConsume(__instance))
                {
                    __result = false;
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ItemBehaviorPatches] OnConsume error: {ex.Message}");
            }
            return true;
        }

        // ── 7. UpdateEquip ──

        private static void UpdateEquip_Postfix(Player __instance, int i)
        {
            try
            {
                // Use the slot index from Player.UpdateEquips(int i) directly
                if (i < 0 || i >= __instance.armor.Length) return;
                var item = __instance.armor[i];
                if (item == null || item.type < ItemRegistry.VanillaItemCount) return;

                var def = ItemRegistry.GetDefinition(item.type);
                if (def?.UpdateEquip == null) return;

                def.UpdateEquip(__instance);
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ItemBehaviorPatches] UpdateEquip error: {ex.Message}");
            }
        }

        // ── 8. OnHoldItem ──

        private static void OnHoldItem_Postfix(Player __instance)
        {
            try
            {
                var heldItem = __instance.inventory[__instance.selectedItem];
                if (heldItem == null || heldItem.type < ItemRegistry.VanillaItemCount) return;

                var def = ItemRegistry.GetDefinition(heldItem.type);
                if (def?.OnHoldItem == null) return;

                def.OnHoldItem(__instance);
            }
            catch (Exception ex)
            {
                _log?.Debug($"[ItemBehaviorPatches] OnHoldItem error: {ex.Message}");
            }
        }
    }
}
