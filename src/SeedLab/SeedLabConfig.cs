using TerrariaModder.Core.Config;

namespace SeedLab
{
    public class SeedLabConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Enable the Seed Lab mod.")]
        public bool Enabled { get; set; } = true;
    }
}
