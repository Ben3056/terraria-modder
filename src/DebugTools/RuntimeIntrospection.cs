using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Logging;

namespace DebugTools
{
    /// <summary>
    /// Runtime introspection: type browsing, field read/write, instance dumps,
    /// property path evaluation, dynamic method tracing, and field watching.
    /// All public Handle* methods are called from DebugHttpServer route dispatch.
    /// </summary>
    public static class RuntimeIntrospection
    {
        private static readonly ILogger _log = LogManager.GetLogger("debug-tools");
        private static readonly Harmony _traceHarmony = new Harmony("com.terrariamodder.debug-tools.trace");

        // Trace storage
        private static readonly ConcurrentDictionary<string, TraceEntry> _traces = new ConcurrentDictionary<string, TraceEntry>();
        private const int MaxTraces = 20;
        private const int TraceBufferSize = 100;

        // Watch storage
        private static readonly ConcurrentDictionary<string, WatchEntry> _watches = new ConcurrentDictionary<string, WatchEntry>();
        private const int MaxWatches = 50;
        private const int WatchBufferSize = 100;
        private static bool _watchHookInstalled = false;
        private static readonly object _watchLock = new object();

        #region Type Resolution

        private static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // Try direct resolution first
            var type = Type.GetType(name);
            if (type != null) return type;

            // Try with Terraria assembly qualifier
            type = Type.GetType(name + ", Terraria");
            if (type != null) return type;

