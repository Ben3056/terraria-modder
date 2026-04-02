using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using Terraria;

namespace DebugTools
{
    /// <summary>
    /// Captures the game screen as PNG or JPEG bytes.
    /// Uses a Harmony postfix on Main.DoDraw to grab the backbuffer at the right time
    /// (after rendering, before Present clears it). The last captured frame is stored
    /// in a buffer that HTTP handlers can read from any thread.
    /// </summary>
    public static class ScreenCapture
    {
        private static object _graphicsDevice;
        private static MethodInfo _getDataMethod;
        private static PropertyInfo _viewportProp;
        private static PropertyInfo _vpWidthProp;
        private static PropertyInfo _vpHeightProp;
        private static bool _initDone;

        // Last captured frame — written by draw-thread postfix, read by HTTP thread
        private static byte[] _lastFramePixels;
        private static int _lastFrameWidth;
        private static int _lastFrameHeight;
        private static readonly object _frameLock = new object();

        // Capture control — only capture when someone requests it (saves perf)
        private static volatile bool _captureRequested;
        private static ManualResetEventSlim _captureReady = new ManualResetEventSlim(false);

        /// <summary>
        /// Called from Harmony postfix on Main.DoDraw (draw thread).
        /// Grabs the backbuffer while it still has rendered content.
        /// </summary>
        public static void OnPostDraw()
        {
            if (!_captureRequested) return;
            _captureRequested = false;

            try
            {
                if (!EnsureInit()) return;

                var viewport = _viewportProp.GetValue(_graphicsDevice, null);
                int w = (int)_vpWidthProp.GetValue(viewport, null);
                int h = (int)_vpHeightProp.GetValue(viewport, null);
                if (w <= 0 || h <= 0) return;

                var pixels = new uint[w * h];
                _getDataMethod.Invoke(_graphicsDevice, new object[] { pixels });

                // Convert ABGR → ARGB in-place
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint abgr = pixels[i];
                    uint a = (abgr >> 24) & 0xFF;
                    uint b = (abgr >> 16) & 0xFF;
                    uint g = (abgr >> 8) & 0xFF;
                    uint r = abgr & 0xFF;
                    pixels[i] = (a << 24) | (r << 16) | (g << 8) | b;
                }

                // Store as raw bytes for the HTTP thread to encode
                var raw = new byte[pixels.Length * 4];
                Buffer.BlockCopy(pixels, 0, raw, 0, raw.Length);

                lock (_frameLock)
                {
                    _lastFramePixels = raw;
                    _lastFrameWidth = w;
                    _lastFrameHeight = h;
                }
                _captureReady.Set();
            }
            catch
            {
                _captureReady.Set(); // unblock waiter even on failure
            }
        }

        /// <summary>
        /// Request a screenshot and wait for the draw thread to capture it.
        /// Safe to call from any thread (HTTP handler).
        /// </summary>
        public static byte[] CaptureScreen(int maxWidth = 0, string format = "png", int jpegQuality = 80)
        {
            try
            {
                // Request capture and wait for draw thread to deliver
                _captureReady.Reset();
                _captureRequested = true;

                // Wait up to 500ms (should take at most 1-2 frames = 16-33ms)
                if (!_captureReady.Wait(500))
                    return null;

                byte[] raw;
                int width, height;
                lock (_frameLock)
                {
                    raw = _lastFramePixels;
                    width = _lastFrameWidth;
                    height = _lastFrameHeight;
                }

                if (raw == null || width <= 0 || height <= 0) return null;

                using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    var bmpData = bmp.LockBits(
                        new Rectangle(0, 0, width, height),
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format32bppArgb);

                    // Copy pre-converted ARGB data directly
                    System.Runtime.InteropServices.Marshal.Copy(raw, 0, bmpData.Scan0, Math.Min(raw.Length, bmpData.Stride * height));
                    bmp.UnlockBits(bmpData);

                    Bitmap output = bmp;
                    bool needsDispose = false;
                    if (maxWidth > 0 && maxWidth < width)
                    {
                        int newHeight = (int)((float)height / width * maxWidth);
                        output = new Bitmap(bmp, new Size(maxWidth, newHeight));
                        needsDispose = true;
                    }

                    try
                    {
                        using (var ms = new MemoryStream())
                        {
                            if (format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ||
                                format.Equals("jpg", StringComparison.OrdinalIgnoreCase))
                            {
                                var encoder = GetEncoder(ImageFormat.Jpeg);
                                if (encoder != null)
                                {
                                    var encoderParams = new EncoderParameters(1);
                                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)jpegQuality);
                                    output.Save(ms, encoder, encoderParams);
                                }
                                else
                                {
                                    output.Save(ms, ImageFormat.Jpeg);
                                }
                            }
                            else
                            {
                                output.Save(ms, ImageFormat.Png);
                            }
                            return ms.ToArray();
                        }
                    }
                    finally
                    {
                        if (needsDispose) output.Dispose();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool EnsureInit()
        {
            if (_initDone) return _graphicsDevice != null;
            _initDone = true;

            try
            {
                var mainInstance = Main.instance;
                if (mainInstance == null) return false;

                var gameProp = mainInstance.GetType().GetProperty("GraphicsDevice",
                    BindingFlags.Public | BindingFlags.Instance);
                if (gameProp == null) return false;

                _graphicsDevice = gameProp.GetValue(mainInstance, null);
                if (_graphicsDevice == null) return false;

                var gdType = _graphicsDevice.GetType();

                _viewportProp = gdType.GetProperty("Viewport");
                if (_viewportProp != null)
                {
                    var vpType = _viewportProp.PropertyType;
                    _vpWidthProp = vpType.GetProperty("Width");
                    _vpHeightProp = vpType.GetProperty("Height");
                }

                if (_vpWidthProp == null || _vpHeightProp == null)
                {
                    _graphicsDevice = null;
                    return false;
                }

                foreach (var m in gdType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "GetBackBufferData" && m.IsGenericMethod &&
                        m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1)
                    {
                        _getDataMethod = m.MakeGenericMethod(typeof(uint));
                        break;
                    }
                }

                if (_getDataMethod == null)
                {
                    _graphicsDevice = null;
                    return false;
                }

                return true;
            }
            catch
            {
                _graphicsDevice = null;
                return false;
            }
        }

        public static void Cleanup()
        {
            _captureReady?.Dispose();
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == format.Guid) return codec;
            }
            return null;
        }
    }
}
