using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TerrariaModder.Core.Logging;

namespace DebugTools
{
    /// <summary>
    /// Thread-safe virtual input state manager.
    /// HTTP thread enqueues input commands, game thread reads state each frame.
    ///
    /// Safety guarantees:
    /// - All indefinite presses auto-release after MaxIndefiniteHoldMs (30 seconds)
    /// - All timed holds are capped at MaxHoldDurationMs (10 seconds)
    /// - ReleaseAll() is called on world unload and mod unload
    /// - Timer comparisons use unchecked subtraction to handle Environment.TickCount overflow
    /// </summary>
    public static class VirtualInputManager
    {
        private static ILogger _log;
        private static bool _initialized;

        /// <summary>Maximum duration for a timed hold (10 seconds).</summary>
        public const int MaxHoldDurationMs = 10_000;

        /// <summary>Maximum time an indefinite press can stay active (30 seconds).</summary>
        public const int MaxIndefiniteHoldMs = 30_000;

        // Currently pressed virtual keys (key name -> true)
        private static readonly ConcurrentDictionary<string, bool> _pressedKeys =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Timed holds: key name -> release tick (Environment.TickCount)
        private static readonly ConcurrentDictionary<string, int> _timedHolds =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Virtual mouse state
        private static volatile int _mouseX = -1;
        private static volatile int _mouseY = -1;
        private static volatile bool _mouseLeftDown;
        private static volatile bool _mouseRightDown;
        private static volatile bool _mouseMiddleDown;
        private static volatile int _scrollDelta;

        // Whether virtual mouse position is set (vs using real mouse)
        private static volatile bool _mousePositionActive;

        // Active trigger overrides (trigger name -> true)
        private static readonly ConcurrentDictionary<string, bool> _activeTriggers =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Timed trigger holds
        private static readonly ConcurrentDictionary<string, int> _timedTriggerHolds =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether any virtual input is currently active.</summary>
        public static bool HasActiveInput =>
            !_pressedKeys.IsEmpty || !_activeTriggers.IsEmpty ||
            _mouseLeftDown || _mouseRightDown || _mouseMiddleDown ||
            _mousePositionActive || _scrollDelta != 0;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _initialized = true;
            _log?.Info("[VirtualInput] Initialized");
        }

