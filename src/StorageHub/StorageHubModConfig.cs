using TerrariaModder.Core.Config;

namespace StorageHub
{
    public class StorageHubModConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Master toggle for the Storage Hub mod. When disabled, the toggle keybind does nothing.")]
        public bool Enabled { get; set; } = true;

        [Client, Label("Protect Hotbar"), Description("Prevent items in your hotbar (slots 1-10) from being consumed by crafting recipes in the Storage Hub.")]
        public bool BlockHotbarFromCrafting { get; set; } = false;

        [Client, Label("Recursive Crafting"), Description("Auto-craft missing intermediate materials from stored items. E.g. auto-smelt bars when crafting something that needs them.")]
        public bool RecursiveCrafting { get; set; } = true;

        [Client, Label("Recursive Depth"), Description("Maximum depth for recursive sub-crafting. 0 = unlimited, 1 = one level of sub-crafting, 2-5 = deeper chains."), Range(0, 10)]
        public int RecursiveCraftingDepth { get; set; } = 0;

        [Client, Label("Mysterious Chest"), Description("Enable the Mysterious Chest (a special chest with upgradeable capacity, up to 5000 slots).")]
        public bool PaintingChest { get; set; } = true;

        [Server, Label("Trust All Players"), Description("Allow all connected players to use Storage Hub without admin privileges. Disable for public servers.")]
        public bool TrustAllPlayers { get; set; } = true;
    }
}