            // Scan all loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(name);
                if (type != null) return type;
            }
            return null;
        }

        private static object GetInstanceFromKnownArray(string typeName, int index)
        {
            // Resolve known arrays for common game types
            if (typeName.Contains("NPC") || typeName == "Terraria.NPC")
            {
                if (index >= 0 && index < Terraria.Main.maxNPCs)
                    return Terraria.Main.npc[index];
            }
            else if (typeName.Contains("Player") || typeName == "Terraria.Player")
            {
                if (index >= 0 && index < Terraria.Main.maxPlayers)
                    return Terraria.Main.player[index];
            }
            else if (typeName.Contains("Projectile") || typeName == "Terraria.Projectile")
            {
                if (index >= 0 && index < Terraria.Main.maxProjectiles)
                    return Terraria.Main.projectile[index];
            }
            else if (typeName.Contains("Item") || typeName == "Terraria.Item")
            {
                // Default: from local player inventory
                var player = Terraria.Main.LocalPlayer;
                if (player != null && index >= 0 && index < player.inventory.Length)
                    return player.inventory[index];
            }
            return null;
        }

        #endregion

        #region D1: GET /api/reflect/type

        public static string HandleReflectType(HttpListenerRequest request)
        {
            try
            {
                string name = request.QueryString["name"];
                if (string.IsNullOrEmpty(name))
                    return "{\"error\": \"Missing 'name' query parameter\"}";

                var type = ResolveType(name);
                if (type == null)
                    return "{\"error\": \"Type not found: " + EscapeJson(name) + "\"}";

                string filter = request.QueryString["filter"];
                string search = request.QueryString["search"]?.ToLowerInvariant();
                int limit = 100;
                if (request.QueryString["limit"] != null) int.TryParse(request.QueryString["limit"], out limit);

                var sb = new StringBuilder(8192);
                sb.Append("{\"name\":\"").Append(EscapeJson(type.FullName ?? type.Name)).Append("\"");
                sb.Append(",\"namespace\":\"").Append(EscapeJson(type.Namespace ?? "")).Append("\"");
                if (type.BaseType != null)
                    sb.Append(",\"baseType\":\"").Append(EscapeJson(type.BaseType.FullName ?? type.BaseType.Name)).Append("\"");

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

                // Fields
                if (filter == null || filter == "fields" || filter == "static")
                {
                    sb.Append(",\"fields\":[");
                    int count = 0;
                    foreach (var f in type.GetFields(flags))
                    {
                        if (filter == "static" && !f.IsStatic) continue;
                        if (search != null && !f.Name.ToLowerInvariant().Contains(search)) continue;
                        if (count >= limit) break;
                        if (count > 0) sb.Append(",");
                        sb.Append("{\"name\":\"").Append(EscapeJson(f.Name)).Append("\"");
                        sb.Append(",\"type\":\"").Append(EscapeJson(f.FieldType.Name)).Append("\"");
                        sb.Append(",\"isStatic\":").Append(f.IsStatic ? "true" : "false");
                        sb.Append(",\"isPublic\":").Append(f.IsPublic ? "true" : "false");
                        sb.Append("}");
                        count++;
                    }
                    sb.Append("]");
                }

                // Properties
                if (filter == null || filter == "properties" || filter == "static")
                {
                    sb.Append(",\"properties\":[");
                    int count = 0;
                    foreach (var p in type.GetProperties(flags))
                    {
                        if (filter == "static")
                        {
                            var getter = p.GetGetMethod(true);
                            if (getter == null || !getter.IsStatic) continue;
                        }
                        if (search != null && !p.Name.ToLowerInvariant().Contains(search)) continue;
                        if (count >= limit) break;
                        if (count > 0) sb.Append(",");
                        sb.Append("{\"name\":\"").Append(EscapeJson(p.Name)).Append("\"");
                        sb.Append(",\"type\":\"").Append(EscapeJson(p.PropertyType.Name)).Append("\"");
                        sb.Append(",\"canRead\":").Append(p.CanRead ? "true" : "false");
                        sb.Append(",\"canWrite\":").Append(p.CanWrite ? "true" : "false");
                        var g = p.GetGetMethod(true);
                        sb.Append(",\"isStatic\":").Append(g != null && g.IsStatic ? "true" : "false");
                        sb.Append("}");
                        count++;
                    }
                    sb.Append("]");
                }

                // Methods
                if (filter == null || filter == "methods" || filter == "static")
                {
                    sb.Append(",\"methods\":[");
                    int count = 0;
                    foreach (var m in type.GetMethods(flags))
                    {
                        if (filter == "static" && !m.IsStatic) continue;
                        if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue; // skip property accessors
                        if (search != null && !m.Name.ToLowerInvariant().Contains(search)) continue;
                        if (count >= limit) break;
                        if (count > 0) sb.Append(",");
                        sb.Append("{\"name\":\"").Append(EscapeJson(m.Name)).Append("\"");
                        sb.Append(",\"returnType\":\"").Append(EscapeJson(m.ReturnType.Name)).Append("\"");
                        sb.Append(",\"isStatic\":").Append(m.IsStatic ? "true" : "false");
                        sb.Append(",\"isPublic\":").Append(m.IsPublic ? "true" : "false");
                        var parms = m.GetParameters();
                        if (parms.Length > 0)
                        {
                            sb.Append(",\"parameters\":[");
                            for (int i = 0; i < parms.Length; i++)
                            {
                                if (i > 0) sb.Append(",");
                                sb.Append("{\"name\":\"").Append(EscapeJson(parms[i].Name)).Append("\"");
                                sb.Append(",\"type\":\"").Append(EscapeJson(parms[i].ParameterType.Name)).Append("\"}");
                            }
                            sb.Append("]");
                        }
                        sb.Append("}");
                        count++;
                    }
                    sb.Append("]");
                }

                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        #endregion

        #region D2/D3: GET/POST /api/reflect/field

        public static string HandleReflectFieldGet(HttpListenerRequest request)
        {
            try
            {
                string typeName = request.QueryString["type"];
                string fieldName = request.QueryString["field"];
                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(fieldName))
                    return "{\"error\": \"Missing 'type' or 'field' query parameter\"}";

                var type = ResolveType(typeName);
                if (type == null)
                    return "{\"error\": \"Type not found: " + EscapeJson(typeName) + "\"}";

                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                    return "{\"error\": \"Static field not found: " + EscapeJson(fieldName) + "\"}";

                object value = field.GetValue(null);
                return SerializeFieldValue(typeName, fieldName, field.FieldType, value);
            }
            catch (Exception ex) { return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        public static string HandleReflectFieldSet(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                string typeName = ExtractJsonString(body, "type");
                string fieldName = ExtractJsonString(body, "field");
                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(fieldName))
                {
                    statusCode = 400;
                    return "{\"error\": \"Missing 'type' or 'field'\"}";
                }

                var type = ResolveType(typeName);
                if (type == null) { statusCode = 400; return "{\"error\": \"Type not found\"}"; }

                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null) { statusCode = 400; return "{\"error\": \"Static field not found\"}"; }

                // Extract value from JSON body and convert
                string rawValue = ExtractJsonRawValue(body, "value");
                object convertedValue = ConvertJsonValue(rawValue, field.FieldType);

                string result = MainThreadDispatcher.RunOnMainThread<string>(() =>
                {
                    object oldValue = field.GetValue(null);
                    field.SetValue(null, convertedValue);
                    EventLog.Emit("debug-tools", "reflect_field_set",
                        $"{{\"type\":\"{EscapeJson(typeName)}\",\"field\":\"{EscapeJson(fieldName)}\",\"old\":\"{oldValue}\",\"new\":\"{convertedValue}\"}}");
                    return null;
                });

                if (result != null) { statusCode = 400; return "{\"error\": \"" + EscapeJson(result) + "\"}"; }
                return "{\"success\": true, \"type\": \"" + EscapeJson(typeName) + "\", \"field\": \"" + EscapeJson(fieldName) + "\"}";
            }
            catch (Exception ex) { statusCode = 500; return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        #endregion

        #region D4: GET /api/reflect/instance

        public static string HandleReflectInstance(HttpListenerRequest request)
        {
            try
            {
                string typeName = request.QueryString["type"];
                string indexStr = request.QueryString["index"];
                string search = request.QueryString["search"]?.ToLowerInvariant();
                int limit = 200;
                if (request.QueryString["limit"] != null) int.TryParse(request.QueryString["limit"], out limit);

                if (string.IsNullOrEmpty(typeName))
                    return "{\"error\": \"Missing 'type' query parameter\"}";

                int index = 0;
                if (!string.IsNullOrEmpty(indexStr)) int.TryParse(indexStr, out index);

                object instance = GetInstanceFromKnownArray(typeName, index);
                if (instance == null)
                    return "{\"error\": \"Could not get instance of " + EscapeJson(typeName) + " at index " + index + "\"}";

                var instanceType = instance.GetType();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                var sb = new StringBuilder(8192);
                sb.Append("{\"type\":\"").Append(EscapeJson(instanceType.FullName ?? instanceType.Name)).Append("\"");
                sb.Append(",\"index\":").Append(index);
                sb.Append(",\"fields\":[");

                int count = 0;
                foreach (var f in instanceType.GetFields(flags))
                {
                    if (search != null && !f.Name.ToLowerInvariant().Contains(search)) continue;
                    if (count >= limit) break;
                    if (count > 0) sb.Append(",");

                    sb.Append("{\"name\":\"").Append(EscapeJson(f.Name)).Append("\"");
                    sb.Append(",\"type\":\"").Append(EscapeJson(f.FieldType.Name)).Append("\"");

                    try
                    {
                        object val = f.GetValue(instance);
                        sb.Append(",\"value\":").Append(SerializeValue(val, f.FieldType));
                    }
                    catch
                    {
                        sb.Append(",\"value\":\"<error>\"");
                    }
                    sb.Append("}");
                    count++;
                }
                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex) { return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        #endregion

        #region D5: GET /api/eval

        public static string HandleEval(HttpListenerRequest request)
        {
            try
            {
                string path = request.QueryString["path"];
                if (string.IsNullOrEmpty(path))
                    return "{\"error\": \"Missing 'path' query parameter\"}";

                // Parse path into segments
                var segments = ParsePath(path);
                if (segments.Count == 0)
                    return "{\"error\": \"Empty path\"}";

                // Resolve starting point
                object current = null;
                Type currentType = null;
                bool isStatic = true;

                string root = segments[0].Name;
                switch (root)
                {
                    case "Main":
                        currentType = ResolveType("Terraria.Main");
                        break;
                    case "NPC":
                        currentType = ResolveType("Terraria.NPC");
                        break;
                    case "Player":
                        currentType = ResolveType("Terraria.Player");
                        break;
                    case "WorldGen":
                        currentType = ResolveType("Terraria.WorldGen");
                        break;
                    case "Item":
                        currentType = ResolveType("Terraria.Item");
                        break;
                    default:
                        currentType = ResolveType(root) ?? ResolveType("Terraria." + root);
                        break;
                }

                if (currentType == null)
                    return "{\"error\": \"Cannot resolve root type: " + EscapeJson(root) + "\"}";

                // If first segment has an array index, it's accessing a static array
                if (segments[0].Index >= 0)
                {
                    // e.g., Main.player[0] — need to resolve in next step
                }

                // Walk the path
                for (int i = 1; i < segments.Count; i++)
                {
                    var seg = segments[i];
                    var flags = BindingFlags.Public | BindingFlags.NonPublic
                        | (isStatic ? BindingFlags.Static : BindingFlags.Instance);

                    // Try field first
                    var field = currentType.GetField(seg.Name, flags);
                    if (field != null)
                    {
                        current = field.GetValue(isStatic ? null : current);
                        currentType = field.FieldType;
                        isStatic = false;
                    }
                    else
                    {
                        // Try property
                        var prop = currentType.GetProperty(seg.Name, flags);
                        if (prop != null && prop.CanRead)
                        {
                            current = prop.GetValue(isStatic ? null : current);
                            currentType = prop.PropertyType;
                            isStatic = false;
                        }
                        else
                        {
                            return "{\"error\": \"Cannot resolve '" + EscapeJson(seg.Name) + "' on " + EscapeJson(currentType.Name) + "\"}";
                        }
                    }

                    // Handle array indexing
                    if (seg.Index >= 0 && current != null)
                    {
                        if (current is Array arr)
                        {
                            if (seg.Index < arr.Length)
                            {
                                current = arr.GetValue(seg.Index);
                                if (current != null)
                                    currentType = current.GetType();
                                else if (currentType.IsArray)
                                    currentType = currentType.GetElementType();
                            }
                            else
                            {
                                return "{\"error\": \"Index " + seg.Index + " out of bounds (length " + arr.Length + ")\"}";
                            }
                        }
                        else
                        {
                            // Try indexer property
                            var indexer = currentType.GetProperty("Item", new[] { typeof(int) });
                            if (indexer != null)
                            {
                                current = indexer.GetValue(current, new object[] { seg.Index });
                                if (current != null) currentType = current.GetType();
                            }
                        }
                        isStatic = false;
                    }
                }

                string valueStr = SerializeValue(current, currentType);
                return "{\"path\":\"" + EscapeJson(path) + "\",\"value\":" + valueStr + ",\"valueType\":\"" + EscapeJson(currentType?.Name ?? "null") + "\"}";
            }
            catch (Exception ex) { return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        private struct PathSegment
        {
            public string Name;
            public int Index; // -1 if no index
        }

        private static List<PathSegment> ParsePath(string path)
        {
            var segments = new List<PathSegment>();
            var parts = path.Split('.');

            foreach (var part in parts)
            {
                int bracketStart = part.IndexOf('[');
                if (bracketStart >= 0)
                {
                    int bracketEnd = part.IndexOf(']', bracketStart);
                    string name = part.Substring(0, bracketStart);
                    string indexStr = part.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
                    int idx = 0;
                    int.TryParse(indexStr, out idx);
                    segments.Add(new PathSegment { Name = name, Index = idx });
                }
                else
                {
                    segments.Add(new PathSegment { Name = part, Index = -1 });
                }
            }
            return segments;
        }

        #endregion

        #region D6-D8: Tracing

        private class TraceEntry
        {
            public string TraceId;
            public MethodInfo Method;
            public TraceCall[] Calls;
            public int WriteIndex;
            public int TotalCalls;

            public TraceEntry(string id, MethodInfo method)
            {
                TraceId = id;
                Method = method;
                Calls = new TraceCall[TraceBufferSize];
                WriteIndex = 0;
                TotalCalls = 0;
            }

            public void AddCall(object[] args, string stackTrace)
            {
                int idx = WriteIndex % TraceBufferSize;
                Calls[idx] = new TraceCall
                {
                    Timestamp = DateTime.UtcNow,
                    Args = args,
                    StackTrace = stackTrace
                };
                WriteIndex++;
                TotalCalls++;
            }
        }

        private struct TraceCall
        {
            public DateTime Timestamp;
            public object[] Args;
            public string StackTrace;
        }

        // Static dict for prefix callbacks to find their trace entry
        private static readonly ConcurrentDictionary<string, TraceEntry> _tracesByMethod = new ConcurrentDictionary<string, TraceEntry>();

        public static string HandleTraceAdd(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                string typeName = ExtractJsonString(body, "type");
                string methodName = ExtractJsonString(body, "method");
                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
                {
                    statusCode = 400;
                    return "{\"error\": \"Missing 'type' or 'method'\"}";
                }

                string traceId = typeName.Replace("Terraria.", "") + "." + methodName;
                if (_traces.ContainsKey(traceId))
                    return "{\"success\": true, \"traceId\": \"" + EscapeJson(traceId) + "\", \"message\": \"Already tracing\"}";

                if (_traces.Count >= MaxTraces)
                {
                    statusCode = 400;
                    return "{\"error\": \"Max " + MaxTraces + " concurrent traces\"}";
                }

                var type = ResolveType(typeName);
                if (type == null) { statusCode = 400; return "{\"error\": \"Type not found\"}"; }

                // Find the method (take first match)
                MethodInfo method = null;
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    if (m.Name == methodName) { method = m; break; }
                }
                if (method == null) { statusCode = 400; return "{\"error\": \"Method not found: " + EscapeJson(methodName) + "\"}"; }

                var entry = new TraceEntry(traceId, method);
                _traces[traceId] = entry;
                _tracesByMethod[method.DeclaringType.FullName + "." + method.Name] = entry;

                // Apply Harmony prefix
                var prefixMethod = typeof(RuntimeIntrospection).GetMethod(nameof(TracePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                _traceHarmony.Patch(method, prefix: new HarmonyMethod(prefixMethod));

                _log.Info($"[Introspection] Trace added: {traceId}");
                return "{\"success\": true, \"traceId\": \"" + EscapeJson(traceId) + "\"}";
            }
            catch (Exception ex) { statusCode = 500; return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        private static void TracePrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                string key = __originalMethod.DeclaringType.FullName + "." + __originalMethod.Name;
                if (_tracesByMethod.TryGetValue(key, out var entry))
                {
                    // Get truncated stack trace (3 frames)
                    string stack = "";
                    try
                    {
                        var st = new System.Diagnostics.StackTrace(2, true);
                        var frames = st.GetFrames();
                        if (frames != null)
                        {
                            var stackSb = new StringBuilder();
                            int maxFrames = Math.Min(3, frames.Length);
                            for (int i = 0; i < maxFrames; i++)
                            {
                                var f = frames[i];
                                if (i > 0) stackSb.Append(" <- ");
                                stackSb.Append(f.GetMethod()?.DeclaringType?.Name ?? "?");
                                stackSb.Append(".").Append(f.GetMethod()?.Name ?? "?");
                            }
                            stack = stackSb.ToString();
                        }
                    }
                    catch { }

                    // Capture first 5 args
                    object[] capturedArgs = null;
                    if (__args != null && __args.Length > 0)
                    {
                        int count = Math.Min(5, __args.Length);
                        capturedArgs = new object[count];
                        for (int i = 0; i < count; i++)
                        {
                            try { capturedArgs[i] = __args[i]?.ToString() ?? "null"; }
                            catch { capturedArgs[i] = "<error>"; }
                        }
                    }

                    entry.AddCall(capturedArgs, stack);
                }
            }
            catch { /* Never throw from trace prefix */ }
        }

        public static string HandleTraceRemove(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                string traceId = ExtractJsonString(body, "traceId");
                bool all = ExtractJsonBool(body, "all", false);

                if (all)
                {
                    _traceHarmony.UnpatchAll("com.terrariamodder.debug-tools.trace");
                    _traces.Clear();
                    _tracesByMethod.Clear();
                    return "{\"success\": true, \"removed\": \"all\"}";
                }

                if (string.IsNullOrEmpty(traceId)) { statusCode = 400; return "{\"error\": \"Missing 'traceId' or 'all'\"}"; }

                TraceEntry entry;
                if (_traces.TryRemove(traceId, out entry))
                {
                    _traceHarmony.Unpatch(entry.Method, HarmonyPatchType.Prefix, "com.terrariamodder.debug-tools.trace");
                    _tracesByMethod.TryRemove(entry.Method.DeclaringType.FullName + "." + entry.Method.Name, out _);
                    return "{\"success\": true, \"removed\": \"" + EscapeJson(traceId) + "\"}";
                }

                statusCode = 404;
                return "{\"error\": \"Trace not found: " + EscapeJson(traceId) + "\"}";
            }
            catch (Exception ex) { statusCode = 500; return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        public static string HandleTraceLog(HttpListenerRequest request)
        {
            try
            {
                string traceId = request.QueryString["traceId"];
                int count = 20;
                if (request.QueryString["count"] != null) int.TryParse(request.QueryString["count"], out count);

                if (string.IsNullOrEmpty(traceId))
                {
                    // Return summary of all traces
                    var sb = new StringBuilder();
                    sb.Append("{\"traces\":[");
                    bool first = true;
                    foreach (var kv in _traces)
                    {
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("{\"traceId\":\"").Append(EscapeJson(kv.Key)).Append("\"");
                        sb.Append(",\"totalCalls\":").Append(kv.Value.TotalCalls).Append("}");
                    }
                    sb.Append("]}");
                    return sb.ToString();
                }

                TraceEntry entry;
                if (!_traces.TryGetValue(traceId, out entry))
                    return "{\"error\": \"Trace not found\"}";

                var rsb = new StringBuilder(4096);
                rsb.Append("{\"traceId\":\"").Append(EscapeJson(traceId)).Append("\"");
                rsb.Append(",\"totalCalls\":").Append(entry.TotalCalls);
                rsb.Append(",\"calls\":[");

                // Read most recent 'count' calls
                int start = Math.Max(0, entry.WriteIndex - count);
                bool f = true;
                for (int i = start; i < entry.WriteIndex; i++)
                {
                    int idx = i % TraceBufferSize;
                    var call = entry.Calls[idx];
                    if (call.Timestamp == default) continue;
                    if (!f) rsb.Append(",");
                    f = false;
                    rsb.Append("{\"timestamp\":\"").Append(call.Timestamp.ToString("HH:mm:ss.fff")).Append("\"");
                    if (call.Args != null)
                    {
                        rsb.Append(",\"args\":[");
                        for (int a = 0; a < call.Args.Length; a++)
                        {
                            if (a > 0) rsb.Append(",");
                            rsb.Append("\"").Append(EscapeJson(call.Args[a]?.ToString() ?? "null")).Append("\"");
                        }
                        rsb.Append("]");
                    }
                    if (!string.IsNullOrEmpty(call.StackTrace))
                        rsb.Append(",\"stack\":\"").Append(EscapeJson(call.StackTrace)).Append("\"");
                    rsb.Append("}");
                }
                rsb.Append("]}");
                return rsb.ToString();
            }
            catch (Exception ex) { return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        #endregion

        #region D9-D11: Field Watching

        private class WatchEntry
        {
            public string WatchId;
            public Type TargetType;
            public FieldInfo Field;
            public int InstanceIndex; // -1 for static
            public object LastValue;
            public WatchChange[] Changes;
            public int WriteIndex;
            public int TotalChanges;

            public WatchEntry(string id, Type type, FieldInfo field, int instanceIndex)
            {
                WatchId = id;
                TargetType = type;
                Field = field;
                InstanceIndex = instanceIndex;
                Changes = new WatchChange[WatchBufferSize];
                WriteIndex = 0;
                TotalChanges = 0;
            }
        }

        private struct WatchChange
        {
            public DateTime Timestamp;
            public string OldValue;
            public string NewValue;
        }

        public static string HandleWatchAdd(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                string typeName = ExtractJsonString(body, "type");
                string fieldName = ExtractJsonString(body, "field");
                int index = ExtractJsonInt(body, "index", -1);

                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(fieldName))
                {
                    statusCode = 400;
                    return "{\"error\": \"Missing 'type' or 'field'\"}";
                }

                string watchId = typeName.Replace("Terraria.", "") + (index >= 0 ? "[" + index + "]" : "") + "." + fieldName;
                if (_watches.ContainsKey(watchId))
                    return "{\"success\": true, \"watchId\": \"" + EscapeJson(watchId) + "\", \"message\": \"Already watching\"}";

                if (_watches.Count >= MaxWatches) { statusCode = 400; return "{\"error\": \"Max " + MaxWatches + " concurrent watches\"}"; }

                var type = ResolveType(typeName);
                if (type == null) { statusCode = 400; return "{\"error\": \"Type not found\"}"; }

                var flags = index >= 0
                    ? BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    : BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var field = type.GetField(fieldName, flags);
                if (field == null) { statusCode = 400; return "{\"error\": \"Field not found\"}"; }

                var entry = new WatchEntry(watchId, type, field, index);

                // Read initial value
                try
                {
                    object instance = index >= 0 ? GetInstanceFromKnownArray(typeName, index) : null;
                    entry.LastValue = field.GetValue(instance);
                }
                catch { }

                _watches[watchId] = entry;
                EnsureWatchHook();

                string currentVal = entry.LastValue?.ToString() ?? "null";
                return "{\"success\": true, \"watchId\": \"" + EscapeJson(watchId) + "\", \"currentValue\": \"" + EscapeJson(currentVal) + "\"}";
            }
            catch (Exception ex) { statusCode = 500; return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        public static string HandleWatchRemove(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                string watchId = ExtractJsonString(body, "watchId");
                bool all = ExtractJsonBool(body, "all", false);

                if (all)
                {
                    _watches.Clear();
                    return "{\"success\": true, \"removed\": \"all\"}";
                }

                if (string.IsNullOrEmpty(watchId)) { statusCode = 400; return "{\"error\": \"Missing 'watchId' or 'all'\"}"; }

                WatchEntry entry;
                if (_watches.TryRemove(watchId, out entry))
                    return "{\"success\": true, \"removed\": \"" + EscapeJson(watchId) + "\"}";

                statusCode = 404;
                return "{\"error\": \"Watch not found\"}";
            }
            catch (Exception ex) { statusCode = 500; return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        public static string HandleWatchLog(HttpListenerRequest request)
        {
            try
            {
                string watchId = request.QueryString["watchId"];
                int count = 20;
                if (request.QueryString["count"] != null) int.TryParse(request.QueryString["count"], out count);

                if (string.IsNullOrEmpty(watchId))
                {
                    // Summary of all watches
                    var sb = new StringBuilder();
                    sb.Append("{\"watches\":[");
                    bool first = true;
                    foreach (var kv in _watches)
                    {
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("{\"watchId\":\"").Append(EscapeJson(kv.Key)).Append("\"");
                        sb.Append(",\"currentValue\":\"").Append(EscapeJson(kv.Value.LastValue?.ToString() ?? "null")).Append("\"");
                        sb.Append(",\"totalChanges\":").Append(kv.Value.TotalChanges).Append("}");
                    }
                    sb.Append("]}");
                    return sb.ToString();
                }

                WatchEntry entry;
                if (!_watches.TryGetValue(watchId, out entry))
                    return "{\"error\": \"Watch not found\"}";

                var rsb = new StringBuilder(2048);
                rsb.Append("{\"watchId\":\"").Append(EscapeJson(watchId)).Append("\"");
                rsb.Append(",\"currentValue\":\"").Append(EscapeJson(entry.LastValue?.ToString() ?? "null")).Append("\"");
                rsb.Append(",\"totalChanges\":").Append(entry.TotalChanges);
                rsb.Append(",\"changes\":[");

                int start = Math.Max(0, entry.WriteIndex - count);
                bool f = true;
                for (int i = start; i < entry.WriteIndex; i++)
                {
                    int idx = i % WatchBufferSize;
                    var change = entry.Changes[idx];
                    if (change.Timestamp == default) continue;
                    if (!f) rsb.Append(",");
                    f = false;
                    rsb.Append("{\"timestamp\":\"").Append(change.Timestamp.ToString("HH:mm:ss.fff")).Append("\"");
                    rsb.Append(",\"oldValue\":\"").Append(EscapeJson(change.OldValue ?? "null")).Append("\"");
                    rsb.Append(",\"newValue\":\"").Append(EscapeJson(change.NewValue ?? "null")).Append("\"}");
                }
                rsb.Append("]}");
                return rsb.ToString();
            }
            catch (Exception ex) { return "{\"error\": \"" + EscapeJson(ex.Message) + "\"}"; }
        }

        private static void EnsureWatchHook()
        {
            lock (_watchLock)
            {
                if (_watchHookInstalled) return;
                _watchHookInstalled = true;

                // Install a postfix on Main.DoUpdate to poll watches each frame
                try
                {
                    var mainType = typeof(Terraria.Main);
                    var doUpdate = mainType.GetMethod("DoUpdate",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(Microsoft.Xna.Framework.GameTime) }, null);

                    if (doUpdate != null)
                    {
                        var postfix = typeof(RuntimeIntrospection).GetMethod(nameof(WatchPollPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic);
                        _traceHarmony.Patch(doUpdate, postfix: new HarmonyMethod(postfix));
                        _log.Info("[Introspection] Watch hook installed on Main.DoUpdate");
                    }
                    else
                    {
                        // Fallback: try Update
                        var update = mainType.GetMethod("Update",
                            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public,
                            null, new[] { typeof(Microsoft.Xna.Framework.GameTime) }, null);
                        if (update != null)
                        {
                            var postfix = typeof(RuntimeIntrospection).GetMethod(nameof(WatchPollPostfix),
                                BindingFlags.Static | BindingFlags.NonPublic);
                            _traceHarmony.Patch(update, postfix: new HarmonyMethod(postfix));
                            _log.Info("[Introspection] Watch hook installed on Main.Update");
                        }
                        else
                        {
                            _log.Error("[Introspection] Could not find Main.DoUpdate or Main.Update for watch hook");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"[Introspection] Failed to install watch hook: {ex.Message}");
                    _watchHookInstalled = false;
                }
            }
        }

        private static int _watchFrameCounter = 0;

        private static void WatchPollPostfix()
        {
            try
            {
                _watchFrameCounter++;
                // Only poll every 6 frames (~10Hz at 60fps) to reduce overhead
                if (_watchFrameCounter % 6 != 0) return;

                foreach (var kv in _watches)
                {
                    var entry = kv.Value;
                    try
                    {
                        object instance = entry.InstanceIndex >= 0
                            ? GetInstanceFromKnownArray(entry.TargetType.FullName, entry.InstanceIndex)
                            : null;

                        object newValue = entry.Field.GetValue(instance);
                        string newStr = newValue?.ToString() ?? "null";
                        string oldStr = entry.LastValue?.ToString() ?? "null";

                        if (newStr != oldStr)
                        {
                            int idx = entry.WriteIndex % WatchBufferSize;
                            entry.Changes[idx] = new WatchChange
                            {
                                Timestamp = DateTime.UtcNow,
                                OldValue = oldStr,
                                NewValue = newStr
                            };
                            entry.WriteIndex++;
                            entry.TotalChanges++;
                            entry.LastValue = newValue;
                        }
                    }
                    catch { /* Skip broken watches */ }
                }
            }
            catch { /* Never throw from postfix */ }
        }

        #endregion

        #region JSON Helpers

        private static string SerializeValue(object value, Type type)
        {
            if (value == null) return "null";
            if (type == typeof(bool)) return (bool)value ? "true" : "false";
            if (type == typeof(int) || type == typeof(short) || type == typeof(byte)
                || type == typeof(long) || type == typeof(uint) || type == typeof(ushort))
                return value.ToString();
            if (type == typeof(float)) return ((float)value).ToString("G");
            if (type == typeof(double)) return ((double)value).ToString("G");
            if (type == typeof(string)) return "\"" + EscapeJson((string)value) + "\"";

            if (value is Array arr)
            {
                if (arr.Length > 10)
                    return "\"[Array length=" + arr.Length + "]\"";
                var sb = new StringBuilder();
                sb.Append("[");
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    try
                    {
                        var elem = arr.GetValue(i);
                        sb.Append(elem == null ? "null" : "\"" + EscapeJson(elem.ToString()) + "\"");
                    }
                    catch { sb.Append("\"<error>\""); }
                }
                sb.Append("]");
                return sb.ToString();
            }

            return "\"" + EscapeJson(value.ToString()) + "\"";
        }

        private static string SerializeFieldValue(string typeName, string fieldName, Type fieldType, object value)
        {
            string valStr = SerializeValue(value, fieldType);
            return "{\"type\":\"" + EscapeJson(typeName) + "\",\"field\":\"" + EscapeJson(fieldName) + "\",\"value\":" + valStr + ",\"valueType\":\"" + EscapeJson(fieldType.Name) + "\"}";
        }

        private static object ConvertJsonValue(string raw, Type targetType)
        {
            if (raw == null || raw == "null") return null;

            // Remove quotes if string
            if (raw.StartsWith("\"") && raw.EndsWith("\""))
                raw = raw.Substring(1, raw.Length - 2);

            if (targetType == typeof(bool)) return raw.ToLower() == "true";
            if (targetType == typeof(int)) return int.Parse(raw);
            if (targetType == typeof(float)) return float.Parse(raw);
            if (targetType == typeof(double)) return double.Parse(raw);
            if (targetType == typeof(long)) return long.Parse(raw);
            if (targetType == typeof(short)) return short.Parse(raw);
            if (targetType == typeof(byte)) return byte.Parse(raw);
            if (targetType == typeof(uint)) return uint.Parse(raw);
            if (targetType == typeof(ushort)) return ushort.Parse(raw);
            if (targetType == typeof(string)) return raw;
            throw new InvalidOperationException($"Cannot convert to {targetType.Name}");
        }

        private static string ExtractJsonRawValue(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return null;
            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            int valueStart = colonIdx + 1;
            while (valueStart < json.Length && json[valueStart] == ' ') valueStart++;

            if (valueStart >= json.Length) return null;

            char first = json[valueStart];

            // String value
            if (first == '"')
            {
                int end = valueStart + 1;
                while (end < json.Length)
                {
                    if (json[end] == '\\') { end += 2; continue; }
                    if (json[end] == '"') break;
                    end++;
                }
                if (end >= json.Length) return null;
                return json.Substring(valueStart, end - valueStart + 1);
            }

            // Boolean or number or null
            int terminator = valueStart;
            while (terminator < json.Length && json[terminator] != ',' && json[terminator] != '}' && json[terminator] != ']')
                terminator++;
            return json.Substring(valueStart, terminator - valueStart).Trim();
        }

        // Delegate to DebugHttpServer helpers via reflection or inline
        private static string ExtractJsonString(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return null;
            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;
            int quoteStart = json.IndexOf('"', colonIdx + 1);
            if (quoteStart < 0) return null;
            int quoteEnd = quoteStart + 1;
            while (quoteEnd < json.Length)
            {
                if (json[quoteEnd] == '\\') { quoteEnd += 2; continue; }
                if (json[quoteEnd] == '"') break;
                quoteEnd++;
            }
            if (quoteEnd >= json.Length) return null;
            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        private static int ExtractJsonInt(string json, string key, int defaultValue = 0)
        {
            string raw = ExtractJsonRawValue(json, key);
            if (raw == null) return defaultValue;
            raw = raw.Trim().Trim('"');
            int result;
            return int.TryParse(raw, out result) ? result : defaultValue;
        }

        private static bool ExtractJsonBool(string json, string key, bool defaultValue = false)
        {
            string raw = ExtractJsonRawValue(json, key);
            if (raw == null) return defaultValue;
            return raw.Trim().ToLower() == "true";
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        #endregion
    }
}