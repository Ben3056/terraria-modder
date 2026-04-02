using TerrariaModder.Core.Config;

namespace WhipStacking
{
    public class WhipStackingConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Allow multiple whip tags to stack on NPCs simultaneously (pre-1.4.5 behavior).")]
        public bool Enabled { get; set; } = true;
    }
}
