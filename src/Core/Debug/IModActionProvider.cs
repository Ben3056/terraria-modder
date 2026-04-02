using System.Collections.Generic;

namespace TerrariaModder.Core.Debug
{
    /// <summary>
    /// Interface for mods to expose executable actions via the debug HTTP API.
    /// Mods implement this to self-describe their capabilities — the HTTP server
    /// and MCP tools route generically without knowing about specific mods.
    /// </summary>
    public interface IModActionProvider
    {
        List<ModActionInfo> GetActions();
        ModActionResult ExecuteAction(string name, Dictionary<string, string> args);
    }

    public class ModActionInfo
    {
        public string Name;
        public string Description;
        public List<ModActionParam> Params;

        public ModActionInfo(string name, string description, params ModActionParam[] parms)
        {
            Name = name;
            Description = description;
            Params = parms != null ? new List<ModActionParam>(parms) : new List<ModActionParam>();
        }
    }

    public class ModActionParam
    {
        public string Name;
        public string Type; // "string", "int", "float", "bool"
        public bool Required;
        public string Description;

        public ModActionParam(string name, string type, bool required, string description)
        {
            Name = name;
            Type = type;
            Required = required;
            Description = description;
        }
    }

    public class ModActionResult
    {
        public bool Success;
        public string Message;
        public Dictionary<string, object> Data;

        public static ModActionResult Ok(string message = null, Dictionary<string, object> data = null)
        {
            return new ModActionResult { Success = true, Message = message, Data = data };
        }

        public static ModActionResult Fail(string message)
        {
            return new ModActionResult { Success = false, Message = message };
        }
    }
}
