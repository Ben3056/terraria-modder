using TerrariaModder.Core.Config;

namespace PetChests
{
    public class PetChestsConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Allow right-clicking your summoned cosmetic pets to open piggy bank storage on the go.")]
        public bool Enabled { get; set; } = true;

        [Client, Label("Interaction Range"), Description("How close (in pixels) you need to be to your pet to right-click it. Increase if your pet is hard to reach."), Range(100, 500)]
        public int InteractionRange { get; set; } = 200;

        [Client, Label("Shown Hint"), Description("Whether the first-run tip has been shown. Resets to false to see the hint again.")]
        public bool ShownHint { get; set; } = false;
    }
}
