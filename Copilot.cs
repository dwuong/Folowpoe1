using System;
using System.Linq;
using System.Drawing;
using System.Threading; // Keep Threading for Mouse.LeftClick delay if used
using System.Windows.Forms;
using ImGuiNET;
using SharpDX;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Collections.Generic;
using SystemNumerics = System.Numerics;

namespace Copilot
{
    public class Copilot : BaseSettingsPlugin<CopilotSettings>
    {
        private Entity _followTarget;
        private DateTime _nextAllowedActionTime = DateTime.Now; // Cooldown timer for all actions
        private DateTime _nextAllowedBlinkTime = DateTime.Now;
        private SystemNumerics.Vector3 lastTargetPosition = SystemNumerics.Vector3.Zero;

        // New: Track the time when the last area change occurred
        private DateTime _lastAreaChangeTime = DateTime.Now;
        // Hardcoded delay after an area change (3 seconds as requested)
        private const int AREA_CHANGE_DELAY_MS = 3000;

        // Properties to get game data
        private IngameUIElements IngameUi => GameController.IngameState.IngameUi;
        private Element UIRoot => GameController.IngameState.UIRoot;
        private Camera Camera => GameController.Game.IngameState.Camera;
        private AreaInstance CurrentArea => GameController.Area.CurrentArea;
        private List<Entity> EntityList => GameController.EntityListWrapper.OnlyValidEntities;
        private SharpDX.Vector3 PlayerSharpDxPosition => GameController.Player.Pos;

        // Helper to convert SharpDX.Vector3 to System.Numerics.Vector3
        private SystemNumerics.Vector3 ToSystemNumerics(SharpDX.Vector3 v) => new System.Numerics.Vector3(v.X, v.Y, v.Z);
        // Helper to convert SharpDX.Vector2 to System.Numerics.Vector2
        private SystemNumerics.Vector2 ToSystemNumerics(SharpDX.Vector2 v) => new System.Numerics.Vector2(v.X, v.Y);


        public override bool Initialise()
        {
            Name = "Copilot";
            lastTargetPosition = SystemNumerics.Vector3.Zero;
            return base.Initialise();
        }

        public override void DrawSettings()
        {
            try
            {
                if (ImGui.Button("Get Party List")) GetPartyList();

                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Get the list of party members");

                // draw the party list
                ImGui.Text("Party List:");
                var i = 0;
                foreach (var playerName in Settings.PartyElements)
                {
                    if (string.IsNullOrEmpty(playerName)) continue;
                    if (i > 0) ImGui.SameLine();
                    i++;
                    if (ImGui.Button("Set " + playerName + " as target"))
                        Settings.TargetPlayerName.Value = playerName;
                }
                if (i == 0) ImGui.Text("No party members found");
            }
            catch (Exception) { /* Handle exceptions silently */ }
            base.DrawSettings();
        }

        // --- Core Change: AreaChange now sets a long cooldown ---
        public override void AreaChange(AreaInstance area)
        {
            lastTargetPosition = SystemNumerics.Vector3.Zero;
            _followTarget = null;

            // When the area changes, set a mandatory delay before any actions can proceed.
            // This covers loading screens and game stabilization.
            _lastAreaChangeTime = DateTime.Now;
            _nextAllowedActionTime = DateTime.Now.AddMilliseconds(AREA_CHANGE_DELAY_MS); // Use hardcoded 3 seconds

            base.AreaChange(area);
        }

