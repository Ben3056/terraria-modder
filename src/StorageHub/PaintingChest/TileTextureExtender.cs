using System;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace StorageHub.PaintingChest
{
    /// <summary>
    /// Extends the chest spritesheet (Tiles_21) to include our custom style 69.
    /// Creates a blue-recolored chest texture at the style 69 position.
    ///
    /// Chest spritesheet layout: each style is 36px wide (2 tiles × 18px), arranged left-to-right.
    /// Style 69 = column starting at frameX 2484. Requires ~2520px width (HiDef max = 8192px).
    /// </summary>
    internal static class TileTextureExtender
    {
        private static ILogger _log;
        private static bool _tileExtended;
        private static bool _itemExtended;
        private static bool _failed;
        private static int _nullRefCount;

        public static bool IsTileExtended => _tileExtended;

        private static GraphicsDevice _graphicsDevice;

        private const int STYLE_WIDTH = 36;  // 2 tiles × 18px per tile
        private const int STYLE_HEIGHT = 38; // 2 tiles × 18px + 2px padding
        private const int SOURCE_STYLE = 1;  // Gold chest — base texture to recolor

        public static void Initialize(ILogger logger)
        {
            _log = logger;
        }

        public static void TryExtend()
        {
            if (_failed) return;
            if (_tileExtended && _itemExtended) return;

            try
            {
                if (_graphicsDevice == null)
                {
                    _graphicsDevice = Main.instance?.GraphicsDevice;
                    if (_graphicsDevice == null) return;
                }

                if (!_tileExtended)
                    _tileExtended = ExtendChestSpritesheet();
                if (!_itemExtended)
                    _itemExtended = GenerateItemTexture();
            }
            catch (Exception ex)
            {
                // NullRef at startup is a timing issue (GraphicsDevice not ready) — suppress after first log
                if (ex is NullReferenceException)
                {
                    if (_nullRefCount++ == 0)
                        _log?.Debug($"TileTextureExtender: waiting for GraphicsDevice (NullRef, will retry)");
                }
                else
                {
                    _log?.Error($"TileTextureExtender failed: {ex.GetType().Name}: {ex.Message}");
                    _failed = true;
                }
            }
        }

        private static Texture2D GetTexture2DFromAsset(object asset)
        {
            if (asset == null) return null;
            return asset.GetType().GetProperty("Value")?.GetValue(asset) as Texture2D;
        }

        private static Texture2D ForceLoadAsset(string assetName)
        {
            try
            {
                object repo = typeof(Main).GetField("Assets", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? typeof(Main).GetProperty("Assets", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (repo == null) return null;

                MethodInfo requestMethod = null;
                foreach (var m in repo.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "Request" && m.IsGenericMethod) { requestMethod = m; break; }
                }
                if (requestMethod == null) return null;

                var requestGeneric = requestMethod.MakeGenericMethod(typeof(Texture2D));
                var modeType = requestGeneric.GetParameters()[1].ParameterType;
                var immediateLoad = Enum.ToObject(modeType, 1);

                var asset = requestGeneric.Invoke(repo, new object[] { assetName, immediateLoad });
                return GetTexture2DFromAsset(asset);
            }
            catch (Exception ex) { _log?.Debug($"ForceLoadAsset failed for {assetName}: {ex.Message}"); return null; }
        }

        private static bool ExtendChestSpritesheet()
        {
            var tileArray = typeof(TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as Array;
            if (tileArray == null) return false;
            if (PaintingChestManager.TILE_TYPE >= tileArray.Length) return false;

            var existingAsset = tileArray.GetValue(PaintingChestManager.TILE_TYPE);
            var existingTexture = ForceLoadAsset("Images/Tiles_" + PaintingChestManager.TILE_TYPE);
            if (existingTexture == null) return false;

            int origWidth = existingTexture.Width;
            int origHeight = existingTexture.Height;

            // Calculate required width for our style
            int requiredWidth = (PaintingChestManager.OUR_PLACE_STYLE + 1) * STYLE_WIDTH;

            if (origWidth >= requiredWidth)
            {
                // Spritesheet already wide enough — just recolor our style position
                var pixels = new uint[origWidth * origHeight];
                existingTexture.GetData(pixels);

                // Copy source style pixels to our style position and recolor
                int srcX = SOURCE_STYLE * STYLE_WIDTH;
                int dstX = PaintingChestManager.OUR_PLACE_STYLE * STYLE_WIDTH;
                CopyAndRecolorStyle(pixels, origWidth, origHeight, srcX, dstX);

                var newTexture = new Texture2D(_graphicsDevice, origWidth, origHeight);
                newTexture.SetData(pixels);
                ReplaceTexture(tileArray, existingAsset, newTexture, origWidth, origHeight);
                return true;
            }
            else
            {
                // Need to extend width
                var origPixels = new uint[origWidth * origHeight];
                existingTexture.GetData(origPixels);

                var newPixels = new uint[requiredWidth * origHeight];
                // Copy original data row by row (width changes)
                for (int row = 0; row < origHeight; row++)
                    Array.Copy(origPixels, row * origWidth, newPixels, row * requiredWidth, origWidth);

                // Copy source style to our style position and recolor
                int srcX = SOURCE_STYLE * STYLE_WIDTH;
                int dstX = PaintingChestManager.OUR_PLACE_STYLE * STYLE_WIDTH;
                CopyAndRecolorStyle(newPixels, requiredWidth, origHeight, srcX, dstX);

                var newTexture = new Texture2D(_graphicsDevice, requiredWidth, origHeight);
                newTexture.SetData(newPixels);
                ReplaceTexture(tileArray, existingAsset, newTexture, requiredWidth, origHeight);
                return true;
            }
        }

        private static void CopyAndRecolorStyle(uint[] pixels, int width, int height, int srcX, int dstX)
        {
            // Copy the FULL column height (all 3 animation frames: closed, half-open, open)
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < STYLE_WIDTH; col++)
                {
                    int srcIdx = row * width + srcX + col;
                    int dstIdx = row * width + dstX + col;
                    if (srcIdx >= pixels.Length || dstIdx >= pixels.Length) continue;

                    uint pixel = pixels[srcIdx];
                    uint alpha = (pixel >> 24) & 0xFF;
                    if (alpha == 0) { pixels[dstIdx] = 0; continue; }

                    // XNA ABGR: bits 0-7=R, 8-15=G, 16-23=B, 24-31=A
                    uint r = pixel & 0xFF;
                    uint g = (pixel >> 8) & 0xFF;
                    uint b = (pixel >> 16) & 0xFF;

                    // Blue-purple tint for mysterious appearance
                    uint newR = r * 60 / 255;
                    uint newG = g * 40 / 255;
                    uint newB = (uint)Math.Min(255, b * 180 / 255 + 80);

                    pixels[dstIdx] = (alpha << 24) | (newB << 16) | (newG << 8) | newR;
                }
            }
        }

        private static void ReplaceTexture(Array tileArray, object existingAsset, Texture2D newTexture, int w, int h)
        {
            var originalName = GetAssetName(existingAsset) ?? "Images/Tiles_" + PaintingChestManager.TILE_TYPE;
            var newAsset = CreateAssetWrapper(existingAsset.GetType(), newTexture, originalName);
            if (newAsset != null)
            {
                tileArray.SetValue(newAsset, PaintingChestManager.TILE_TYPE);
                _log?.Info($"Extended chest spritesheet to {w}x{h} (style {PaintingChestManager.OUR_PLACE_STYLE})");
            }
        }

        private static bool GenerateItemTexture()
        {
            int ourType = ItemRegistry.GetRuntimeType(PaintingChestManager.FULL_ITEM_ID);
            if (ourType < 0) return false;

            var itemArray = typeof(TextureAssets).GetField("Item", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as Array;
            if (itemArray == null) return false;

            // Use gold chest item (306) as base for our item texture
            const int SOURCE_ITEM_ID = 306; // Gold Chest
            if (SOURCE_ITEM_ID >= itemArray.Length || ourType >= itemArray.Length) return false;

            var srcTexture = ForceLoadAsset("Images/Item_" + SOURCE_ITEM_ID);
            if (srcTexture == null) return false;

            int itemW = srcTexture.Width;
            int itemH = srcTexture.Height;
            var pixels = new uint[itemW * itemH];
            srcTexture.GetData(pixels);

            // Apply blue-purple recolor
            for (int i = 0; i < pixels.Length; i++)
            {
                uint pixel = pixels[i];
                uint alpha = (pixel >> 24) & 0xFF;
                if (alpha == 0) continue;
                uint r = pixel & 0xFF;
                uint g = (pixel >> 8) & 0xFF;
                uint b = (pixel >> 16) & 0xFF;
                uint newR = r * 60 / 255;
                uint newG = g * 40 / 255;
                uint newB = (uint)Math.Min(255, b * 180 / 255 + 80);
                pixels[i] = (alpha << 24) | (newB << 16) | (newG << 8) | newR;
            }

            var newTexture = new Texture2D(_graphicsDevice, itemW, itemH);
            newTexture.SetData(pixels);

            var existingItemAsset = itemArray.GetValue(SOURCE_ITEM_ID);
            var originalItemName = GetAssetName(existingItemAsset) ?? "Images/Item_" + SOURCE_ITEM_ID;
            var newAsset = CreateAssetWrapper(existingItemAsset.GetType(), newTexture, originalItemName);
            if (newAsset != null)
            {
                itemArray.SetValue(newAsset, ourType);
                _log?.Info($"Generated mysterious chest item texture (type {ourType})");
                return true;
            }
            return false;
        }

        private static string GetAssetName(object asset)
        {
            return asset?.GetType().GetProperty("Name")?.GetValue(asset) as string;
        }

        private static object CreateAssetWrapper(Type assetType, Texture2D texture, string assetName)
        {
            try
            {
                var instance = Activator.CreateInstance(assetType, BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { assetName }, null)
                    ?? Activator.CreateInstance(assetType, true);
                if (instance == null) return null;

                var valueField = assetType.GetField("<Value>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("ownValue", BindingFlags.NonPublic | BindingFlags.Instance);
                if (valueField == null) return null;
                valueField.SetValue(instance, texture);

                var stateField = assetType.GetField("<State>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("state", BindingFlags.NonPublic | BindingFlags.Instance);
                if (stateField != null)
                {
                    if (stateField.FieldType.IsEnum) stateField.SetValue(instance, Enum.ToObject(stateField.FieldType, 2));
                    else if (stateField.FieldType == typeof(int)) stateField.SetValue(instance, 2);
                }
                else
                {
                    foreach (var field in assetType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (field.FieldType.IsEnum) { field.SetValue(instance, Enum.ToObject(field.FieldType, 2)); break; }
                    }
                }

                return instance;
            }
            catch (Exception ex) { _log?.Warn($"CreateAssetWrapper failed: {ex.Message}"); return null; }
        }
    }
}
