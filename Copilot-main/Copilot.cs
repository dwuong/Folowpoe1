using System;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using ImGuiNET;

using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using SharpDX;
using System.Collections.Generic;


namespace Copilot
{
    public class Copilot : BaseSettingsPlugin<CopilotSettings>
    {
        private Entity _followTarget;
        private DateTime _nextAllowedActionTime = DateTime.Now; // Cooldown timer
        private DateTime _nextAllowedBlinkTime = DateTime.Now;
        private DateTime _nextAllowedShockTime = DateTime.Now;
        private Vector3 lastTargetPosition = Vector3.Zero; // This will now be SharpDX.Vector3

        private IngameUIElements IngameUi => GameController.IngameState.IngameUi;
        private Element UIRoot => GameController.IngameState.UIRoot;
        private Camera Camera => GameController.Game.IngameState.Camera;
        private AreaInstance CurrentArea => GameController.Area.CurrentArea;
        private List<Entity> EntityList => GameController.EntityListWrapper.OnlyValidEntities;
        private Vector3 PlayerPos => GameController.Player.Pos; // This will now be SharpDX.Vector3

        public override bool Initialise()
        {
            // Initialize plugin
            Name = "Copilot";
            lastTargetPosition = Vector3.Zero;
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
            catch (Exception ex) { }
            base.DrawSettings();
        }

        public override void AreaChange(AreaInstance area)
        {
            lastTargetPosition = Vector3.Zero;
            _followTarget = null;
            // Fix: Changed 'return base.AreaChange(area);' to just call the base method,
            // as AreaChange is a void method and cannot return a value.
            base.AreaChange(area);
        }

        public override void Render()
        {
            if (!GameController.Window.IsForeground()) return;

            // Handle pause/unpause toggle
            if (Settings.TogglePauseHotkey.PressedOnce())
            {
                Settings.IsPaused.Value = !Settings.IsPaused.Value;
            }

            // If paused, disabled, or not ready for the next action, do nothing
            if (!Settings.Enable.Value || Settings.IsPaused.Value || DateTime.Now < _nextAllowedActionTime || GameController.Player == null || GameController.IsLoading)
            {
                return;
            }

            try
            {
                // Check if there are any UI elements blocking the player
                var checkpoint = UIRoot.Children?[1]?.Children?[64];
                var market = UIRoot.Children?[1]?.Children?[27];
                var leftPanel = IngameUi.OpenLeftPanel;
                var rightPanel = IngameUi.OpenRightPanel;
                var worldMap = IngameUi.WorldMap;
                var npcDialog = IngameUi.NpcDialog;

                bool uiBlocking = false;
                if (checkpoint?.IsVisible == true) { uiBlocking = true; }
                if (leftPanel?.IsVisible == true) { uiBlocking = true; }
                if (rightPanel?.IsVisible == true) { uiBlocking = true; }
                if (worldMap?.IsVisible == true) { uiBlocking = true; }
                if (npcDialog?.IsVisible == true) { uiBlocking = true; }
                if (market?.IsVisible == true) { uiBlocking = true; }

                if (uiBlocking)
                {
                    Keyboard.KeyPress(Keys.Space);
                    _nextAllowedActionTime = DateTime.Now.AddMilliseconds(Settings.ActionCooldown.Value);
                    return;
                }

                var resurrectPanel = IngameUi.ResurrectPanel;
                if (resurrectPanel != null && resurrectPanel.IsVisible)
                {
                    var inTown = resurrectPanel?.ResurrectInTown;
                    var atCheckpoint = resurrectPanel?.ResurrectAtCheckpoint;
                    var btn = atCheckpoint ?? inTown; // if inTown is null, use atCheckpoint
                    if (btn != null && btn.IsVisible)
                    {
                        var screenPoint = new System.Drawing.Point((int)btn.GetClientRectCache.Center.X, (int)btn.GetClientRectCache.Center.Y);
                        Mouse.LeftClick(screenPoint, 300);
                    }
                    _nextAllowedActionTime = DateTime.Now.AddMilliseconds(1000);
                    return;
                }

                FollowTarget();
            }
            catch (Exception ex) { }
        }

