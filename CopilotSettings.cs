using System.Windows.Forms;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ExileCore.Shared.Attributes; // For Menu and Submenu attributes
using Newtonsoft.Json; // Needed for [JsonIgnore] if you still want to ignore PartyElements

namespace Copilot
{
    public class CopilotSettings : ISettings
    {
        public ToggleNode Enable { get; set; } = new ToggleNode(true);

        // [JsonIgnore] attribute is good practice if you don't want this array saved directly in settings.json
        [JsonIgnore]
        public string[] PartyElements { get; set; } = new string[5];

        public TextNode TargetPlayerName { get; set; } = new TextNode("Leader");

        [Menu(null, "~460 as default")] // Keep the default text, if it's descriptive
        public RangeNode<int> FollowDistance { get; set; } = new RangeNode<int>(460, 10, 1000);

        [Menu(null, "~100 as default")] // Keep the default text
        public RangeNode<int> ActionCooldown { get; set; } = new RangeNode<int>(100, 50, 20000); // Cooldown in milliseconds

        public HotkeyNode TogglePauseHotkey { get; set; } = new HotkeyNode(Keys.OemPeriod); // Default to Period key

        [Menu("Disabled")] // This will show "Disabled" next to the checkbox in the UI
        public ToggleNode IsPaused { get; set; } = new ToggleNode(false); // Default to not paused

        [Menu("Additional Settings")] // Grouping settings under a main menu title
        public AdditionalSettings Additional { get; set; } = new AdditionalSettings();

        [Menu("Blink Settings")]
        public BlinkSettings Blink { get; set; } = new BlinkSettings();

        [Menu("Pickup Settings")]
        public PickupSettings Pickup { get; set; } = new PickupSettings();

        [Menu("Shock Bot Settings")]
        public ShockBotSettings ShockBot { get; set; } = new ShockBotSettings();
    }

    [Submenu(CollapsedByDefault = true)] // This attribute is also in ExileCore.Shared.Attributes
    public class AdditionalSettings
    {
        [Menu("Use Mouse to Follow")]
        public ToggleNode UseMouse { get; set; } = new ToggleNode(true);

        [Menu("Follow with key")]
        public HotkeyNode FollowKey { get; set; } = new HotkeyNode(Keys.T);
    }

    [Submenu(CollapsedByDefault = true)]
    public class BlinkSettings
    {
        [Menu("Enable")]
        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        [Menu("Range", "The minimum range required to teleport (TP) to the target player. Default: 1000.")]
        public RangeNode<int> Range { get; set; } = new RangeNode<int>(1000, 10, 2000);

        [Menu("Cooldown", "If within range, it will attempt to TP every {cooldown} milliseconds. Default: 500.")]
        public RangeNode<int> Cooldown { get; set; } = new RangeNode<int>(500, 100, 10000);
    }

    [Submenu(CollapsedByDefault = true)]
    public class PickupSettings
    {
        [Menu("Enable", "This will enable the item pickup of EVERY item that is within the range.")]
        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        [Menu("Use Target's Position", "This will use the target's position to pick up items.")]
        public ToggleNode UseTargetPosition { get; set; } = new ToggleNode(false);

        [Menu("Range", "The minimum range required to pick up an item. Default: 400.")]
        public RangeNode<int> Range { get; set; } = new RangeNode<int>(400, 1, 1000);

        [Menu("Item Filter", "Comma-separated list of item names to pick up. e.g. Orb,Mirror,...")]
        public TextNode Filter { get; set; } = new TextNode("Orb,Mirror");

        [Menu("Ignore if target too far", "If the target is too far away, ignore the items. Default: 1200.")]
        public RangeNode<int> RangeToIgnore { get; set; } = new RangeNode<int>(1200, 1, 3000);
    }

    [Submenu(CollapsedByDefault = true)]
    public class ShockBotSettings
    {
        [Menu("Enable", "This will enable the Shock Bot.")]
        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        [Menu(null, "~1000 as default")] // Keep the default text
        public RangeNode<int> ActionCooldown { get; set; } = new RangeNode<int>(1000, 50, 2000); // Cooldown in milliseconds

        [Menu("Monster Range to Shock", "The minimum range required to shock a monster. Default: 1000.")]
        public RangeNode<int> Range { get; set; } = new RangeNode<int>(1000, 1, 2000);

        [Menu("Ball Lightning Key", "The key to use for Ball Lightning. Default: Q.")]
        public HotkeyNode BallLightningKey { get; set; } = new HotkeyNode(Keys.Q);

        [Menu("Lightning Warp Key", "The key to use for Lightning Warp. Default: W.")]
        public HotkeyNode LightningWarpKey { get; set; } = new HotkeyNode(Keys.W);

        [Menu("Range of the ball to the boss use Lightning Warp", "The range to use Lightning Warp. Default: 600.")]
        public RangeNode<int> RangeToUseLightningWarp { get; set; } = new RangeNode<int>(600, 1, 1000);
    }
}