        public override void Render()
        {
            if (!GameController.Window.IsForeground()) return;

            if (Settings.TogglePauseHotkey.PressedOnce()) Settings.IsPaused.Value = !Settings.IsPaused.Value;

            // --- Key Cooldown Check: Prevent actions immediately after area change or during loading ---
            // If the plugin is paused, or the game is loading, or we are within the AREA_CHANGE_DELAY_MS period, return early.
            // The _nextAllowedActionTime is set to the current time + AREA_CHANGE_DELAY_MS in AreaChange.
            if (Settings.IsPaused.Value || GameController.IsLoading || DateTime.Now < _nextAllowedActionTime)
            {
                return;
            }

            // Optional: An extra check for clarity, ensuring total elapsed time since area change is sufficient.
            // This works in conjunction with _nextAllowedActionTime set in AreaChange.
            if ((DateTime.Now - _lastAreaChangeTime).TotalMilliseconds < AREA_CHANGE_DELAY_MS)
            {
                return;
            }

            if (!Settings.Enable.Value || GameController.Player == null) return;

            try
            {
                // UI Panel handling: close them if visible by pressing space
                var checkpoint = UIRoot.Children?[1]?.Children?[64];
                var market = UIRoot.Children?[1]?.Children?[27];
                var leftPanel = IngameUi.OpenLeftPanel;
                var rightPanel = IngameUi.OpenRightPanel;
                var worldMap = IngameUi.WorldMap;
                var npcDialog = IngameUi.NpcDialog;

                if ((checkpoint?.IsVisible ?? false) ||
                    (leftPanel?.IsVisible ?? false) ||
                    (rightPanel?.IsVisible ?? false) ||
                    (worldMap?.IsVisible ?? false) ||
                    (npcDialog?.IsVisible ?? false) ||
                    (market?.IsVisible ?? false))
                {
                    Keyboard.KeyPress(Keys.Space);
                    // Standard action cooldown applies here
                    _nextAllowedActionTime = DateTime.Now.AddMilliseconds(Settings.ActionCooldown.Value);
                    return;
                }

                // Resurrect panel handling
                var resurrectPanel = IngameUi.ResurrectPanel;
                if (resurrectPanel != null && resurrectPanel.IsVisible)
                {
                    var inTown = resurrectPanel?.ResurrectInTown;
                    var atCheckpoint = resurrectPanel?.ResurrectAtCheckpoint;
                    var btn = atCheckpoint ?? inTown;
                    if (btn != null && btn.IsVisible)
                    {
                        var rect = btn.GetClientRectCache;
                        var screenPoint = new System.Drawing.Point((int)rect.Center.X, (int)rect.Center.Y);
                        Mouse.LeftClick(screenPoint, 300);
                        // After resurrecting, treat it like an area change for cooldown purposes
                        _lastAreaChangeTime = DateTime.Now;
                        _nextAllowedActionTime = DateTime.Now.AddMilliseconds(AREA_CHANGE_DELAY_MS); // Use hardcoded 3 seconds
                    }
                    return;
                }

                FollowTarget();
            }
            catch (Exception e) { LogError($"Render Error: {e.Message}"); }
        }

        private void FollowTarget()
        {
            try
            {
                _followTarget = GetFollowingTarget();

                if (_followTarget == null)
                {
                    var portal = GetBestPortalLabel();
                    if (portal != null)
                    {
                        ClickBestPortal();
                        return;
                    }

                    var tpConfirmButton = GetTpConfirmation();
                    if (tpConfirmButton != null)
                    {
                        var rect = tpConfirmButton.GetClientRectCache;
                        var screenPoint = new System.Drawing.Point((int)rect.Center.X, (int)rect.Center.Y);
                        Mouse.LeftClick(screenPoint, 300);
                        _nextAllowedActionTime = DateTime.Now.AddMilliseconds(Settings.ActionCooldown.Value);
                        return;
                    }
                    return;
                }

                if (CurrentArea.IsTown)
                {
                    return;
                }

                SystemNumerics.Vector3 playerPosSystemNum = ToSystemNumerics(PlayerSharpDxPosition);
                SystemNumerics.Vector3 targetPosSystemNum = ToSystemNumerics(_followTarget.Pos);

                if (lastTargetPosition == SystemNumerics.Vector3.Zero) lastTargetPosition = targetPosSystemNum;
                var distanceToTarget = SystemNumerics.Vector3.Distance(playerPosSystemNum, targetPosSystemNum);

                if (distanceToTarget <= Settings.FollowDistance.Value)
                {
                    return;
                }

                if (distanceToTarget > 4000)
                {
                    ClickBestPortal();
                    return;
                }
                else if (Settings.Blink.Enable.Value && DateTime.Now > _nextAllowedBlinkTime && distanceToTarget > Settings.Blink.Range.Value)
                {
                    MoveToward(targetPosSystemNum);
                    Thread.Sleep(50); // Small sleep for mouse input to register before key press
                    Keyboard.KeyPress(Keys.E);
                    _nextAllowedBlinkTime = DateTime.Now.AddMilliseconds(Settings.Blink.Cooldown.Value);
                }
                else
                {
                    MoveToward(targetPosSystemNum);
                }
                _nextAllowedActionTime = DateTime.Now.AddMilliseconds(Settings.ActionCooldown.Value);
            }
            catch (Exception e) { LogError($"FollowTarget Error: {e.Message}"); }
        }

