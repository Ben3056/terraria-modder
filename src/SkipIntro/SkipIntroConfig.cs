using TerrariaModder.Core.Config;

namespace SkipIntro
{
    public class SkipIntroConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Skip the ReLogic splash screen animation on game startup for faster loading."), RestartRequired]
        public bool Enabled { get; set; } = true;
    }
}
