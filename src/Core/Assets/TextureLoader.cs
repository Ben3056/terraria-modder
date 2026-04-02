using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Loads custom item textures from PNG files and injects them into TextureAssets.Item[runtimeType].
    /// Uses same reflection approach as the old TextureCache — Texture2D.FromStream via reflection.
    /// </summary>
    public static class TextureLoader
    {
        private static ILogger _log;

        // Reflection caches
        private static Type _texture2dType;
        private static MethodInfo _fromStreamMethod;
        private static object _graphicsDevice;
        private static bool _reflectionFailed;
        private static bool _reflectionReady;

        // Cache of loaded Asset objects keyed by runtime type, for fast re-injection
        // after the async texture loader overwrites our entries
        private static readonly Dictionary<int, object> _assetCache = new Dictionary<int, object>();

        public static void Initialize(ILogger logger)
        {
            _log = logger;
        }

        /// <summary>Whether XNA reflection is ready for texture loading.</summary>
        public static bool IsReflectionReady => _reflectionReady;

        /// <summary>
        /// Load and inject textures for all registered custom items.
        /// Must be called after ItemRegistry.AssignRuntimeTypes() and after game's GraphicsDevice is ready.
        /// Items without a texture .png get a generated placeholder texture.
        /// </summary>
        public static int InjectAllTextures()
        {
            if (!InitializeReflection())
            {
                _log?.Warn("[TextureLoader] Cannot inject textures - XNA reflection not ready");
                return 0;
            }

            int loaded = 0;
            int placeholders = 0;
            foreach (var fullId in ItemRegistry.AllIds)
            {
                int runtimeType = ItemRegistry.GetRuntimeType(fullId);
                if (runtimeType < 0) continue;

                var def = ItemRegistry.GetDefinitionById(fullId);
                if (def == null) continue;

                // Try to load custom texture file
                bool hasTexture = false;
                if (!string.IsNullOrEmpty(def.Texture))
                {
                    int colonIdx = fullId.IndexOf(':');
                    if (colonIdx >= 0)
                    {
                        string modId = fullId.Substring(0, colonIdx);
                        string modFolder = ItemRegistry.GetModFolder(modId);
                        if (!string.IsNullOrEmpty(modFolder))
                        {
                            string texturePath = Path.Combine(modFolder, def.Texture.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(texturePath))
                            {
                                if (InjectTexture(runtimeType, texturePath))
                                {
                                    loaded++;
                                    hasTexture = true;
                                }
                            }
                            else
                            {
                                _log?.Warn($"[TextureLoader] Texture not found: {texturePath} for {fullId}");
                            }
                        }
                    }
                }

                // If no texture was loaded, inject a placeholder using a known-good vanilla item texture
                if (!hasTexture)
                {
                    if (InjectPlaceholderTexture(runtimeType))
                        placeholders++;
                }
            }

            _log?.Info($"[TextureLoader] Injected {loaded} textures, {placeholders} placeholders");
            return loaded + placeholders;
        }

        /// <summary>
        /// Load a PNG and inject it into TextureAssets.Item[runtimeType].
        /// </summary>
        private static bool InjectTexture(int runtimeType, string pngPath)
        {
            try
            {
                // Load Texture2D from file
                object texture;
                using (var stream = File.OpenRead(pngPath))
                {
                    texture = _fromStreamMethod.Invoke(null, new object[] { _graphicsDevice, stream });
                }

                if (texture == null)
                {
                    _log?.Warn($"[TextureLoader] FromStream returned null for {pngPath}");
                    return false;
                }

                // Wrap in Asset<Texture2D> and inject into TextureAssets.Item[runtimeType]
                var texAssetsType = typeof(Terraria.GameContent.TextureAssets);
                var itemField = texAssetsType.GetField("Item", BindingFlags.Public | BindingFlags.Static);
                if (itemField == null) return false;

                var itemArray = itemField.GetValue(null) as Array;
                if (itemArray == null || runtimeType >= itemArray.Length) return false;

                // Create an Asset<Texture2D> wrapper
                // ReLogic.Content.Asset<T> wraps the texture — we need to create one
                var assetType = itemArray.GetType().GetElementType();
                var asset = CreateAssetWrapper(assetType, texture);
                if (asset != null)
                {
                    itemArray.SetValue(asset, runtimeType);
                    _assetCache[runtimeType] = asset;
                    _log?.Debug($"[TextureLoader] Injected texture for type {runtimeType}: {Path.GetFileName(pngPath)}");
                    return true;
                }

                return false;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                _log?.Warn($"[TextureLoader] Failed to load {pngPath}: {tie.InnerException.Message}");
                return false;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[TextureLoader] Failed to load {pngPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Create an Asset wrapper around a Texture2D object.
        /// Asset<Texture2D> stores the value and reports it as loaded.
        /// </summary>
        private static object CreateAssetWrapper(Type assetType, object texture)
        {
            try
            {
                // Asset<T> has internal constructor: Asset(string name)
                var instance = Activator.CreateInstance(assetType, BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { $"TerrariaModder/CustomItem" }, null);

                if (instance == null)
                    instance = Activator.CreateInstance(assetType, true);

                if (instance == null) return null;

                // Find the Texture2D backing field by type rather than by name.
                // Name-based probing breaks across XNA/FNA versions; the field type is stable.
                FieldInfo valueField = null;
                foreach (var f in assetType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType == _texture2dType)
                    {
                        valueField = f;
                        break;
                    }
                }

                if (valueField != null)
                {
                    valueField.SetValue(instance, texture);
                    _log?.Debug($"[TextureLoader] Set value via {valueField.Name} (type-based discovery)");
                }

                // Mark as loaded — State property prevents LoadItem from trying to reload
                // Try auto-property backing field first, then manual field names
                var stateField = assetType.GetField("<State>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("state", BindingFlags.NonPublic | BindingFlags.Instance);

                if (stateField != null)
                {
                    if (stateField.FieldType.IsEnum)
                    {
                        var loadedValue = Enum.ToObject(stateField.FieldType, 2);
                        stateField.SetValue(instance, loadedValue);
                    }
                    else if (stateField.FieldType == typeof(int))
                    {
                        stateField.SetValue(instance, 2);
                    }
                    _log?.Debug($"[TextureLoader] Set state via {stateField.Name} (type: {stateField.FieldType.Name})");
                }
                else
                {
                    // Brute force: find any enum field that could be state
                    foreach (var field in assetType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (field.FieldType.IsEnum)
                        {
                            field.SetValue(instance, Enum.ToObject(field.FieldType, 2));
                            _log?.Debug($"[TextureLoader] Set state via brute-force field: {field.Name}");
                            break;
                        }
                    }
                }

                return instance;
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TextureLoader] Failed to create asset wrapper: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Inject a placeholder texture for a custom item that has no .png file.
        /// Copies the Asset from a known-good vanilla item (type 1 = Iron Shortsword)
        /// so that TextureAssets.Item[runtimeType].Value is never null.
        /// </summary>
        private static bool InjectPlaceholderTexture(int runtimeType)
        {
            try
            {
                var texAssetsType = typeof(Terraria.GameContent.TextureAssets);
                var itemField = texAssetsType.GetField("Item", BindingFlags.Public | BindingFlags.Static);
                if (itemField == null) return false;

                var itemArray = itemField.GetValue(null) as Array;
                if (itemArray == null || runtimeType >= itemArray.Length) return false;

                // Use item type 1 (Iron Shortsword) as placeholder — guaranteed to have a loaded texture
                var placeholder = itemArray.GetValue(1);
                if (placeholder == null) return false;

                itemArray.SetValue(placeholder, runtimeType);
                _assetCache[runtimeType] = placeholder;
                _log?.Debug($"[TextureLoader] Placeholder texture for type {runtimeType}");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TextureLoader] Failed to set placeholder for type {runtimeType}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Verify that all cached Asset objects are still present in TextureAssets.Item[].
        /// Returns true if all injected slots still hold the expected Asset reference.
        /// Returns false if any slot was overwritten (re-injection needed).
        /// </summary>
        public static bool VerifyTextures()
        {
            if (_assetCache.Count == 0) return true;

            try
            {
                var texAssetsType = typeof(Terraria.GameContent.TextureAssets);
                var itemField = texAssetsType.GetField("Item", BindingFlags.Public | BindingFlags.Static);
                if (itemField == null) return true;

                var itemArray = itemField.GetValue(null) as Array;
                if (itemArray == null) return true;

                foreach (var kvp in _assetCache)
                {
                    if (kvp.Key < 0 || kvp.Key >= itemArray.Length) continue;
                    // Reference equality: any overwrite (even to same value) resets the reference
                    if (!ReferenceEquals(itemArray.GetValue(kvp.Key), kvp.Value))
                        return false;
                }
                return true;
            }
            catch
            {
                return true; // Can't verify — assume stable
            }
        }

        /// <summary>
        /// Re-inject cached Asset objects into TextureAssets.Item[].
        /// Fast path (no disk I/O) — used by OnUpdate retry loop to survive
        /// async texture loader overwrites from AssetInitializer.LoadTextures().
        /// </summary>
        public static int ReinjectCachedTextures()
        {
            if (_assetCache.Count == 0) return 0;

            try
            {
                var texAssetsType = typeof(Terraria.GameContent.TextureAssets);
                var itemField = texAssetsType.GetField("Item", BindingFlags.Public | BindingFlags.Static);
                if (itemField == null) return 0;

                var itemArray = itemField.GetValue(null) as Array;
                if (itemArray == null) return 0;

                int count = 0;
                foreach (var kvp in _assetCache)
                {
                    if (kvp.Key >= 0 && kvp.Key < itemArray.Length)
                    {
                        itemArray.SetValue(kvp.Value, kvp.Key);
                        count++;
                    }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        private static bool InitializeReflection()
        {
            if (_reflectionFailed) return false;
            if (_reflectionReady) return true;

            try
            {
                var mainType = typeof(Terraria.Main);
                var assembly = mainType.Assembly;

                // Find Texture2D
                _texture2dType = assembly.GetType("Microsoft.Xna.Framework.Graphics.Texture2D");
                if (_texture2dType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _texture2dType = asm.GetType("Microsoft.Xna.Framework.Graphics.Texture2D");
                        if (_texture2dType != null) break;
                    }
                }

                if (_texture2dType == null) { _reflectionFailed = true; return false; }

                // Find GraphicsDevice type
                var gdType = _texture2dType.Assembly.GetType("Microsoft.Xna.Framework.Graphics.GraphicsDevice");
                if (gdType == null) { _reflectionFailed = true; return false; }

                // Find FromStream
                _fromStreamMethod = _texture2dType.GetMethod("FromStream",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { gdType, typeof(Stream) }, null);
                if (_fromStreamMethod == null) { _reflectionFailed = true; return false; }

                // Get GraphicsDevice
                var graphicsProp = mainType.GetProperty("graphics", BindingFlags.Public | BindingFlags.Static);
                if (graphicsProp != null)
                {
                    var gm = graphicsProp.GetValue(null);
                    if (gm != null)
                    {
                        var gdProp = gm.GetType().GetProperty("GraphicsDevice");
                        _graphicsDevice = gdProp?.GetValue(gm);
                    }
                }

                if (_graphicsDevice == null)
                {
                    var instField = mainType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    var inst = instField?.GetValue(null);
                    if (inst != null)
                    {
                        var gdProp = inst.GetType().GetProperty("GraphicsDevice");
                        _graphicsDevice = gdProp?.GetValue(inst);
                    }
                }

                if (_graphicsDevice == null) return false; // Not ready yet, try again later

                _reflectionReady = true;
                _log?.Info("[TextureLoader] XNA reflection initialized");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[TextureLoader] Reflection init failed: {ex.Message}");
                _reflectionFailed = true;
                return false;
            }
        }
    }
}