        /// <summary>
        /// Press a key by XNA key name (e.g., "W", "Space", "A").
        /// Key auto-releases after MaxIndefiniteHoldMs (30s) as a safety net.
        /// </summary>
        public static void PressKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _pressedKeys[key] = true;
            // Safety: indefinite presses get a max hold time to prevent stuck keys
            _timedHolds[key] = Environment.TickCount + MaxIndefiniteHoldMs;
        }

        /// <summary>
        /// Release a previously pressed key.
        /// </summary>
        public static void ReleaseKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _pressedKeys.TryRemove(key, out _);
            _timedHolds.TryRemove(key, out _);
        }

        /// <summary>
        /// Hold a key for a specified duration, then auto-release.
        /// Duration is capped at MaxHoldDurationMs (10 seconds).
        /// </summary>
        public static void HoldKey(string key, int durationMs)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (durationMs <= 0) durationMs = 100;
            if (durationMs > MaxHoldDurationMs) durationMs = MaxHoldDurationMs;
            _pressedKeys[key] = true;
            _timedHolds[key] = Environment.TickCount + durationMs;
        }

        /// <summary>
        /// Set a trigger directly by name (e.g., "Up", "Jump", "MouseLeft").
        /// Trigger auto-releases after MaxIndefiniteHoldMs (30s) as a safety net.
        /// </summary>
        public static void SetTrigger(string triggerName)
        {
            if (string.IsNullOrEmpty(triggerName)) return;
            _activeTriggers[triggerName] = true;
            // Safety: indefinite triggers get a max hold time to prevent stuck input
            _timedTriggerHolds[triggerName] = Environment.TickCount + MaxIndefiniteHoldMs;
        }

        /// <summary>
        /// Clear a trigger.
        /// </summary>
        public static void ClearTrigger(string triggerName)
        {
            if (string.IsNullOrEmpty(triggerName)) return;
            _activeTriggers.TryRemove(triggerName, out _);
            _timedTriggerHolds.TryRemove(triggerName, out _);
        }

        /// <summary>
        /// Hold a trigger for a specified duration, then auto-release.
        /// Duration is capped at MaxHoldDurationMs (10 seconds).
        /// </summary>
        public static void HoldTrigger(string triggerName, int durationMs)
        {
            if (string.IsNullOrEmpty(triggerName)) return;
            if (durationMs <= 0) durationMs = 100;
            if (durationMs > MaxHoldDurationMs) durationMs = MaxHoldDurationMs;
            _activeTriggers[triggerName] = true;
            _timedTriggerHolds[triggerName] = Environment.TickCount + durationMs;
        }

        /// <summary>
        /// Set virtual mouse position (screen coordinates).
        /// </summary>
        public static void SetMousePosition(int x, int y)
        {
            _mouseX = x;
            _mouseY = y;
            _mousePositionActive = true;
        }

        /// <summary>
        /// Clear virtual mouse position (revert to real mouse).
        /// </summary>
        public static void ClearMousePosition()
        {
            _mousePositionActive = false;
        }

        /// <summary>
        /// Press a mouse button.
        /// Button auto-releases after MaxIndefiniteHoldMs (30s) as a safety net.
        /// </summary>
        public static void MouseDown(string button)
        {
            var btn = (button ?? "").ToLowerInvariant();
            switch (btn)
            {
                case "left": _mouseLeftDown = true; break;
                case "right": _mouseRightDown = true; break;
                case "middle": _mouseMiddleDown = true; break;
                default: return;
            }
            // Safety: use __mouse_ sentinel key so Update() auto-releases the button
            string releaseKey = $"__mouse_{btn}";
            _pressedKeys[releaseKey] = true;
            _timedHolds[releaseKey] = Environment.TickCount + MaxIndefiniteHoldMs;
        }

        /// <summary>
        /// Release a mouse button.
        /// </summary>
        public static void MouseUp(string button)
        {
            var btn = (button ?? "").ToLowerInvariant();
            switch (btn)
            {
                case "left": _mouseLeftDown = false; break;
                case "right": _mouseRightDown = false; break;
                case "middle": _mouseMiddleDown = false; break;
                default: return;
            }
            // Clear auto-release sentinel
            string releaseKey = $"__mouse_{btn}";
            _pressedKeys.TryRemove(releaseKey, out _);
            _timedHolds.TryRemove(releaseKey, out _);
        }

        /// <summary>
        /// Click a mouse button at a position (press + auto-release after duration).
        /// Duration is capped at MaxHoldDurationMs.
        /// </summary>
        public static void ClickMouse(int x, int y, string button, int durationMs = 100)
        {
            if (durationMs <= 0) durationMs = 100;
            if (durationMs > MaxHoldDurationMs) durationMs = MaxHoldDurationMs;

            SetMousePosition(x, y);
            MouseDown(button);

            // Set trigger for mouse click
            string triggerName = null;
            switch ((button ?? "").ToLowerInvariant())
            {
                case "left": triggerName = "MouseLeft"; break;
                case "right": triggerName = "MouseRight"; break;
            }
            if (triggerName != null)
                HoldTrigger(triggerName, durationMs);

            // Schedule auto-release of button and position
            string releaseKey = $"__mouse_{button}";
            _timedHolds[releaseKey] = Environment.TickCount + durationMs;
            _pressedKeys[releaseKey] = true;
        }

        /// <summary>
        /// Set scroll wheel delta (applied once, then cleared).
        /// </summary>
        public static void ScrollMouse(int delta)
        {
            _scrollDelta = delta;
        }

        /// <summary>
        /// Release all virtual input. Called on world unload, mod unload, and manually.
        /// </summary>
        public static void ReleaseAll()
        {
            _pressedKeys.Clear();
            _timedHolds.Clear();
            _activeTriggers.Clear();
            _timedTriggerHolds.Clear();
            _mouseLeftDown = false;
            _mouseRightDown = false;
            _mouseMiddleDown = false;
            _mousePositionActive = false;
            _scrollDelta = 0;

            // Clear action-level state to prevent stale ref counts
            VirtualInputActions.ReleaseAll();
        }

        /// <summary>
        /// Called each frame from the input patch to handle timed releases.
        /// Must run on game thread.
        /// Uses unchecked subtraction for Environment.TickCount overflow safety.
        /// </summary>
        public static void Update()
        {
            if (!_initialized) return;

            int now = Environment.TickCount;

            // Check timed key holds (overflow-safe: (now - target) >= 0 means expired)
            foreach (var kvp in _timedHolds.ToArray())
            {
                if (unchecked(now - kvp.Value) >= 0)
                {
                    _timedHolds.TryRemove(kvp.Key, out _);

                    // Check for mouse button auto-release
                    if (kvp.Key.StartsWith("__mouse_"))
                    {
                        string btn = kvp.Key.Substring(8);
                        MouseUp(btn);
                        ClearMousePosition();
                        _pressedKeys.TryRemove(kvp.Key, out _);
                    }
                    else
                    {
                        _pressedKeys.TryRemove(kvp.Key, out _);
                    }
                }
            }

            // Check timed trigger holds (overflow-safe)
            foreach (var kvp in _timedTriggerHolds.ToArray())
            {
                if (unchecked(now - kvp.Value) >= 0)
                {
                    _timedTriggerHolds.TryRemove(kvp.Key, out _);
                    _activeTriggers.TryRemove(kvp.Key, out _);
                }
            }

            // Scroll delta is now cleared in GetMouseState() after being consumed by InjectMouse
        }

        /// <summary>
        /// Get all currently active virtual trigger names.
        /// Called from game thread by the input patch.
        /// </summary>
        public static IEnumerable<string> GetActiveTriggers()
        {
            return _activeTriggers.Keys;
        }

        /// <summary>
        /// Get all currently pressed virtual key names (XNA Keys enum names).
        /// Called from game thread by the input patch.
        /// </summary>
        public static IEnumerable<string> GetPressedKeys()
        {
            return _pressedKeys.Keys.Where(k => !k.StartsWith("__"));
        }

        /// <summary>
        /// Get virtual mouse state for the input patch.
        /// </summary>
        public static (bool active, int x, int y, bool left, bool right, bool middle, int scroll) GetMouseState()
        {
            int scroll = _scrollDelta;
            _scrollDelta = 0; // Consume scroll delta after reading
            return (_mousePositionActive, _mouseX, _mouseY,
                    _mouseLeftDown, _mouseRightDown, _mouseMiddleDown, scroll);
        }

        /// <summary>
        /// Get current state as a JSON-friendly structure for the HTTP API.
        /// </summary>
        public static (string[] keys, string[] triggers, bool mouseActive, int mouseX, int mouseY,
                        bool mouseLeft, bool mouseRight, bool mouseMiddle) GetState()
        {
            var keys = _pressedKeys.Keys.Where(k => !k.StartsWith("__")).ToArray();
            var triggers = _activeTriggers.Keys.ToArray();
            return (keys, triggers, _mousePositionActive, _mouseX, _mouseY,
                    _mouseLeftDown, _mouseRightDown, _mouseMiddleDown);
        }
    }
}
