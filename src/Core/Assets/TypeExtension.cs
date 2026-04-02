using System;
using System.Reflection;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Extends ItemID.Count and resizes all type-indexed arrays to accommodate custom items.
    /// Must run during initialization BEFORE any custom items are created.
    ///
    /// Arrays resized:
    ///   - ~130 ItemID.Sets arrays (via SetFactory)
    ///   - TextureAssets.Item[] and ItemFlame[]
    ///   - Lang._itemNameCache[] and _itemTooltipCache[]
    ///   - Main.itemAnimations[] and itemAnimationsRegistered[]
    ///   - Item.staff[] and Item.claw[]
    ///   - Any other static array in Terraria assembly matching ItemID.Count size
    ///     (comprehensive scan catches ArmorSetBonuses, QuickStacking, etc.)
    /// </summary>
    public static class TypeExtension
    {
        private static ILogger _log;
        private static bool _applied;

        /// <summary>The original vanilla item count before extension.</summary>
        public static int OriginalCount { get; private set; }

        /// <summary>The new extended count.</summary>
        public static int ExtendedCount { get; private set; }

        /// <summary>
        /// Extend ItemID.Count and resize all type-indexed arrays.
        /// Call once during early initialization (Main constructor postfix).
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="newCount">New maximum item count (default 32767 = max positive short).</param>
        /// <returns>The original vanilla item count, or -1 on failure.</returns>
        /// <summary>
        /// Extend the type system using a content type descriptor.
        /// The descriptor specifies which Sets class to scan, the vanilla count, the
        /// extended count, and any additional named fields to resize explicitly.
        /// </summary>
        internal static int Apply(ILogger logger, ContentTypeDescriptor descriptor)
        {
            _log = logger;
            if (_applied)
            {
                _log?.Warn("[TypeExtension] Already applied");
                return OriginalCount;
            }

            try
            {
                _log?.Info("[TypeExtension] Extending type system...");

                OriginalCount = descriptor.VanillaCount;
                ExtendedCount = descriptor.ExtendedCount;
                _log?.Info($"[TypeExtension] VanillaCount = {OriginalCount}, extending to {ExtendedCount}");

                // Step 1: Resize IdClass.Sets arrays (ItemID.Sets, TileID.Sets, etc.)
                int setsResized = ResizeSetsClass(descriptor.IdClass, OriginalCount, ExtendedCount);

                // Step 2: Resize additional named fields listed in the descriptor
                // (TextureAssets.Item, Lang._itemNameCache, Main.itemAnimations, Item.staff, etc.)
                int namedResized = ResizeNamedFields(descriptor.AdditionalFields, OriginalCount, ExtendedCount);

                // Step 3: Comprehensive scan — catch any static array in the Terraria assembly
                // matching the vanilla count that wasn't covered by steps 1–2.
                // ArmorSetBonuses, QuickStacking, and similar arrays are caught here.
                // Safety: the vanilla count (6145 for items) is specific enough that any array
                // of that exact size is almost certainly indexed by that content type.
                int assemblyResized = ResizeAllAssemblyArrays(OriginalCount, ExtendedCount);

                // DO NOT change IdClass.Count — PostSetupContent() iterates 0..Count-1
                // and runs AFTER OnGameReady, accessing ContentSamples that won't have
                // custom type entries yet. Instance arrays (QuickStacking, ItemSorting) are
                // handled by safety finalizers in DrawPatches instead.

                // Step 4: On dedicated server, also scan TerrariaServer.exe assembly.
                // TerrariaServer has its OWN copies of ItemID.Sets, Item.cachedItemSpawnsByType, etc.
                // Without extending those, Item.NewItem(customType) crashes with IndexOutOfRangeException.
                if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
                {
                    int serverResized = 0;
                    foreach (var asm2 in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm2 == typeof(Terraria.Main).Assembly) continue; // Skip the one we already scanned
                        string asmName = asm2.GetName().Name;
                        if (asmName != "TerrariaServer" && asmName != "Terraria") continue;

                        try
                        {
                            // Scan the server assembly's ItemID.Sets
                            var serverIdClass = asm2.GetType(descriptor.IdClass?.FullName ?? "Terraria.ID.ItemID");
                            if (serverIdClass != null)
                                serverResized += ResizeSetsClass(serverIdClass, OriginalCount, ExtendedCount);

                            // Comprehensive array scan on server assembly
                            foreach (var type in asm2.GetTypes())
                            {
                                try
                                {
                                    var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                                    foreach (var field in fields)
                                    {
                                        if (!field.FieldType.IsArray) continue;
                                        try
                                        {
                                            var arr = field.GetValue(null) as Array;
                                            if (arr == null || arr.Length != OriginalCount) continue;
                                            var elemType = field.FieldType.GetElementType();
                                            var newArr = Array.CreateInstance(elemType, ExtendedCount);
                                            Array.Copy(arr, newArr, Math.Min(arr.Length, ExtendedCount));
                                            FillNewEntries(newArr, arr, OriginalCount, ExtendedCount, elemType);
                                            field.SetValue(null, newArr);
                                            serverResized++;
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log?.Warn($"[TypeExtension] Server assembly scan error: {ex.Message}");
                        }
                    }
                    if (serverResized > 0)
                        _log?.Info($"[TypeExtension] Server assembly: resized {serverResized} arrays");
                }

                _applied = true;
                _log?.Info($"[TypeExtension] Complete: {setsResized} Sets + {namedResized} named + {assemblyResized} assembly-wide arrays resized");

                return OriginalCount;
            }
            catch (Exception ex)
            {
                _log?.Error($"[TypeExtension] Failed: {ex.Message}\n{ex.StackTrace}");
                return -1;
            }
        }

        /// <summary>
        /// Resize all static array fields inside idClass.Sets (e.g. ItemID.Sets, TileID.Sets).
        /// Also updates SetFactory._size and clears its buffer caches.
        /// </summary>
        private static int ResizeSetsClass(Type idClass, int oldSize, int newSize)
        {
            int count = 0;
            try
            {
                var setsType = idClass?.GetNestedType("Sets", BindingFlags.Public);
                if (setsType == null)
                {
                    _log?.Warn($"[TypeExtension] {idClass?.Name}.Sets not found");
                    return 0;
                }

                // Update SetFactory._size and clear caches
                var factoryField = setsType.GetField("Factory", BindingFlags.Public | BindingFlags.Static);
                if (factoryField != null)
                {
                    var factory = factoryField.GetValue(null);
                    if (factory != null)
                    {
                        var sizeField = factory.GetType().GetField("_size", BindingFlags.NonPublic | BindingFlags.Instance);
                        sizeField?.SetValue(factory, newSize);

                        foreach (var cacheName in new[] { "_boolBufferCache", "_intBufferCache", "_ushortBufferCache", "_floatBufferCache" })
                        {
                            var cacheField = factory.GetType().GetField(cacheName, BindingFlags.NonPublic | BindingFlags.Instance);
                            if (cacheField != null)
                                cacheField.GetValue(factory)?.GetType().GetMethod("Clear")?.Invoke(cacheField.GetValue(factory), null);
                        }
                        _log?.Debug("[TypeExtension] Updated SetFactory._size and cleared caches");
                    }
                }

                // Resize all static array fields in Sets
                var fields = setsType.GetFields(BindingFlags.Public | BindingFlags.Static);
                foreach (var field in fields)
                {
                    if (field.Name == "Factory" || !field.FieldType.IsArray) continue;
                    try
                    {
                        var arr = field.GetValue(null) as Array;
                        if (arr == null || arr.Length != oldSize) continue;

                        var elemType = field.FieldType.GetElementType();
                        var newArr = Array.CreateInstance(elemType, newSize);
                        Array.Copy(arr, newArr, Math.Min(arr.Length, newSize));
                        FillNewEntries(newArr, arr, oldSize, newSize, elemType);
                        field.SetValue(null, newArr);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        _log?.Debug($"[TypeExtension] Failed to resize Sets.{field.Name}: {ex.Message}");
                    }
                }

                _log?.Debug($"[TypeExtension] Resized {count} {idClass?.Name}.Sets arrays");
            }
            catch (Exception ex)
            {
                _log?.Error($"[TypeExtension] Error resizing Sets: {ex.Message}");
            }
            return count;
        }

        /// <summary>
        /// Resize named static array fields listed in the descriptor's AdditionalFields.
        /// Used for fields like TextureAssets.Item, Lang._itemNameCache, Main.itemAnimations.
        /// Each field is found by declaring type + field name; missing fields are silently skipped.
        /// </summary>
        private static int ResizeNamedFields(ContentTypeDescriptor.NamedField[] fields, int oldSize, int newSize)
        {
            if (fields == null) return 0;
            int count = 0;
            foreach (var nf in fields)
            {
                try
                {
                    if (nf.DeclaringType == null) continue;
                    var field = nf.DeclaringType.GetField(nf.FieldName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (field == null || !field.FieldType.IsArray) continue;

                    var arr = field.GetValue(null) as Array;
                    if (arr == null || arr.Length != oldSize) continue;

                    var elemType = field.FieldType.GetElementType();
                    var newArr = Array.CreateInstance(elemType, newSize);
                    Array.Copy(arr, newArr, arr.Length);
                    FillNewEntries(newArr, arr, oldSize, newSize, elemType);
                    field.SetValue(null, newArr);
                    count++;
                    _log?.Debug($"[TypeExtension] Resized {nf.DeclaringType.Name}.{nf.FieldName}: {oldSize} → {newSize}");
                }
                catch (Exception ex)
                {
                    _log?.Debug($"[TypeExtension] Failed to resize {nf.DeclaringType?.Name}.{nf.FieldName}: {ex.Message}");
                }
            }
            return count;
        }

        /// <summary>
        /// Fill newly-extended array entries with the appropriate default value.
        /// Uses index 0 (the empty/air item slot) as the sentinel: vanilla never gives
        /// the air item special properties, so arr[0] IS the semantic default for any
        /// given array — whether that's -1, 0, 0f, or false.
        /// </summary>
        private static void FillNewEntries(Array newArr, Array oldArr, int oldSize, int newSize, Type elemType)
        {
            if (oldSize >= newSize) return;

            if (elemType == typeof(int))
            {
                int defaultVal = (int)oldArr.GetValue(0);
                if (defaultVal != 0)
                {
                    var newIntArr = (int[])newArr;
                    for (int i = oldSize; i < newSize; i++)
                        newIntArr[i] = defaultVal;
                }
            }
            else if (elemType == typeof(short))
            {
                short defaultVal = (short)oldArr.GetValue(0);
                if (defaultVal != 0)
                {
                    var newShortArr = (short[])newArr;
                    for (int i = oldSize; i < newSize; i++)
                        newShortArr[i] = defaultVal;
                }
            }
            else if (elemType == typeof(float))
            {
                float defaultVal = (float)oldArr.GetValue(0);
                if (defaultVal != 0f)
                {
                    var newFloatArr = (float[])newArr;
                    for (int i = oldSize; i < newSize; i++)
                        newFloatArr[i] = defaultVal;
                }
            }
            // bool arrays: default is false — already correct (Array.CreateInstance initialises to false)
            // Reference arrays: fill with slot-0 value as placeholder
            // (prevents NullReferenceException if vanilla code accesses custom type slots in
            //  TextureAssets.Item[], Lang._itemNameCache[], etc.)
            else if (!elemType.IsValueType)
            {
                object placeholder = oldArr.GetValue(0);
                if (placeholder != null)
                {
                    for (int i = oldSize; i < newSize; i++)
                        newArr.SetValue(placeholder, i);
                }
            }
        }


        /// <summary>
        /// Comprehensive scan: find ALL static array fields in the Terraria assembly
        /// whose length matches the old ItemID.Count, and resize them.
        /// This catches arrays we don't know about (ArmorSetBonuses, QuickStacking, etc.)
        /// that would cause IndexOutOfRangeException for custom item types.
        ///
        /// Safety: 6145 is a very specific number (ItemID.Count). Any array of that
        /// exact size is almost certainly indexed by item type. Making it bigger with
        /// default-filled entries is safe even for false positives.
        /// </summary>
        private static int ResizeAllAssemblyArrays(int oldSize, int newSize)
        {
            int count = 0;
            try
            {
                var asm = typeof(Terraria.Main).Assembly;

                foreach (var type in asm.GetTypes())
                {
                    try
                    {
                        var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        foreach (var field in fields)
                        {
                            if (!field.FieldType.IsArray) continue;

                            try
                            {
                                var arr = field.GetValue(null) as Array;
                                if (arr == null || arr.Length != oldSize) continue;

                                // Build a unique key to track what we resize
                                string key = $"{type.FullName}.{field.Name}";

                                var elemType = field.FieldType.GetElementType();
                                var newArr = Array.CreateInstance(elemType, newSize);
                                Array.Copy(arr, newArr, Math.Min(arr.Length, newSize));

                                FillNewEntries(newArr, arr, oldSize, newSize, elemType);

                                field.SetValue(null, newArr);
                                count++;
                                _log?.Info($"[TypeExtension] Assembly scan resized {key} ({elemType.Name}[{oldSize}] → {newSize})");
                            }
                            catch (Exception ex)
                            {
                                _log?.Debug($"[TypeExtension] Skipped {type.FullName}.{field.Name}: {ex.Message}");
                            }
                        }
                    }
                    catch
                    {
                        // Some types may not be loadable (generic constraints, etc.)
                    }
                }

                _log?.Info($"[TypeExtension] Assembly-wide scan resized {count} additional arrays");
            }
            catch (Exception ex)
            {
                _log?.Error($"[TypeExtension] Assembly scan error: {ex.Message}");
            }
            return count;
        }
    }
}