        private void FollowTarget()
        {
            try
            {
                _followTarget = GetFollowingTarget();
                if (_followTarget == null)
                {
                    var leaderPE = GetLeaderPartyElement();
                    if (leaderPE != null && !leaderPE.ZoneName.Equals(CurrentArea.DisplayName))
                    {
                        FollowUsingPortalOrTpButton(leaderPE);
                    }
                    return;
                }
                if (CurrentArea.IsTown)
                {
                    return;
                }

                var targetPos = _followTarget.Pos;
                if (lastTargetPosition == Vector3.Zero) lastTargetPosition = targetPos;
                var distanceToTarget = Vector3.Distance(PlayerPos, targetPos);

                //* Shock Bot
                if (Settings.ShockBot.Enable.Value && DateTime.Now > _nextAllowedShockTime && ShockBotCode())
                {
                    return;
                }

                //* Pickup
                if (Settings.Pickup.Enable.Value && distanceToTarget <= Settings.Pickup.RangeToIgnore.Value && PickUpItem())
                {
                    return;
                }

                // If within the follow distance, do nothing
                if (distanceToTarget <= Settings.FollowDistance.Value)
                {
                    return;
                }

                // Quick check for "bugged" (shouldn't be open) confirmation tp
                var leaderPE_render = GetLeaderPartyElement(); // Re-get leaderPE if needed in this section
                if (leaderPE_render?.TpButton != null && GetTpConfirmation() != null)
                {
                    Keyboard.KeyPress(Keys.Escape);
                }

                // check if the distance of the target changed significantly from the last position OR if there is a boss near and the distance is less than 2000
                if (distanceToTarget > 3000 /* || (thereIsBossNear && distanceToTarget < 2000) */)
                {
                    ClickBestPortal();
                    _nextAllowedActionTime = DateTime.Now.AddMilliseconds(1000); // Give time for portal transition
                    return;
                }
                else if (Settings.Blink.Enable.Value && DateTime.Now > _nextAllowedBlinkTime && distanceToTarget > Settings.Blink.Range.Value)
                {
                    MoveToward(targetPos);
                    Thread.Sleep(50);
                    Keyboard.KeyPress(Keys.Space);
                    _nextAllowedBlinkTime = DateTime.Now.AddMilliseconds(Settings.Blink.Cooldown.Value);
                }
                else
                {
                    MoveToward(targetPos);
                }

                // Set the cooldown for the next allowed action
                _nextAllowedActionTime = DateTime.Now.AddMilliseconds(Settings.ActionCooldown.Value);
            }
            catch (Exception ex) { }
        }

        private void FollowUsingPortalOrTpButton(PartyElementWindow leaderPE)
        {
            try
            {
                var portal = GetBestPortalLabel();
                const int threshold = 1000;
                var distanceToPortal = portal != null ? Vector3.Distance(PlayerPos, portal.ItemOnGround.Pos) : threshold + 1;

                if (
                    (CurrentArea.IsHideout ||
                        (CurrentArea.Name.Equals("The Temple of Chaos") && leaderPE.ZoneName.Equals("The Trial of Chaos")
                    )) && distanceToPortal <= threshold)
                { // if in hideout and near the portal
                    if (portal != null)
                    {
                        var screenPos = Camera.WorldToScreen(portal.ItemOnGround.Pos);
                        var screenPoint = new System.Drawing.Point((int)screenPos.X, (int)screenPos.Y);
                        Mouse.LeftClick(screenPoint, 500);
                        if (leaderPE?.TpButton != null && GetTpConfirmation() != null) Keyboard.KeyPress(Keys.Escape);
                    }
                }
                else if (leaderPE?.TpButton != null && leaderPE.TpButton.IsVisible) // Added IsVisible check
                {
                    var screenPoint = GetTpButton(leaderPE);
                    Mouse.LeftClick(screenPoint, 100);

                    // Check if the tp confirmation is open
                    var tpConfirmation = GetTpConfirmation();
                    if (tpConfirmation != null)
                    {
                        screenPoint = new System.Drawing.Point((int)tpConfirmation.GetClientRectCache.Center.X, (int)tpConfirmation.GetClientRectCache.Center.Y);
                        Mouse.LeftClick(screenPoint, 100);
                    }
                }
                _nextAllowedActionTime = DateTime.Now.AddMilliseconds(500);
            }
            catch (Exception ex) { }
        }

