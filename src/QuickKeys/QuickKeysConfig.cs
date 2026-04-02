using TerrariaModder.Core.Config;

namespace QuickKeys
{
    public class QuickKeysConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Master toggle for all QuickKeys features including auto-torch, recall, and quick stack hotkeys.")]
        public bool Enabled { get; set; } = true;

        [Client, Label("Show Messages"), Description("Display chat notifications when using auto-torch, recall, or quick stack (e.g. 'Recalled home!').")]
        public bool ShowMessages { get; set; } = true;

        [Client, Label("Extended Hotbar (Slots 11-20)"), Description("Map NumPad 1-0 to inventory slots 11-20, letting you quick-use items from the second row of your inventory.")]
        public bool EnableExtendedHotbar { get; set; } = false;

        [Client, Label("Debug Logging"), Description("Enable verbose debug logging for QuickKeys actions.")]
        public bool DebugLogging { get; set; } = false;
    }
}
