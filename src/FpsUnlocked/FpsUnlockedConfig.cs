using TerrariaModder.Core.Config;

namespace FpsUnlocked
{
    public class FpsUnlockedConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Enable the FPS Unlocked mod.")]
        public bool Enabled { get; set; } = true;

        [Client, Label("Frame Rate Mode"), Description("VSync = vanilla 60 FPS, Capped = custom limit, Uncapped = no limit."), Options("VSync (Vanilla)", "Capped", "Uncapped")]
        public string Mode { get; set; } = "VSync (Vanilla)";

        [Client, Label("Max FPS (Capped Mode)"), Description("Maximum frame rate when mode is set to Capped (30-1000)."), Range(30, 1000)]
        public int MaxFps { get; set; } = 144;

        [Client, Label("Frame Interpolation"), Description("Smooth entity motion between game ticks. Keeps game logic at 60hz while rendering at display rate. Disable for raw FPS unlock where game speed scales with FPS.")]
        public bool Interpolation { get; set; } = true;

        [Client, Label("Responsive Mouse"), Description("Update mouse position every render frame for lower input lag (only applies when interpolation is enabled).")]
        public bool MouseEveryFrame { get; set; } = true;
    }
}