        private bool ShockBotCode()
        {
            var monster = EntityList
                .Where(e => e.Type == EntityType.Monster && e.IsAlive && (e.Rarity == MonsterRarity.Rare || e.Rarity == MonsterRarity.Unique))
                .OrderBy(e => Vector3.Distance(PlayerPos, e.Pos))
                .FirstOrDefault();
            if (monster != null)
            {
                var distanceToMonster = Vector3.Distance(PlayerPos, monster.Pos);
                if (distanceToMonster <= Settings.ShockBot.Range)
                {
                    var screenPos = Camera.WorldToScreen(monster.Pos);
                    var screenPoint = new System.Drawing.Point((int)screenPos.X, (int)screenPos.Y);
                    Mouse.SetCursorPosition(screenPoint);
                    Thread.Sleep(100);

                    Keyboard.KeyPress(Settings.ShockBot.BallLightningKey.Value);

                    // start tracking the balls
                    var ball = EntityList
                        .Where(e => e.IsDead && e.Metadata == "Metadata/Projectiles/BallLightningPlayer")
                        .OrderBy(e => Vector3.Distance(monster.Pos, e.Pos))
                        .FirstOrDefault();

                    if (ball != null && Vector3.Distance(monster.Pos, ball.Pos) <= Settings.ShockBot.RangeToUseLightningWarp.Value)
                    {
                        var ballScreenPos = Camera.WorldToScreen(ball.Pos);
                        var ballScreenPoint = new System.Drawing.Point((int)ballScreenPos.X, (int)ballScreenPos.Y);
                        Mouse.SetCursorPosition(ballScreenPoint);
                        Thread.Sleep(100);
                        Keyboard.KeyPress(Settings.ShockBot.LightningWarpKey.Value);
                    }

                    _nextAllowedActionTime = DateTime.Now.AddMilliseconds(Settings.ActionCooldown.Value);
                    _nextAllowedShockTime = DateTime.Now.AddMilliseconds(Settings.ShockBot.ActionCooldown.Value);
                    return true;
                }
            }
            _nextAllowedShockTime = DateTime.Now.AddMilliseconds(Settings.ShockBot.ActionCooldown.Value);
            return false;
        }

        private bool PickUpItem()
        {
            var pos = (Settings.Pickup.UseTargetPosition.Value && _followTarget != null) ? _followTarget.Pos : PlayerPos;
            try
            {
                var items = IngameUi.ItemsOnGroundLabelsVisible;
                if (items != null)
                {
                    var filteredItems = Settings.Pickup.Filter.Value.Split(',');
                    var item = items?
                        .OrderBy(x => Vector3.Distance(pos, x.ItemOnGround.Pos))
                        .FirstOrDefault(x => filteredItems.Any(y => x.Label.Text != null && x.Label.Text.Contains(y)));

                    if (item != null)
                    {
                    }
                    else
                    {
                        return false;
                    }

                    var distanceToItem = Vector3.Distance(pos, item.ItemOnGround.Pos);
                    if (distanceToItem <= Settings.Pickup.Range.Value)
                    {
                        var screenPos = Camera.WorldToScreen(item.ItemOnGround.Pos);
                        var screenPoint = new System.Drawing.Point((int)screenPos.X, (int)screenPos.Y);
                        Mouse.LeftClick(screenPoint, 50);
                        _nextAllowedActionTime = DateTime.Now.AddMilliseconds(Settings.ActionCooldown.Value);
                        return true;
                    }
                }
            }
            catch (Exception ex) { }
            return false;
        }

        private Entity GetFollowingTarget()
        {
            try
            {
                var leaderName = Settings.TargetPlayerName.Value.ToLower();
                var target = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player].FirstOrDefault(x => string.Equals(x.GetComponent<Player>()?.PlayerName.ToLower(), leaderName, StringComparison.OrdinalIgnoreCase));
                return target;
            }
            catch (Exception e)
            {
                LogError(e.Message);
                return null;
            }
        }

