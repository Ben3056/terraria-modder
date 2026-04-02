using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Prefix patch on Main.MouseText_DrawItemTooltip_GetLinesInfo.
    ///
    /// Problem: Vanilla tooltip code (lines 20541-20890 in Main.cs) accesses
    /// ItemID.Sets arrays by item.type — e.g. ToolTipDamageMultiplier[item.type],
    /// RapidAttackBonusDamage[item.type], PlaceTileOnAltUse[item.type], etc.
    /// For custom items (type >= 6145), even though TypeExtension resizes most
    /// arrays, any missed array or edge case causes IndexOutOfRangeException.
    /// Since there's no try-catch in the tooltip rendering chain, the exception
    /// kills the entire frame's tooltip rendering — the user sees nothing.
    ///
    /// Fix: For custom items, build tooltip lines manually and skip vanilla.
    /// This avoids ALL array access issues. Vanilla items pass through unchanged.
    /// </summary>
    internal static class TooltipPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        // Cached Lang.tip access
        private static Array _langTip;
        private static PropertyInfo _ltValueProp; // LocalizedText.Value

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.tooltip");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                CacheReflection();
                PatchGetLinesInfo();
                _applied = true;
                _log?.Info("[TooltipPatches] Applied successfully");
            }
            catch (Exception ex)
            {
                _log?.Error($"[TooltipPatches] Failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void CacheReflection()
        {
            var langType = typeof(Terraria.Lang);
            var tipField = langType.GetField("tip", BindingFlags.Public | BindingFlags.Static);
            _langTip = tipField?.GetValue(null) as Array;
            _log?.Debug($"[TooltipPatches] Lang.tip cached: {_langTip?.Length ?? -1} entries");

            // Cache LocalizedText.Value property
            var localizedTextType = typeof(Terraria.Localization.LocalizedText);
            _ltValueProp = localizedTextType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        }

        private static void PatchGetLinesInfo()
        {
            var mainType = typeof(Main);
            var target = mainType.GetMethod("MouseText_DrawItemTooltip_GetLinesInfo",
                BindingFlags.Public | BindingFlags.Static);

            if (target == null)
            {
                _log?.Warn("[TooltipPatches] MouseText_DrawItemTooltip_GetLinesInfo not found");
                return;
            }

            var prefix = typeof(TooltipPatches).GetMethod(nameof(GetLinesInfo_Prefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            _log?.Info("[TooltipPatches] Patched GetLinesInfo");
        }

        /// <summary>
        /// Prefix for MouseText_DrawItemTooltip_GetLinesInfo.
        /// Custom items: build tooltip manually, return false (skip vanilla).
        /// Vanilla items: return true (let vanilla handle).
        /// </summary>
        private static bool GetLinesInfo_Prefix(Item item, ref int numLines, string[] toolTipLine)
        {
            if (item == null || item.type < ItemRegistry.VanillaItemCount)
                return true;

            var def = ItemRegistry.GetDefinition(item.type);
            if (def == null)
            {
                // I4: KnownUnknown placeholder — build minimal tooltip
                if (ItemRegistry.IsKnownUnknown(item.type, out string fullId))
                {
                    try
                    {
                        string modId = fullId.Contains(":") ? fullId.Split(':')[0] : fullId;
                        toolTipLine[0] = $"[{fullId}]";
                        toolTipLine[1] = $"This item requires [{modId}] to be installed.";
                        numLines = 2;
                    }
                    catch { toolTipLine[0] = "[Unknown Item]"; numLines = 1; }
                    return false;
                }
                return true;
            }

            try
            {
                BuildCustomTooltip(item, ref numLines, toolTipLine);
                return false;
            }
            catch (Exception ex)
            {
                _log?.Error($"[TooltipPatches] Error building tooltip for type {item.type}: {ex.Message}");
                // Fallback: at minimum show the name
                toolTipLine[0] = item.AffixName();
                numLines = 1;
                return false; // still skip vanilla to prevent crash
            }
        }

        private static void BuildCustomTooltip(Item item, ref int numLines, string[] toolTipLine)
        {
            numLines = 1;

            // Line 0: item name (with stack suffix if > 1)
            string name = item.AffixName();
            if (item.stack > 1)
                name += " (" + item.stack + ")";
            toolTipLine[0] = name;

            // Damage + type
            if (item.damage > 0 && (!item.notAmmo || item.useStyle != 0))
            {
                string damageType;
                if (item.melee) damageType = GetTip(2);
                else if (item.ranged) damageType = GetTip(3);
                else if (item.magic) damageType = GetTip(4);
                else if (item.summon) damageType = GetTip(53);
                else damageType = GetTip(55);

                toolTipLine[numLines++] = item.damage + damageType;

                // Critical strike chance
                if (item.melee || item.ranged || item.magic)
                {
                    toolTipLine[numLines++] = item.crit + GetTip(5);
                }

                // Attack speed (based on useAnimation)
                if (item.useStyle != 0 && (!item.summon))
                {
                    if (item.useAnimation <= 8) toolTipLine[numLines++] = GetTip(6);
                    else if (item.useAnimation <= 20) toolTipLine[numLines++] = GetTip(7);
                    else if (item.useAnimation <= 25) toolTipLine[numLines++] = GetTip(8);
                    else if (item.useAnimation <= 30) toolTipLine[numLines++] = GetTip(9);
                    else if (item.useAnimation <= 35) toolTipLine[numLines++] = GetTip(10);
                    else if (item.useAnimation <= 45) toolTipLine[numLines++] = GetTip(11);
                    else if (item.useAnimation <= 55) toolTipLine[numLines++] = GetTip(12);
                    else toolTipLine[numLines++] = GetTip(13);
                }

                // Knockback
                float kb = item.knockBack;
                if (kb == 0f) toolTipLine[numLines++] = GetTip(14);
                else if (kb <= 1.5) toolTipLine[numLines++] = GetTip(15);
                else if (kb <= 3f) toolTipLine[numLines++] = GetTip(16);
                else if (kb <= 4f) toolTipLine[numLines++] = GetTip(17);
                else if (kb <= 6f) toolTipLine[numLines++] = GetTip(18);
                else if (kb <= 7f) toolTipLine[numLines++] = GetTip(19);
                else if (kb <= 9f) toolTipLine[numLines++] = GetTip(20);
                else if (kb <= 11f) toolTipLine[numLines++] = GetTip(21);
                else toolTipLine[numLines++] = GetTip(22);
            }

            // Equipment
            if (item.accessory || item.headSlot > 0 || item.bodySlot > 0 || item.legSlot > 0)
            {
                toolTipLine[numLines++] = GetTip(23);
            }

            // Vanity
            if (item.vanity)
            {
                toolTipLine[numLines++] = GetTip(24);
            }

            // Defense
            if (item.defense > 0)
            {
                toolTipLine[numLines++] = item.defense + GetTip(25);
            }

            // Pick/Axe/Hammer
            if (item.pick > 0) toolTipLine[numLines++] = item.pick + GetTip(26);
            if (item.axe > 0) toolTipLine[numLines++] = (item.axe * 5) + GetTip(27);
            if (item.hammer > 0) toolTipLine[numLines++] = item.hammer + GetTip(28);

            // Tile range bonus
            if (item.tileBoost != 0)
            {
                string prefix = item.tileBoost > 0 ? "+" : "";
                toolTipLine[numLines++] = prefix + item.tileBoost + GetTip(54);
            }

            // Healing
            if (item.healLife > 0)
            {
                toolTipLine[numLines++] = "Restores " + item.healLife + " life";
            }
            if (item.healMana > 0)
            {
                toolTipLine[numLines++] = "Restores " + item.healMana + " mana";
            }

            // Mana cost
            if (item.mana > 0)
            {
                toolTipLine[numLines++] = "Uses " + item.mana + " mana";
            }

            // Placeable / Consumable / Ammo
            if (item.createWall > 0 || item.createTile > -1)
            {
                if (item.consumable)
                    toolTipLine[numLines++] = GetTip(33);
            }
            else if (item.ammo > 0 && !item.notAmmo)
            {
                toolTipLine[numLines++] = GetTip(34);
            }
            else if (item.consumable)
            {
                toolTipLine[numLines++] = GetTip(35);
            }

            // Material
            if (item.material)
            {
                toolTipLine[numLines++] = GetTip(36);
            }

            // Tooltip description lines from item.ToolTip
            if (item.ToolTip != null)
            {
                int lines = item.ToolTip.Lines;
                for (int i = 0; i < lines && numLines < toolTipLine.Length; i++)
                {
                    string line = item.ToolTip.GetLine(i);
                    if (!string.IsNullOrEmpty(line))
                        toolTipLine[numLines++] = line;
                }
            }

            // Buff duration
            if (item.buffTime > 0)
            {
                if (item.buffTime / 60 < 60)
                    toolTipLine[numLines++] = Math.Round((double)item.buffTime / 60.0) + " second duration";
                else
                    toolTipLine[numLines++] = Math.Round((double)(item.buffTime / 60) / 60.0) + " minute duration";
            }

            // ModifyTooltips hook — let definition add/remove/change lines dynamically
            var def = ItemRegistry.GetDefinition(item.type);
            if (def?.ModifyTooltips != null)
            {
                try
                {
                    // Copy current lines into a mutable list
                    var lines = new List<string>(numLines);
                    for (int i = 0; i < numLines; i++)
                        lines.Add(toolTipLine[i]);

                    def.ModifyTooltips(lines);

                    // Write back (capped to array size)
                    int cap = Math.Min(lines.Count, toolTipLine.Length);
                    for (int i = 0; i < cap; i++)
                        toolTipLine[i] = lines[i];
                    numLines = cap;
                }
                catch (Exception ex)
                {
                    _log?.Debug($"[TooltipPatches] ModifyTooltips error for type {item.type}: {ex.Message}");
                }
            }

            // J1: Apply global tooltip modifiers (registered via context.RegisterTooltipModifier)
            var globalModifiers = TooltipRegistry.GetModifiers();
            if (globalModifiers.Count > 0)
            {
                try
                {
                    var lines = new List<string>(numLines);
                    for (int i = 0; i < numLines; i++)
                        lines.Add(toolTipLine[i]);

                    foreach (var modifier in globalModifiers)
                    {
                        try { modifier(item.type, lines); }
                        catch (Exception ex) { _log?.Debug($"[TooltipPatches] Global modifier error: {ex.Message}"); }
                    }

                    int cap = Math.Min(lines.Count, toolTipLine.Length);
                    for (int i = 0; i < cap; i++)
                        toolTipLine[i] = lines[i];
                    numLines = cap;
                }
                catch (Exception ex)
                {
                    _log?.Debug($"[TooltipPatches] Global modifier dispatch error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Get a Lang.tip[index].Value string. Returns empty string on failure.
        /// </summary>
        private static string GetTip(int index)
        {
            if (_langTip == null || index < 0 || index >= _langTip.Length)
                return "";

            try
            {
                var obj = _langTip.GetValue(index);
                if (obj == null) return "";
                return _ltValueProp?.GetValue(obj) as string ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
