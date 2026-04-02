using TerrariaModder.Core.Config;

namespace DebugTools
{
    public class DebugToolsConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Enable the debug tools suite.")]
        public bool Enabled { get; set; } = true;

        [Client, Label("HTTP Debug Server"), Description("Start the HTTP API server on the configured port."), RestartRequired]
        public bool HttpServer { get; set; } = true;

        [Client, Label("HTTP Port"), Description("Port for the HTTP debug server. Default 7878. Change to run a second instance."), RestartRequired, Range(1024, 65535)]
        public int HttpPort { get; set; } = 7878;

        [Client, Label("Start Hidden"), Description("Hide game and console windows on startup for headless operation."), RestartRequired]
        public bool StartHidden { get; set; } = false;
    }
}