        private PartyElementWindow GetLeaderPartyElement()
        {
            // Debug: Entering GetLeaderPartyElement
            DebugWindow.LogMsg($"Copilot: Entering GetLeaderPartyElement. Target Player: {Settings.TargetPlayerName.Value}", 1);

            try
            {
                // Safely access the root of the party elements.
                // This path should match where GetPartyList finds individual party elements.
                // IngameUi.PartyElement -> Children[0] -> Children[0] -> Children (this collection holds the "Element" for each player)
                var partyElementList = IngameUi.PartyElement?.Children?[0]?.Children?[0]?.Children;

                // Debug: Log if partyElementList is found or not
                if (partyElementList == null)
                {
                    DebugWindow.LogMsg("Copilot: partyElementList is null or empty. (Party UI not open or structure changed)", 1);
                    return null;
                }
                else
                {
                    DebugWindow.LogMsg($"Copilot: Found {partyElementList.Count()} party element candidates.", 1);
                }

                // Find the specific leader element in the list.
                // Player name is usually the first child (index 0) of each partyElement.
                var leader = partyElementList.FirstOrDefault(partyElement =>
                {
                    var playerNameElement = partyElement?.Children?.ElementAtOrDefault(0); // Assuming player name is at index 0 of the individual partyElement
                    string currentElementName = playerNameElement?.Text;

                    DebugWindow.LogMsg($"Copilot: Checking party element. IsVisible={(partyElement?.IsVisible == true)}. Player Name Candidate: '{currentElementName ?? "N/A"}'", 1);
                    return string.Equals(
                        currentElementName?.ToLower(), // Safely access player name
                        Settings.TargetPlayerName.Value.ToLower(),
                        StringComparison.OrdinalIgnoreCase
                    );
                });

                // If the leader UI element is not found, return null immediately.
                if (leader == null)
                {
                    DebugWindow.LogMsg("Copilot: Leader UI element not found in party list for target player.", 3);
                    return null;
                }

                // --- DEBUGGING ZONE: INSPECT CHILDREN OF THE FOUND LEADER ELEMENT ---
                DebugWindow.LogMsg($"Copilot: Found leader element for '{Settings.TargetPlayerName.Value}'. Inspecting its children:", 1);
                if (leader.Children != null)
                {
                    for (int i = 0; i < leader.Children.Count; i++)
                    {
                        var child = leader.Children.ElementAtOrDefault(i);
                        if (child != null)
                        {
                            DebugWindow.LogMsg($"Copilot: Leader Child[{i}]: Text='{child.Text ?? "N/A"}', IsVisible={child.IsVisible}, ClientRect={child.GetClientRectCache}", 1);
                        }
                        else
                        {
                            DebugWindow.LogMsg($"Copilot: Leader Child[{i}]: NULL", 1);
                        }
                    }
                }
                else
                {
                    DebugWindow.LogMsg("Copilot: Leader element has no children (unexpected).", 3);
                }
                // --- END DEBUGGING ZONE ---


                // Safely extract properties for the PartyElementWindow.
                // Based on previous understanding, player name is Children[0].
                // We need to re-verify indices for ZoneName and TpButton based on your in-game debug output.
                string playerName = leader.Children?.ElementAtOrDefault(0)?.Text;

                // *** IMPORTANT: You need to update these indices based on the DebugWindow output ***
                // Example: If DebugWindow shows Zone Name is at index 2, change ElementAtOrDefault(3) to ElementAtOrDefault(2)
                string zoneName = leader.Children?.ElementAtOrDefault(3)?.Text ?? CurrentArea.DisplayName; // Placeholder: Re-verify this index
                Element tpButton = leader.Children?.ElementAtOrDefault(4); // Placeholder: Re-verify this index

                // Debug: Log the found leader's raw name for verification
                DebugWindow.LogMsg($"Copilot: Leader Party Element Info (Before final creation): Name='{playerName ?? "N/A"}', Zone='{zoneName ?? "N/A"}', TP Button Visible='{(tpButton?.IsVisible == true ? "Yes" : "No")}'", 1);

                // Create the PartyElementWindow object and populate its properties.
                var leaderPartyElement = new PartyElementWindow
                {
                    PlayerName = playerName,
                    TpButton = tpButton,
                    ZoneName = zoneName
                };

                // Debug: Log the retrieved leader information after creating the object
                DebugWindow.LogMsg($"Copilot: Leader Party Element Info (After creation): Name='{leaderPartyElement.PlayerName ?? "N/A"}', Zone='{leaderPartyElement.ZoneName ?? "N/A"}', TP Button Visible='{(leaderPartyElement.TpButton?.IsVisible == true ? "Yes" : "No")}'", 1);

                return leaderPartyElement;
            }
            catch (Exception ex)
            {
                // Debug: Log any exceptions that occur during UI element access
                DebugWindow.LogError($"Copilot: Error in GetLeaderPartyElement: {ex.Message} -- StackTrace: {ex.StackTrace}", 5);
                return null;
            }
        }

