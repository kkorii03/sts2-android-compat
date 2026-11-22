using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace STS2Mobile.Patches;

/// <summary>
/// STS1-mobile look for a tap-selected targeted card (attack on one enemy, ally
/// card). The pinned card moves to the screen centre (x = 50% W, width ~29% H,
/// top edge ~56% H) and the game's own targeting arrow is drawn from it toward
/// the enemy nearest the last touch, or toward the finger while it is down.
/// Visual only: pinning, tapping an enemy to play, the long-press/accidental-play
/// guards and the enemy aim assist all stay in <see cref="MobileTapPreviewPatches"/>.
/// The arrow is borrowed only while vanilla targeting is idle; the moment a real
/// card play or targeting starts, the game owns it again.
/// </summary>
public static class MobileTargetingPreviewPatches
{
    private const float CardWidthScreenFraction = 0.29f;
    private const float CardTopScreenFraction = 0.56f;
    // NCard.defaultSize; used only when the hitbox has no size yet.
    private const float FallbackCardWidth = 300f;
    private const float FallbackCardHeight = 422f;
    // Resting aim when no target exists: straight up from the card.
    private const float IdleAimScreenFraction = 0.4f;

    private static readonly FieldInfo TargetingArrowField = AccessTools.Field(typeof(NTargetManager), "_targetingArrow");
    private static NHandCardHolder _centered;
    private static NTargetingArrow _ownedArrow;
    private static NHandCardHolder _ownedHolder;
    private static Vector2 _lastTouch;
    private static bool _hasLastTouch;
    private static bool _loggedFailure;

