using System;

namespace TerrariaModder.Core.Logging
{
    /// <summary>
    /// Per-mod logger implementation. Writes to both console and shared log file.
    /// </summary>
    public class ModLogger : ILogger
    {
        public string ModId { get; }
        public LogLevel MinLevel { get; set; } = LogLevel.Info;

        public ModLogger(string modId)
        {
            ModId = modId;
        }

        public void Debug(string message) => Log(LogLevel.Debug, message);
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);

        public void Error(string message, Exception ex)
        {
            Log(LogLevel.Error, message);
            if (ex != null)
            {
                WriteRaw($"    {ex.GetType().Name}: {ex.Message}");
                if (ex.StackTrace != null)
                {
                    foreach (var line in ex.StackTrace.Split('\n'))
                    {
                        WriteRaw($"    {line.Trim()}");
                    }
                }
            }
        }

        private void Log(LogLevel level, string message)
        {
            if (level < MinLevel) return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string levelStr = level.ToString().ToUpper().PadRight(5);
            string formatted = $"[{timestamp}] [{levelStr}] [{ModId}] {message}";

            // Console write is best-effort - must not prevent file/buffer writes
            if (!LogManager.SuppressConsole)
            {
                try { WriteToConsole(level, formatted); }
                catch { }
            }

            LogManager.WriteToSharedFile(formatted);
            LogManager.AddRecentLog(ModId, level, message);
        }

        private void WriteRaw(string text)
        {
            try { Console.WriteLine(text); }
            catch { }
            LogManager.WriteToSharedFile(text);
        }

        private void WriteToConsole(LogLevel level, string message)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = GetColorForLevel(level);
            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }

        private static ConsoleColor GetColorForLevel(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return ConsoleColor.Gray;
                case LogLevel.Info: return ConsoleColor.White;
                case LogLevel.Warn: return ConsoleColor.Yellow;
                case LogLevel.Error: return ConsoleColor.Red;
                default: return ConsoleColor.White;
            }
        }
    }
}
