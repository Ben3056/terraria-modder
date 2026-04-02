using System;
using System.IO;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Identity
{
    /// <summary>
    /// Manages the per-install stable GUID used for multiplayer identity.
    /// The GUID is generated on first run and persisted to core/identity.json.
    /// Stable across character renames, world changes, and reconnects.
    /// </summary>
    public static class IdentityService
    {
        private static string _installId;
        private static ILogger _log;

        /// <summary>
        /// Stable install GUID. Never null after Initialize().
        /// </summary>
        public static string InstallId => _installId;

        /// <summary>
        /// Initialize and load (or generate) the install GUID.
        /// </summary>
        public static void Initialize(string corePath, ILogger log)
        {
            _log = log;
            string path = Path.Combine(corePath, "identity.json");

            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    string id = ExtractGuid(json);
                    if (!string.IsNullOrEmpty(id))
                    {
                        _installId = id;
                        _log?.Info($"[Identity] Loaded install ID: {_installId}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[Identity] Failed to read identity.json: {ex.Message}");
                }
            }

            // Generate new GUID
            _installId = Guid.NewGuid().ToString("D");
            _log?.Info($"[Identity] Generated new install ID: {_installId}");
            Save(path);
        }

        private static void Save(string path)
        {
            try
            {
                string json = $"{{\n  \"installId\": \"{_installId}\"\n}}\n";
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Identity] Failed to save identity.json: {ex.Message}");
            }
        }

        private static string ExtractGuid(string json)
        {
            // Simple extraction: find "installId": "..."
            int idx = json.IndexOf("\"installId\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int colon = json.IndexOf(':', idx);
            if (colon < 0) return null;

            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;

            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;

            string val = json.Substring(q1 + 1, q2 - q1 - 1);
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
    }
}
