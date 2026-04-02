using TerrariaModder.Core.Config;

namespace ItemSpawner
{
    public class ItemSpawnerConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Master toggle for the item spawner UI. When disabled, the toggle keybind does nothing.")]
        public bool Enabled { get; set; } = true;
    }
}