        private Entity GetFollowingTarget()
        {
            try
            {
                var leaderName = Settings.TargetPlayerName.Value.ToLower();
                var target = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player]
                                .FirstOrDefault(x => x.GetComponent<Player>()?.PlayerName.Equals(leaderName, StringComparison.OrdinalIgnoreCase) ?? false);

                return target;
            }
            catch (Exception e)
            {
                LogError($"GetFollowingTarget Error: {e.Message}");
                return null;
            }
        }

        private LabelOnGround GetBestPortalLabel()
        {
            try
            {
                var portalLabels = IngameUi.ItemsOnGroundLabelsVisible?
                    .Where(x => x?.ItemOnGround?.Metadata != null &&
                                        (x.ItemOnGround.Metadata.ToLower().Contains("areatransition") ||
                                        x.ItemOnGround.Metadata.ToLower().Contains("portal") ||
                                        x.ItemOnGround.Metadata.ToLower().EndsWith("ultimatumentrance")))
                    .OrderBy(x => SystemNumerics.Vector3.Distance(lastTargetPosition, ToSystemNumerics(x.ItemOnGround.Pos)))
                    .ToList();

                if (portalLabels == null || !portalLabels.Any())
                {
                    return null;
                }

                if (CurrentArea?.IsHideout == true)
                {
                    var random = new Random();
                    return portalLabels[random.Next(portalLabels.Count)];
                }

                return portalLabels.FirstOrDefault();
            }
            catch (Exception e)
            {
                LogError($"GetBestPortalLabel Error: {e.Message}");
                return null;
            }
        }

        private void ClickBestPortal()
        {
            try
            {
                var portal = GetBestPortalLabel();
                if (portal != null)
                {
                    var screenPos = Camera.WorldToScreen(portal.ItemOnGround.Pos);
                    var screenPoint = new System.Drawing.Point((int)screenPos.X, (int)screenPos.Y);

                    Mouse.LeftClick(screenPoint, 300);

                    // A short cooldown to prevent immediate re-clicks before AreaChange is triggered.
                    // The main 3-second delay is handled by AreaChange updating _nextAllowedActionTime.
                    _nextAllowedActionTime = DateTime.Now.AddMilliseconds(4000);
                }
            }
            catch (Exception e) { LogError($"ClickBestPortal Error: {e.Message}"); }
        }

        private void GetPartyList()
        {
            var partyElements = new List<string>();
            try
            {
                var partyElementList = IngameUi.PartyElement?.Children?[0]?.Children?[0]?.Children;
                if (partyElementList == null) return;

                foreach (var partyElement in partyElementList)
                {
                    var playerName = partyElement?.Children?[0]?.Text;
                    if (!string.IsNullOrEmpty(playerName))
                    {
                        partyElements.Add(playerName);
                    }
                }
            }
            catch (Exception e) { LogError($"GetPartyList Error: {e.Message}"); }

            Settings.PartyElements = partyElements.ToArray();
        }

        private Element GetTpConfirmation()
        {
            try
            {
                var popUp = IngameUi.PopUpWindow;
                if (popUp == null || !popUp.IsVisible || popUp.Children.Count == 0) return null;

                var firstChild = popUp.Children[0];
                if (firstChild == null || firstChild.Children.Count == 0) return null;

                var ui = firstChild.Children[0];
                if (ui == null || ui.Children.Count < 4) return null;

                if (ui.Children[0]?.Text?.Equals("Are you sure you want to teleport to this player's location?", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var buttonContainer = ui.Children[3];
                    if (buttonContainer?.Children.Count > 0)
                    {
                        return buttonContainer.Children[0];
                    }
                }
                return null;
            }
            catch (Exception e)
            {
                LogError($"GetTpConfirmation Error: {e.Message}");
                return null;
            }
        }

        private void MoveToward(SystemNumerics.Vector3 targetPosSystemNum)
        {
            try
            {
                SharpDX.Vector3 targetPosSharpDx = new SharpDX.Vector3(targetPosSystemNum.X, targetPosSystemNum.Y, targetPosSystemNum.Z);
                var screenPos = Camera.WorldToScreen(targetPosSharpDx);
                var screenPoint = new System.Drawing.Point((int)screenPos.X, (int)screenPos.Y);

                Mouse.SetCursorPosition(screenPoint);
                if (Settings.Additional.UseMouse.Value)
                {
                    Mouse.LeftClick(screenPoint, 20);
                }
                else
                {
                    Keyboard.KeyPress(Keys.T);
                }

                lastTargetPosition = targetPosSystemNum;
            }
            catch (Exception e) { LogError($"MoveToward Error: {e.Message}"); }
        }
    }
}
