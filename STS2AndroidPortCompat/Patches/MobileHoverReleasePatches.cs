using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace STS2Mobile.Patches;

/// <summary>
/// Touch leaves Godot's emulated mouse parked where the finger lifted, so
/// hover-only UI (the creature nameplate, button glows) stays up after a tap.
/// After a still single-finger tap, the pointer is moved off screen on the next
/// frame, exactly as a mouse leaving the window would, unless the hover is the
/// state the tap asked for (see <see cref="MobileHoverReleasePolicy"/>), an icon
/// tooltip is tap-pinned, targeting/card play is running or tooltips are in
/// immediate (hover) mode. Drags, flings and two-finger gestures are never touched.
/// </summary>
public static class MobileHoverReleasePatches
{
    private const int EmulatedMouseDevice = -1;
    private const int AncestorDepth = 6;
    // Far outside any window or canvas; positive so no "drag upward" check can fire.
    private static readonly Vector2 ParkedPointer = new(100000f, 100000f);
    private static readonly MobileHoverReleaseTracker Tracker = new();
    private static bool _logged;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NGame), "_Input", postfix: PatchHelper.Method(typeof(MobileHoverReleasePatches), nameof(GameInputPostfix)));
    }

    public static void Reset()
    {
        Tracker.Reset();
    }

    // A postfix runs even when an earlier _Input prefix consumed the event.
    public static void GameInputPostfix(InputEvent inputEvent)
    {
        try
        {
            if (!MobileInputPolicyPatches.UseTouchControls)
            {
                Tracker.Reset();
                return;
            }
            switch (inputEvent)
            {
                case InputEventScreenTouch touch:
                    if (touch.Pressed && !touch.Canceled)
                        Tracker.TouchPressed(touch.Index);
                    else
                        Tracker.TouchReleased(touch.Index);
                    break;
                case InputEventScreenDrag drag:
                    Tracker.Moved(drag.Position.X, drag.Position.Y);
                    break;
                case InputEventMouseMotion motion when MobileInputPolicyPatches.IsTouchPointerEvent(motion):
                    Tracker.Moved(motion.Position.X, motion.Position.Y);
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left } button when MobileInputPolicyPatches.IsTouchPointerEvent(button):
                    if (button.Pressed)
                    {
                        Tracker.MousePressed(button.Position.X, button.Position.Y);
                    }
                    else if (Tracker.MouseReleased(button.Canceled))
                    {
                        var serial = Tracker.Serial;
                        // End of this frame: every release handler (tooltip pin, card play,
                        // button press) has run; the parked motion is delivered next frame.
                        Callable.From(() => ClearHoverIfStillIdle(serial)).CallDeferred();
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile hover release tracking failed: {exception.Message}");
            Tracker.Reset();
        }
    }

    private static void ClearHoverIfStillIdle(ulong serial)
    {
        try
        {
            var game = NGame.Instance;
            if (game == null || !GodotObject.IsInstanceValid(game) || !game.IsInsideTree())
                return;
            var viewport = game.GetViewport();
            if (viewport == null)
                return;
            if (!MobileHoverReleasePolicy.ShouldClear(
                    MobileInputPolicyPatches.UseTouchControls,
                    MobileTooltipPatches.UsesHoverTooltips(),
                    Input.IsMouseButtonPressed(MouseButton.Left),
                    serial == Tracker.Serial,
                    IsTargetingOrCardPlay(),
                    MobileTooltipPatches.HasStickyTooltip(),
                    GetHoveredChain(viewport)))
                return;
            Input.ParseInputEvent(new InputEventMouseMotion
            {
                Device = EmulatedMouseDevice,
                Position = ParkedPointer,
                GlobalPosition = ParkedPointer,
            });
            if (!_logged)
            {
                _logged = true;
                PatchHelper.Log("Touch tap released: parked the emulated pointer off screen to clear leftover hover.");
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile hover release clear failed: {exception.Message}");
        }
    }

    private static bool IsTargetingOrCardPlay()
    {
        try
        {
            var hand = NPlayerHand.Instance;
            if (hand == null || !GodotObject.IsInstanceValid(hand))
                return false; // Card/potion targeting only exists in combat.
            if (hand.InCardPlay)
                return true;
            var targeting = NTargetManager.Instance;
            return targeting != null && targeting.IsInSelection;
        }
        catch
        {
            // No run (menus): NTargetManager.Instance needs NRun.
            return false;
        }
    }

    private static List<string> GetHoveredChain(Viewport viewport)
    {
        Node node = viewport.GuiGetHoveredControl();
        if (node == null || !GodotObject.IsInstanceValid(node))
            return null;
        var chain = new List<string>(AncestorDepth + 1);
        for (var depth = 0; depth <= AncestorDepth && node != null && GodotObject.IsInstanceValid(node); depth++)
        {
            chain.Add(node.GetType().Name);
            node = node.GetParent();
        }
        return chain;
    }
}
