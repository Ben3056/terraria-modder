using System;
using System.Collections.Generic;

namespace TerrariaModder.Core.Debug
{
    /// <summary>
    /// Ring buffer of game events for reactive testing.
    /// Mods call Emit() to log events; HTTP API serves via GET /api/events.
    /// Thread-safe. Capacity 500 events.
    /// </summary>
    public static class EventLog
    {
        private const int Capacity = 500;
        private static readonly EventEntry[] _buffer = new EventEntry[Capacity];
        private static int _head;
        private static int _count;
        private static long _nextId = 1;
        private static readonly object _lock = new object();

        public struct EventEntry
        {
            public long Id;
            public long TimestampMs;
            public string Source;
            public string Type;
            public string Data;
        }

        /// <summary>
        /// Emit a game event. Call from any thread.
        /// </summary>
        /// <param name="source">Mod ID or system name (e.g., "storage-hub", "combat")</param>
        /// <param name="type">Event type (e.g., "deposit", "damage", "death")</param>
        /// <param name="data">Freeform data string (JSON or plain text)</param>
        public static void Emit(string source, string type, string data = null)
        {
            lock (_lock)
            {
                _buffer[_head] = new EventEntry
                {
                    Id = _nextId++,
                    TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Source = source,
                    Type = type,
                    Data = data
                };
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        /// <summary>
        /// Get recent events, optionally filtered.
        /// </summary>
        /// <param name="sinceId">Only return events with ID > sinceId (0 for all)</param>
        /// <param name="source">Filter by source (null for all)</param>
        /// <param name="limit">Max events to return</param>
        public static List<EventEntry> GetEvents(long sinceId = 0, string source = null, int limit = 50)
        {
            var result = new List<EventEntry>();
            lock (_lock)
            {
                int start = (_head - _count + Capacity) % Capacity;
                for (int i = 0; i < _count && result.Count < limit; i++)
                {
                    int idx = (start + i) % Capacity;
                    var e = _buffer[idx];
                    if (e.Id <= sinceId) continue;
                    if (source != null && !string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(e);
                }
            }
            return result;
        }

        public static void Clear()
        {
            lock (_lock) { _count = 0; _head = 0; }
        }
    }
}