        private void GetPartyList()
        {
            var partyElements = new List<string>(); // Use List<string> for dynamic sizing
            try
            {
                // Corrected path to the list of player elements.
                // This path was confirmed from your original working snippet.
                var partyElementList = IngameUi.PartyElement?.Children?[0]?.Children?[0]?.Children;

                if (partyElementList == null)
                {
                    DebugWindow.LogMsg("Copilot: Party UI element list not found or is null (path: IngameUi.PartyElement?.Children?[0]?.Children?[0]?.Children).", 3);
                    return;
                }

                foreach (var partyElement in partyElementList)
                {
                    // PlayerName is found in the first child of the 'partyElement' (index 0)
                    var playerName = partyElement?.Children?[0]?.Text;

                    if (!string.IsNullOrEmpty(playerName) && playerName != "Party") // "Party" can sometimes be a header, filter it out
                    {
                        partyElements.Add(playerName);
                        DebugWindow.LogMsg($"Copilot: Found party member: {playerName}", 1);
                    }
                    else
                    {
                        DebugWindow.LogMsg($"Copilot: Skipped empty or invalid party element name: {playerName ?? "NULL"}", 1);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"Copilot: Error in GetPartyList: {ex.Message} -- StackTrace: {ex.StackTrace}", 5);
            }

            Settings.PartyElements = partyElements.ToArray(); // Convert to array for settings
        }

        private LabelOnGround GetBestPortalLabel()
        {
            try
            {
                var portalLabels =
                    IngameUi.ItemsOnGroundLabelsVisible?.Where(
                                x => x.ItemOnGround.Metadata.ToLower().Contains("areatransition")
                                    || x.ItemOnGround.Metadata.ToLower().Contains("portal")
                                    || x.ItemOnGround.Metadata.ToLower().EndsWith("ultimatumentrance")
                                )
                        .OrderBy(x => Vector3.Distance(lastTargetPosition, x.ItemOnGround.Pos)).ToList();

                // Ensure portalLabels is not null and has elements before proceeding
                if (portalLabels == null || !portalLabels.Any())
                {
                    return null; // No portals found
                }

                var random = new Random();

                LabelOnGround selectedPortal = null;
                if (CurrentArea?.IsHideout != null && CurrentArea.IsHideout)
                {
                    selectedPortal = portalLabels[random.Next(portalLabels.Count)];
                }
                else
                {
                    selectedPortal = portalLabels.FirstOrDefault();
                }
                return selectedPortal;
            }
            catch (Exception ex)
            {
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
                }
            }
            catch (Exception ex) { }
        }

        private System.Drawing.Point GetTpButton(PartyElementWindow leaderPE)
        {
            try
            {
                var windowOffset = GameController.Window.GetWindowRectangle().TopLeft;
                var elemCenter = leaderPE?.TpButton?.GetClientRectCache.Center;
                if (elemCenter.HasValue)
                {
                    var finalPos = new System.Drawing.Point((int)(elemCenter.Value.X + windowOffset.X), (int)(elemCenter.Value.Y + windowOffset.Y));
                    return finalPos;
                }
                return System.Drawing.Point.Empty;
            }
            catch (Exception ex)
            {
                return System.Drawing.Point.Empty;
            }
        }

        private Element GetTpConfirmation()
        {
            try
            {
                // Ensure PopUpWindow and its children exist before accessing
                if (IngameUi.PopUpWindow?.Children?.Count > 0 && IngameUi.PopUpWindow.Children[0]?.Children?.Count > 0)
                {
                    var ui = IngameUi.PopUpWindow.Children[0].Children[0];

                    if (ui.Children.Count > 0 && ui.Children[0].Text.Equals("Are you sure you want to teleport to this player's location?"))
                    {
                        return ui.Children[3].Children[0];
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private void MoveToward(Vector3 targetPos)
        {
            try
            {
                var screenPos = Camera.WorldToScreen(targetPos);
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
                lastTargetPosition = targetPos;
            }
            catch (Exception ex) { }
        }
    }
}

public class PartyElementWindow
{
    public string PlayerName { get; set; } = string.Empty;
    public string ZoneName { get; set; } = string.Empty;
    public Element TpButton { get; set; } = new Element(); // Ensure this is initialized

    public override string ToString()
    {
        string not = TpButton != null ? TpButton.Text : "not ";
        return $"{PlayerName}, current zone: {ZoneName}, and does {not}have tp button";
    }
}