    public static void Apply(Harmony harmony)
    {
        try
        {
            var layout = AccessTools.Method(typeof(NPlayerHand), "RefreshLayout");
            if (layout == null)
            {
                PatchHelper.Log("SKIPPED mobile targeting preview: NPlayerHand.RefreshLayout not found");
                return;
            }
            // Last, so the centre placement wins over MobileHandLayoutPatches' row adjustments.
            harmony.Patch(layout,
                prefix: new HarmonyMethod(PatchHelper.Method(typeof(MobileTargetingPreviewPatches), nameof(RefreshLayoutPrefix))) { priority = Priority.Last },
                postfix: new HarmonyMethod(PatchHelper.Method(typeof(MobileTargetingPreviewPatches), nameof(RefreshLayoutPostfix))) { priority = Priority.Last });
            PatchHelper.Log("Patched NPlayerHand.RefreshLayout for mobile targeting preview");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"FAILED mobile targeting preview layout patch: {exception}");
        }
        PatchHelper.Patch(harmony, typeof(NTargetingArrow), "_Process", prefix: PatchHelper.Method(typeof(MobileTargetingPreviewPatches), nameof(ArrowProcessPrefix)));
    }

    // Vanilla RefreshLayout snaps the focused card's Y to the in-hand lift every
    // time it runs; remember where the centred card already was so it does not jump.
    public static void RefreshLayoutPrefix(NPlayerHand __instance, out Vector2? __state)
    {
        __state = null;
        try
        {
            if (_centered != null && ShouldCenter(__instance, out var holder) && ReferenceEquals(holder, _centered))
                __state = holder.Position;
        }
        catch
        {
            __state = null;
        }
    }

    public static void RefreshLayoutPostfix(NPlayerHand __instance, Vector2? __state)
    {
        try
        {
            if (!ShouldCenter(__instance, out var holder) || holder.GetParent() is not Control container || !container.IsInsideTree())
            {
                _centered = null;
                return;
            }
            var visible = container.GetViewport().GetVisibleRect();
            var transform = container.GetGlobalTransformWithCanvas();
            var containerScale = Mathf.Abs(transform.Scale.X);
            if (containerScale < 0.001f || visible.Size.Y <= 0f)
            {
                _centered = null;
                return;
            }
            var hitboxSize = holder.Hitbox?.Size ?? Vector2.Zero;
            var baseWidth = hitboxSize.X > 1f ? hitboxSize.X : FallbackCardWidth;
            var baseHeight = hitboxSize.Y > 1f ? hitboxSize.Y : FallbackCardHeight;
            var scale = visible.Size.Y * CardWidthScreenFraction / (baseWidth * containerScale);
            var cardHeight = baseHeight * containerScale * scale;
            var centerY = visible.Position.Y + visible.Size.Y * CardTopScreenFraction + cardHeight * 0.5f;
            centerY = Mathf.Min(centerY, visible.End.Y - cardHeight * 0.5f);
            var screenCenter = new Vector2(visible.Position.X + visible.Size.X * 0.5f, centerY);
            var local = transform.AffineInverse() * screenCenter;

            if (__state.HasValue && ReferenceEquals(holder, _centered))
                holder.Position = __state.Value;
            holder.SetAngleInstantly(0f);
            holder.SetScaleInstantly(Vector2.One * scale);
            holder.SetTargetPosition(local);
            _centered = holder;
        }
        catch (Exception exception)
        {
            _centered = null;
            LogFailureOnce($"Mobile targeting preview layout failed: {exception.Message}");
        }
    }

    public static void ArrowProcessPrefix(NTargetingArrow __instance)
    {
        try
        {
            SyncArrow(__instance);
        }
        catch (Exception exception)
        {
            LogFailureOnce($"Mobile targeting preview arrow failed: {exception.Message}");
            ReleaseArrow(stopDrawing: true);
        }
    }

    private static void SyncArrow(NTargetingArrow arrow)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !GodotObject.IsInstanceValid(hand))
        {
            if (ReferenceEquals(arrow, _ownedArrow))
                ReleaseArrow(stopDrawing: true);
            return;
        }
        if (MobileInputPolicyPatches.UseTouchControls && Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _lastTouch = hand.GetViewport().GetMousePosition();
            _hasLastTouch = true;
        }

        var manager = GetTargetManager();
        if (manager == null || !ReferenceEquals(TargetingArrowField?.GetValue(manager), arrow))
            return;
        if (manager.IsInSelection || hand.InCardPlay)
        {
            // A real targeting/card play took the arrow over; it starts and stops it itself.
            _ownedArrow = null;
            _ownedHolder = null;
            return;
        }

        NHandCardHolder holder = null;
        var wanted = MobileTapPreviewPatches.IsCombatInputActive()
            && ShouldCenter(hand, out holder)
            && ReferenceEquals(holder, _centered)
            && holder.CardNode != null
            && holder.CardNode.IsVisibleInTree();
        if (!wanted)
        {
            if (ReferenceEquals(arrow, _ownedArrow))
                ReleaseArrow(stopDrawing: true);
            return;
        }

        if (!ReferenceEquals(arrow, _ownedArrow) || !ReferenceEquals(holder, _ownedHolder))
        {
            // Controller-style drawing: the arrow does not follow the mouse or hide
            // the cursor, and eases toward whatever end point is set each frame.
            arrow.StartDrawingFrom(holder.CardNode, true);
            arrow.SetHighlightingOff();
            _ownedArrow = arrow;
            _ownedHolder = holder;
        }
        arrow.UpdateDrawingTo(GetArrowEnd(hand, holder));
    }

    private static void ReleaseArrow(bool stopDrawing)
    {
        var arrow = _ownedArrow;
        _ownedArrow = null;
        _ownedHolder = null;
        if (!stopDrawing || arrow == null || !GodotObject.IsInstanceValid(arrow))
            return;
        var manager = GetTargetManager();
        if (manager != null && manager.IsInSelection)
            return; // Never stop an arrow the game is using.
        arrow.StopDrawing();
    }

    private static NTargetManager GetTargetManager()
    {
        try
        {
            var manager = NTargetManager.Instance;
            return manager != null && GodotObject.IsInstanceValid(manager) ? manager : null;
        }
        catch
        {
            return null; // No run (main menu): NRun.Instance is null.
        }
    }

    private static bool ShouldCenter(NPlayerHand hand, out NHandCardHolder holder)
    {
        holder = null;
        if (hand == null || !GodotObject.IsInstanceValid(hand) || !MobileTapPreviewPatches.IsTapPreviewEnabled())
            return false;
        if (MobileTapPreviewPatches.GetPinnedHolder(hand) is not NHandCardHolder pinned || !GodotObject.IsInstanceValid(pinned) || !pinned.IsInsideTree())
            return false;
        if (!MobileTapPreviewPatches.IsSingleCreatureTarget(pinned.CardNode?.Model))
            return false;
        if (hand.CurrentMode != NPlayerHand.Mode.Play || hand.InCardPlay || hand.IsAwaitingPlay(pinned) || hand.PeekButton.IsPeeking)
            return false;
        if (MobileHandScrubPatches.IsScrubbing || !ReferenceEquals(hand.FocusedHolder, pinned))
            return false;
        holder = pinned;
        return true;
    }

    // Finger down: the finger, snapped to a valid target under/near it.
    // Finger up: the valid target nearest the last touch, else straight up.
    private static Vector2 GetArrowEnd(NPlayerHand hand, NHandCardHolder holder)
    {
        var visible = hand.GetViewport().GetVisibleRect();
        var model = holder.CardNode.Model;
        if (MobileInputPolicyPatches.UseTouchControls && Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var finger = hand.GetViewport().GetMousePosition();
            var near = FindTarget(model, finger, visible.Size.Y * MobileTapPreviewPatches.TargetAssistScreenFraction);
            return near != null ? AimPoint(near) : finger;
        }
        var anchor = _hasLastTouch ? _lastTouch : holder.CardNode.GlobalPosition;
        var nearest = FindTarget(model, anchor, float.MaxValue);
        if (nearest != null)
            return AimPoint(nearest);
        return new Vector2(visible.Position.X + visible.Size.X * 0.5f, visible.Position.Y + visible.Size.Y * IdleAimScreenFraction);
    }

    // Same candidate rules as the tap assist in MobileTapPreviewPatches.
    private static NCreature FindTarget(CardModel model, Vector2 position, float maxDistance)
    {
        var room = NCombatRoom.Instance;
        if (model == null || room == null)
            return null;
        NCreature best = null;
        var bestDistance = maxDistance;
        foreach (var creature in room.CreatureNodes)
        {
            if (creature == null || !GodotObject.IsInstanceValid(creature) || !creature.IsVisibleInTree())
                continue;
            var hitbox = creature.Hitbox;
            if (hitbox == null || !hitbox.IsVisibleInTree() || hitbox.MouseFilter == Control.MouseFilterEnum.Ignore)
                continue;
            if (creature.Entity is not { IsHittable: true } entity || !MobileTapPreviewPatches.CanTargetPinnedCard(model, entity))
                continue;
            var distance = MobileTapPreviewPatches.DistanceToRect(MobileTapPreviewPatches.ScreenRect(hitbox), position);
            if (distance <= bestDistance)
            {
                best = creature;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static Vector2 AimPoint(NCreature creature)
    {
        try
        {
            return creature.VfxSpawnPosition;
        }
        catch
        {
            var rect = MobileTapPreviewPatches.ScreenRect(creature.Hitbox);
            return rect.Position + rect.Size * 0.5f;
        }
    }

    private static void LogFailureOnce(string message)
    {
        if (_loggedFailure)
            return;
        _loggedFailure = true;
        PatchHelper.Log(message);
    }
}
