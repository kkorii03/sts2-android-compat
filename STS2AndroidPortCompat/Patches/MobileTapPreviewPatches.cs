using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class MobileTapPreviewPatches
{
    private const float DirectDragThreshold = 30f;
    // Guarded play-zone tap: decided on release, never on press. A hold this long
    // is a long-press (read info), not a tap; same floor as the tooltip long-press.
    private const ulong TapMaxDurationUsec = 500_000;
    private const float TapSlop = 30f;
    // A press on an NClickableControl at least this share of the screen is a
    // backdrop, not a button, and does not block the tap.
    private const float ClaimMaxScreenShare = 0.25f;
    // Enemy tap assist radius as a share of the visible height (~76 px at 1080p,
    // about 5 mm on a phone), so it follows the UI scale instead of raw pixels.
    internal const float TargetAssistScreenFraction = 0.07f;
    private const float PlayZoneMaxScreenFraction = 0.75f;
    // ~43 px at 1080p: the no-op strip just above the resting hand.
    private const float HandDeadBandScreenFraction = 0.04f;
    private static readonly GuardedTap PendingTap = new();
    private static ulong _lastTargetPressUsec;
    private static readonly MethodInfo FinishTargetingMethod = AccessTools.Method(typeof(NTargetManager), "FinishTargeting", new[] { typeof(bool) });
    private static readonly FieldInfo TargetModeField = AccessTools.Field(typeof(NTargetManager), "_targetMode");
    private static readonly ConditionalWeakTable<NPlayerHand, PreviewState> States = new();
    private static readonly ConditionalWeakTable<NCardHolder, PinnedState> PinnedHolders = new();
    private static readonly ConditionalWeakTable<NCardHolder, SuppressedHoverState> SuppressedHolders = new();
    // Keyed by the node itself (not a truncated instance id), so a freed node
    // can never make a new one look already hooked, and entries die with the node.
    private static readonly ConditionalWeakTable<NCreature, object> ConnectedCreatures = new();
    private static readonly ConditionalWeakTable<NHandCardHolder, object> ConnectedHandHolders = new();
    private static readonly object Hooked = new();
    private static readonly MethodInfo StartCardPlayMethod = typeof(NPlayerHand).GetMethod("StartCardPlay", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(NHandCardHolder), typeof(bool) }, null);
    private static readonly Func<UnplayableReason, AbstractModel, LocString> GetPlayerDialogueLine =
        AccessTools.Method(typeof(CardModel).Assembly.GetType("MegaCrit.Sts2.Core.Entities.Cards.UnplayableReasonExtensions"), "GetPlayerDialogueLine")
            .CreateDelegate<Func<UnplayableReason, AbstractModel, LocString>>();

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NGame), "_Input", prefix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(GameInputPrefix)));
        PatchHelper.Patch(harmony, typeof(NPlayerHand), "OnHolderPressed", prefix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(OnHolderPressedPrefix)));
        PatchHelper.Patch(harmony, typeof(NPlayerHand), "OnHolderFocused", prefix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(OnHolderFocusedPrefix)));
        PatchHelper.Patch(harmony, typeof(NPlayerHand), "StartCardPlay", prefix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(StartCardPlayPrefix)));
        PatchHelper.Patch(harmony, typeof(NPlayerHand), "CancelAllCardPlay", prefix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(ClearPinnedPrefix)));
        PatchHelper.Patch(harmony, typeof(NPlayerHand), "OnCombatEnded", prefix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(ClearPinnedPrefix)));
        PatchHelper.Patch(harmony, typeof(NCardHolder), "RefreshFocusState", prefix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(CardHolderRefreshFocusStatePrefix)));
        PatchHelper.Patch(harmony, typeof(NHandCardHolder), "_Ready", postfix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(HandHolderReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NCreature), "_Ready", postfix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(CreatureReadyPostfix)));
        PatchHelper.Patch(harmony, typeof(NClickableControl), "_GuiInput", prefix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(ClickableGuiInputPrefix)));
        PatchHelper.Patch(harmony, typeof(NTargetManager), "_Input", prefix: PatchHelper.Method(typeof(MobileTapPreviewPatches), nameof(TargetManagerInputPrefix)));
    }

    public static bool GameInputPrefix(NGame __instance, InputEvent inputEvent)
    {
        try
        {
            if (!MobileInputPolicyPatches.UseTouchControls || !MobileInputPolicyPatches.IsTouchPointerEvent(inputEvent))
                return true;
            var hand = NPlayerHand.Instance;
            if (hand == null || !IsCombatInputActive())
            {
                PendingTap.Reset();
                // A re-tap put-down or direct drag armed before the screen changed
                // must not consume the next unrelated release or drag.
                if (hand != null)
                    CancelTapGestureForScrub(hand);
                return true;
            }
            var isButton = inputEvent is InputEventMouseButton or InputEventScreenTouch;
            var guarded = isButton && IsTapGuardEnabled();
            if (guarded)
                TrackGuardedTap(hand, inputEvent);
            else if (isButton)
                PendingTap.Reset();
            if (HandleMobileTapPreviewDirectDrag(hand, inputEvent) || HandlePendingPutDownRelease(hand, inputEvent) || (!guarded && HandleMobilePinnedCardPlayZoneTap(hand, inputEvent)))
            {
                __instance.GetViewport()?.SetInputAsHandled();
                return false;
            }
            // Puts the pinned card down but never consumes the press: End Turn and
            // the draw/discard piles sit below the play zone and still get it.
            HandleMobilePinnedCardCancelTap(hand, inputEvent);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tap preview global input failed: {exception.Message}");
        }
        return true;
    }

    public static bool OnHolderPressedPrefix(NPlayerHand __instance, NCardHolder holder)
    {
        try
        {
            if (!ShouldHandle(__instance, holder))
                return true;

            var handHolder = (NHandCardHolder)holder;
            var state = States.GetOrCreateValue(__instance);
            if (IsSameValidHolder(state.PinnedHolder, handHolder))
            {
                switch (GetRetapAction())
                {
                    case RetapAction.Play:
                        ClearDirectDrag(state);
                        state.PendingPutDownHolder = null;
                        if (ShouldPlayPinnedCardOnTap(handHolder.CardNode?.Model))
                        {
                            TryPlayPinnedCardOnTap(__instance, handHolder);
                            return false;
                        }
                        ClearPinned(__instance, keepDragCandidate: true);
                        return true;
                    case RetapAction.None:
                        ClearDirectDrag(state);
                        state.PendingPutDownHolder = null;
                        return false;
                    case RetapAction.PutDown:
                    default:
                        state.PendingPutDownHolder = handHolder;
                        ArmDirectDrag(__instance, handHolder);
                        return false;
                }
            }

            state.PendingPutDownHolder = null;
            SetPinned(__instance, handHolder, armDirectDrag: true);
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile tap preview failed; falling back to original press: {exception.Message}");
            return true;
        }
    }

    public static bool OnHolderFocusedPrefix(NPlayerHand __instance, NHandCardHolder holder)
    {
        try
        {
            if (!IsTapPreviewEnabled() || holder == null || IsPinned(holder) || !SuppressedHolders.TryGetValue(holder, out _))
                return true;
            ClearFocusedHolderIfMatches(__instance, holder);
            RefreshLayout(__instance);
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Suppressed hand-card focus failed: {exception.Message}");
            return true;
        }
    }

    public static void StartCardPlayPrefix(NPlayerHand __instance)
    {
        ClearPinned(__instance, keepDragCandidate: true);
    }

    public static void ClearPinnedPrefix(NPlayerHand __instance)
    {
        ClearPinned(__instance);
    }

    public static bool CardHolderRefreshFocusStatePrefix(NCardHolder __instance)
    {
        if (!IsTapPreviewEnabled())
            return true;
        if (PinnedHolders.TryGetValue(__instance, out _))
        {
            try
            {
                var isFocusedField = Members.IsFocused;
                if (isFocusedField?.GetValue(__instance) is not true)
                {
                    isFocusedField?.SetValue(__instance, true);
                    InvokeDoCardHoverEffects(__instance, true);
                }
                if (__instance.CardModel != null)
                    RunManager.Instance.HoveredModelTracker.OnLocalCardHovered(__instance.CardModel);
                return false;
            }
            catch (Exception exception)
            {
                PatchHelper.Log($"Pinned card refresh failed: {exception.Message}");
                return true;
            }
        }

        if (SuppressedHolders.TryGetValue(__instance, out _))
        {
            try
            {
                ForceHolderUnfocused(__instance);
                return false;
            }
            catch (Exception exception)
            {
                PatchHelper.Log($"Suppressed card hover refresh failed: {exception.Message}");
                return true;
            }
        }

        return true;
    }

    public static void HandHolderReadyPostfix(NHandCardHolder __instance)
    {
        try
        {
            var hitbox = __instance.Hitbox;
            if (hitbox == null || ConnectedHandHolders.TryGetValue(__instance, out _))
                return;
            ConnectedHandHolders.Add(__instance, Hooked);
            hitbox.Connect(NClickableControl.SignalName.MouseReleased, Callable.From<InputEvent>(inputEvent => OnHandHolderMouseReleased(__instance, inputEvent)));
            hitbox.Connect(Control.SignalName.MouseExited, Callable.From(() => OnHandHolderHitboxMouseExited(__instance)));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Hand-card release/exit bridge failed: {exception.Message}");
        }
    }

    public static void CreatureReadyPostfix(NCreature __instance)
    {
        try
        {
            var hitbox = __instance.Hitbox;
            if (hitbox == null || ConnectedCreatures.TryGetValue(__instance, out _))
                return;
            ConnectedCreatures.Add(__instance, Hooked);
            hitbox.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(inputEvent => OnCreatureHitboxGuiInput(__instance, inputEvent)));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Creature touch-target bridge failed: {exception.Message}");
        }
    }

    // GUI phase of the press that opened a guarded tap: Godot's own hit test routed
    // it to a button/potion/relic/hand card, so the release belongs to that control.
    public static void ClickableGuiInputPrefix(NClickableControl __instance, InputEvent inputEvent)
    {
        try
        {
            if (!PendingTap.Active || PendingTap.Claimed || PendingTap.Frame != Engine.GetProcessFrames())
                return;
            if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } || !MobileInputPolicyPatches.IsTouchPointerEvent(inputEvent))
                return;
            var size = __instance.GetGlobalRect().Size;
            var screen = __instance.GetViewport().GetVisibleRect().Size;
            if (Mathf.Abs(size.X * size.Y) >= screen.X * screen.Y * ClaimMaxScreenShare)
                return;
            PendingTap.Claimed = true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Guarded tap claim check failed: {exception.Message}");
            PendingTap.Claimed = true;
        }
    }

    // Long-press rule for click-to-target (card tapped onto an enemy, potions):
    // a release after a hold never picks a target. Targeting stays open, so the
    // next quick tap on a creature still targets it and a tap elsewhere cancels.
    public static bool TargetManagerInputPrefix(NTargetManager __instance, InputEvent inputEvent)
    {
        try
        {
            if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button
                || !MobileInputPolicyPatches.UseTouchControls || !MobileInputPolicyPatches.IsTouchPointerEvent(button))
                return true;
            var now = Time.GetTicksUsec();
            if (button.Pressed)
            {
                _lastTargetPressUsec = now;
                return true;
            }
            // Not tied to touch_lift_preview: potion targeting needs the guard too.
            if (!__instance.IsInSelection || TargetModeField?.GetValue(__instance) is not TargetMode.ClickMouseToTarget
                || !AndroidSettingsBridge.GetBool("touch_tap_guard", true))
                return true;
            // A cancelled touch (system gesture, palm) is not a tap either.
            if (!button.Canceled && (_lastTargetPressUsec == 0 || now - _lastTargetPressUsec < TapMaxDurationUsec))
                return true;
            PatchHelper.Log(button.Canceled ? "Cancelled touch release ignored while targeting." : "Long-press release ignored while targeting.");
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Targeting long-press guard failed: {exception.Message}");
            return true;
        }
    }

    public static void RepinAfterCancelledCardPlay(NHandCardHolder holder)
    {
        try
        {
            var hand = NPlayerHand.Instance;
            if (hand == null || holder == null || !IsTapPreviewEnabled() || hand.CurrentMode != NPlayerHand.Mode.Play || !IsSameValidHolder(holder, holder))
                return;
            if (!hand.IsAwaitingPlay(holder))
                SetPinned(hand, holder, armDirectDrag: false, log: false);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Repin cancelled card play failed: {exception.Message}");
        }
    }

    public static bool IsPinned(NHandCardHolder holder)
    {
        return IsTapPreviewEnabled() && holder != null && PinnedHolders.TryGetValue(holder, out _);
    }

    public static NCardHolder GetPinnedHolder(NPlayerHand hand)
    {
        return IsTapPreviewEnabled() && hand != null && States.TryGetValue(hand, out var state) ? state.PinnedHolder : null;
    }

    public static void ClearAllPinned()
    {
        ResetForInputModeChange();
    }

    public static void ResetForInputModeChange()
    {
        PendingTap.Reset();
        _lastTargetPressUsec = 0;
        var holders = new List<NCardHolder>();
        foreach (var entry in PinnedHolders)
            holders.Add(entry.Key);
        foreach (var entry in SuppressedHolders)
            holders.Add(entry.Key);
        PinnedHolders.Clear();
        SuppressedHolders.Clear();
        foreach (var entry in States)
        {
            var hand = entry.Key;
            var state = entry.Value;
            if (state.PinnedHolder is NHandCardHolder pinned && GodotObject.IsInstanceValid(hand))
                ClearFocusedHolderIfMatches(hand, pinned);
            state.PinnedHolder = null;
            state.PendingPutDownHolder = null;
            ClearDirectDrag(state);
            if (GodotObject.IsInstanceValid(hand))
                RefreshLayout(hand);
        }
        foreach (var holder in holders)
        {
            if (!GodotObject.IsInstanceValid(holder))
                continue;
            try
            {
                var hitbox = holder.Hitbox;
                var hovered = holder.Visible && hitbox != null && hitbox.IsInsideTree()
                    && hitbox.GetGlobalRect().HasPoint(holder.GetViewport().GetMousePosition());
                Members.IsHovered?.SetValue(holder, hovered);
                InvokeRefreshFocusState(holder);
            }
            catch (Exception exception)
            {
                PatchHelper.Log($"Restore hand-card hover after input mode change failed: {exception.Message}");
            }
        }
    }

    // Hand scrubbing moves the pin without arming the tap drag or a pending
    // re-tap put-down; MobileHandScrubPatches owns the upward-play check then.
    internal static void PinForScrub(NPlayerHand hand, NHandCardHolder holder)
    {
        States.GetOrCreateValue(hand).PendingPutDownHolder = null;
        SetPinned(hand, holder, armDirectDrag: false, log: false);
    }

    internal static void CancelTapGestureForScrub(NPlayerHand hand)
    {
        if (!States.TryGetValue(hand, out var state))
            return;
        state.PendingPutDownHolder = null;
        ClearDirectDrag(state);
    }

    // Vanilla OnHolderUnfocused clears FocusedHolder even when another card is
    // pinned; restore the pinned card's lift without touching drag state.
    internal static void ReassertPinnedFocus(NPlayerHand hand)
    {
        if (!States.TryGetValue(hand, out var state) || state.PinnedHolder is not NHandCardHolder holder || !IsSameValidHolder(holder, holder))
            return;
        SetFocusedHolder(hand, holder);
        RunManager.Instance.HoveredModelTracker.OnLocalCardHovered(holder.CardModel);
        ForcePinnedPreview(holder);
        RefreshLayout(hand);
    }

    internal static bool ShouldHandle(NPlayerHand hand, NCardHolder holder)
    {
        if (!IsTapPreviewEnabled())
            return false;
        if (holder is not NHandCardHolder handHolder || handHolder.CardNode == null)
            return false;
        if (!CombatManager.Instance.IsInProgress || NOverlayStack.Instance.ScreenCount > 0)
            return false;
        if (hand.CurrentMode != NPlayerHand.Mode.Play || hand.PeekButton.IsPeeking || hand.InCardPlay || hand.IsAwaitingPlay(handHolder))
            return false;
        return AreCardActionsAllowed(hand);
    }

    internal static bool IsTapPreviewEnabled()
    {
        return MobileInputPolicyPatches.UseTouchControls && AndroidSettingsBridge.GetBool("touch_lift_preview", true);
    }

    // Off restores the old press-to-play upper-area tap and long-press targeting.
    internal static bool IsTapGuardEnabled()
    {
        return IsTapPreviewEnabled() && AndroidSettingsBridge.GetBool("touch_tap_guard", true);
    }

    private static bool IsTargetAssistEnabled()
    {
        return AndroidSettingsBridge.GetBool("touch_target_assist", true);
    }

    internal static bool IsCombatInputActive()
    {
        try
        {
            // The map, modals and inspect screens are not overlays but still cover
            // the hand; vanilla NPlayerHand._UnhandledInput uses the same context gate.
            return CombatManager.Instance.IsInProgress
                && !CombatManager.Instance.IsOverOrEnding
                && NCombatRoom.Instance != null
                && NOverlayStack.Instance.ScreenCount <= 0
                && ActiveScreenContext.Instance.IsCurrent(NCombatRoom.Instance);
        }
        catch
        {
            return false;
        }
    }

    private static bool AreCardActionsAllowed(NPlayerHand hand)
    {
        try
        {
            var method = Members.AreCardActionsAllowed;
            return method == null || (bool)method.Invoke(hand, null);
        }
        catch
        {
            return !CombatManager.Instance.PlayerActionsDisabled;
        }
    }

    private static void SetPinned(NPlayerHand hand, NHandCardHolder holder, bool armDirectDrag, bool log = true)
    {
        var state = States.GetOrCreateValue(hand);
        if (state.PinnedHolder != null && !ReferenceEquals(state.PinnedHolder, holder))
        {
            SuppressedHolders.Remove(state.PinnedHolder);
            UnpinHolder(state.PinnedHolder);
        }
        state.PinnedHolder = holder;
        SuppressedHolders.Remove(holder);
        PinnedHolders.GetOrCreateValue(holder);
        SetFocusedHolder(hand, holder);
        RunManager.Instance.HoveredModelTracker.OnLocalCardHovered(holder.CardModel);
        ForcePinnedPreview(holder);
        if (armDirectDrag)
            ArmDirectDrag(hand, holder);
        else
            ClearDirectDrag(state);
        SetLastFocusedHolderIndex(hand, holder);
        RefreshLayout(hand);
        if (log)
            PatchHelper.Log($"Pinned hand card preview: {holder.CardModel?.Id}");
    }

    private static void ClearPinned(NPlayerHand hand, bool keepDragCandidate = false, bool suppressHoverUntilExit = false)
    {
        if (!States.TryGetValue(hand, out var state) || state.PinnedHolder == null)
            return;
        var holder = state.PinnedHolder;
        UnpinHolder(holder);
        state.PinnedHolder = null;
        if (holder is NHandCardHolder clearedHandHolder)
            ClearFocusedHolderIfMatches(hand, clearedHandHolder);
        if (ReferenceEquals(state.PendingPutDownHolder, holder))
            state.PendingPutDownHolder = null;
        if (suppressHoverUntilExit)
        {
            if (holder is NHandCardHolder handHolder)
                ClearFocusedHolderIfMatches(hand, handHolder);
            SuppressHoverUntilExit(holder);
        }
        if (!keepDragCandidate)
            ClearDirectDrag(state);
        try
        {
            RunManager.Instance.HoveredModelTracker.OnLocalCardUnhovered();
        }
        catch
        {
        }
        RefreshLayout(hand);
    }

    private static void UnpinHolder(NCardHolder holder)
    {
        try
        {
            PinnedHolders.Remove(holder);
            ForceHolderUnfocused(holder);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Unpin hand card failed: {exception.Message}");
        }
    }

    private static void ForcePinnedPreview(NCardHolder holder)
    {
        try
        {
            var isHoveredField = Members.IsHovered;
            isHoveredField?.SetValue(holder, true);
            var isFocusedField = Members.IsFocused;
            if (isFocusedField?.GetValue(holder) is not true)
            {
                isFocusedField?.SetValue(holder, true);
                InvokeDoCardHoverEffects(holder, true);
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Force pinned preview failed: {exception.Message}");
        }
    }

    private static void ArmDirectDrag(NPlayerHand hand, NHandCardHolder holder)
    {
        var state = States.GetOrCreateValue(hand);
        state.DragHolder = holder;
        state.DragStartPosition = hand.GetViewport()?.GetMousePosition() ?? Vector2.Zero;
    }

    private static void ClearDirectDrag(PreviewState state)
    {
        state.DragHolder = null;
        state.DragStartPosition = Vector2.Zero;
    }

    private static void OnHandHolderMouseReleased(NHandCardHolder holder, InputEvent inputEvent)
    {
        try
        {
            if (!IsTapPreviewEnabled() || !MobileInputPolicyPatches.IsTouchPointerEvent(inputEvent) || !IsDirectDragRelease(inputEvent))
                return;
            var hand = NPlayerHand.Instance;
            if (hand == null || !States.TryGetValue(hand, out var state))
                return;
            CompletePendingPutDown(hand, state, holder, "holder release");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Pinned-card put-down release failed: {exception.Message}");
        }
    }

    // Only the emulated mouse release (first finger) completes it: consuming the
    // raw touch release would hide it from later _Input prefixes (hand scrub's
    // finger bookkeeping, tooltips), and a second finger's release is not a re-tap.
    private static bool HandlePendingPutDownRelease(NPlayerHand hand, InputEvent input)
    {
        if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } || !States.TryGetValue(hand, out var state) || state.PendingPutDownHolder == null)
            return false;
        return CompletePendingPutDown(hand, state, state.PendingPutDownHolder, "global release");
    }

    private static bool CompletePendingPutDown(NPlayerHand hand, PreviewState state, NHandCardHolder holder, string source)
    {
        if (!ReferenceEquals(state.PendingPutDownHolder, holder))
            return false;
        state.PendingPutDownHolder = null;
        if (hand.InCardPlay || !IsSameValidHolder(state.PinnedHolder, holder))
            return false;
        ClearPinned(hand, suppressHoverUntilExit: true);
        PatchHelper.Log($"Put down pinned hand card on re-tap {source}: {holder.CardModel?.Id}");
        return true;
    }

    private static void OnHandHolderHitboxMouseExited(NHandCardHolder holder)
    {
        try
        {
            if (!SuppressedHolders.TryGetValue(holder, out _))
                return;
            SuppressedHolders.Remove(holder);
            InvokeRefreshFocusState(holder);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Clear suppressed card hover failed: {exception.Message}");
        }
    }

    private static void SuppressHoverUntilExit(NCardHolder holder)
    {
        if (holder == null || !GodotObject.IsInstanceValid(holder))
            return;
        SuppressedHolders.GetOrCreateValue(holder);
        try
        {
            holder.ReleaseFocus();
            holder.Hitbox?.ReleaseFocus();
        }
        catch
        {
        }
        ForceHolderUnfocused(holder);
    }

    private static void ForceHolderUnfocused(NCardHolder holder)
    {
        if (holder == null || !GodotObject.IsInstanceValid(holder))
            return;
        var isHoveredField = Members.IsHovered;
        isHoveredField?.SetValue(holder, false);
        var isFocusedField = Members.IsFocused;
        if (isFocusedField?.GetValue(holder) is true)
        {
            isFocusedField.SetValue(holder, false);
            InvokeDoCardHoverEffects(holder, false);
        }
    }

    private static void SetFocusedHolder(NPlayerHand hand, NHandCardHolder holder)
    {
        try
        {
            WriteFocusedHolder(hand, holder);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Set focused hand-card holder failed: {exception.Message}");
        }
    }

    private static void ClearFocusedHolderIfMatches(NPlayerHand hand, NHandCardHolder holder)
    {
        try
        {
            if (!ReferenceEquals(GetFocusedHolder(hand), holder))
                return;
            WriteFocusedHolder(hand, null);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Clear focused hand-card holder failed: {exception.Message}");
        }
    }

    private static object GetFocusedHolder(NPlayerHand hand)
    {
        return Members.FocusedHolder?.GetValue(hand);
    }

    private static void WriteFocusedHolder(NPlayerHand hand, NHandCardHolder holder)
    {
        var setter = Members.FocusedHolderSetter;
        if (setter != null)
        {
            setter.Invoke(hand, new object[] { holder });
            return;
        }
        Members.FocusedHolderBackingField?.SetValue(hand, holder);
    }

    private static bool HandleMobileTapPreviewDirectDrag(NPlayerHand hand, InputEvent input)
    {
        if (!States.TryGetValue(hand, out var state) || state.DragHolder == null)
            return false;
        if (IsDirectDragRelease(input))
        {
            ClearDirectDrag(state);
            return false;
        }
        var holder = state.DragHolder;
        if (!IsTapPreviewEnabled() || hand.CurrentMode != NPlayerHand.Mode.Play || hand.PeekButton.IsPeeking || hand.InCardPlay || !AreCardActionsAllowed(hand) || !IsSameValidHolder(state.PinnedHolder, holder) || hand.IsAwaitingPlay(holder))
        {
            ClearDirectDrag(state);
            return false;
        }
        if (!TryGetDragPosition(input, out var position))
            return false;
        var delta = position - state.DragStartPosition;
        if (delta.LengthSquared() < DirectDragThreshold * DirectDragThreshold || delta.Y > -DirectDragThreshold)
            return false;
        ClearDirectDrag(state);
        StartCardPlay(hand, holder, startedViaShortcut: false);
        return true;
    }

    private static bool HandleMobilePinnedCardPlayZoneTap(NPlayerHand hand, InputEvent input)
    {
        if (!IsTapPreviewEnabled() || hand.CurrentMode != NPlayerHand.Mode.Play || hand.PeekButton.IsPeeking || hand.InCardPlay || !AreCardActionsAllowed(hand))
            return false;
        // Only the emulated mouse press (first finger); a second finger's touch press is not a tap.
        if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press)
            return false;
        var position = press.Position;
        if (!States.TryGetValue(hand, out var state) || state.PinnedHolder is not NHandCardHolder holder || !IsSameValidHolder(holder, holder) || hand.IsAwaitingPlay(holder) || !ShouldPlayPinnedCardOnTap(holder.CardNode?.Model))
            return false;
        if (GetTapZone(hand, holder, position.Y) != TapZone.Play)
            return false;
        TryPlayPinnedCardOnTap(hand, holder);
        return true;
    }

    private enum TapZone
    {
        Play,
        DeadBand,
        BelowPlayZone,
    }

    // One boundary for play-zone taps and cancel taps. The play zone ends at 75%
    // of the screen height, or a dead band above the resting hand when the hand
    // reaches higher (bigger cards + lift). A tap inside that band was probably
    // aimed at a card's top edge: it neither plays the pinned card nor puts it down.
    private static TapZone GetTapZone(NPlayerHand hand, NHandCardHolder pinned, float y)
    {
        var height = hand.GetViewport().GetVisibleRect().Size.Y;
        var handTop = GetRestingHandTopY(hand, pinned);
        var bandTop = handTop - height * HandDeadBandScreenFraction;
        if (y < Mathf.Min(height * PlayZoneMaxScreenFraction, bandTop))
            return TapZone.Play;
        return y >= bandTop && y < handTop ? TapZone.DeadBand : TapZone.BelowPlayZone;
    }

    // Top edge of the resting hand, ignoring the lifted pinned card;
    // float.MaxValue when no other card rests in the hand.
    internal static float GetRestingHandTopY(NPlayerHand hand, NHandCardHolder pinned)
    {
        var top = float.MaxValue;
        foreach (var holder in hand.ActiveHolders)
        {
            if (ReferenceEquals(holder, pinned) || !GodotObject.IsInstanceValid(holder) || holder.Hitbox == null)
                continue;
            top = Mathf.Min(top, holder.Hitbox.GetGlobalRect().Position.Y);
        }
        return top;
    }

    private static bool HandleMobilePinnedCardCancelTap(NPlayerHand hand, InputEvent input)
    {
        if (!IsTapPreviewEnabled() || hand.CurrentMode != NPlayerHand.Mode.Play || hand.PeekButton.IsPeeking || hand.InCardPlay)
            return false;
        if (!IsPress(input) || !TryGetTapPosition(input, out var position))
            return false;
        if (!States.TryGetValue(hand, out var state) || state.PinnedHolder is not NHandCardHolder holder || !IsSameValidHolder(holder, holder) || hand.IsAwaitingPlay(holder))
            return false;
        if (GetTapZone(hand, holder, position.Y) != TapZone.BelowPlayZone || IsPositionOverAnyHandCard(hand, position))
            return false;
        ClearPinned(hand, suppressHoverUntilExit: true);
        return true;
    }

    // Guarded replacement for HandleMobilePinnedCardPlayZoneTap. The press is
    // only recorded (never consumed), so Godot still routes it to whatever is under
    // the finger. The release plays the pinned card (untargeted) or targets the
    // nearest enemy (targeted, assist) only when it was a short, still tap that no
    // button/potion/relic/hand card claimed. Never acts on press or after a hold.
    private static void TrackGuardedTap(NPlayerHand hand, InputEvent input)
    {
        if (input is InputEventScreenTouch { Pressed: true } && PendingTap.Active && PendingTap.Frame != Engine.GetProcessFrames())
        {
            PendingTap.Reset(); // A second finger joined: two-finger gestures are not taps.
            return;
        }
        if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            return;
        if (button.Pressed)
        {
            PendingTap.Reset();
            if (!CanStartGuardedTap(hand, button.Position, out var pressedHolder))
                return;
            PendingTap.Active = true;
            PendingTap.Holder = pressedHolder;
            PendingTap.Position = button.Position;
            PendingTap.Frame = Engine.GetProcessFrames();
            PendingTap.StartUsec = Time.GetTicksUsec();
            return;
        }
        if (!PendingTap.Active)
            return;
        var holder = PendingTap.Holder;
        var position = PendingTap.Position;
        var claimed = PendingTap.Claimed;
        var heldUsec = Time.GetTicksUsec() - PendingTap.StartUsec;
        PendingTap.Reset();
        if (button.Canceled || claimed || heldUsec >= TapMaxDurationUsec || button.Position.DistanceSquaredTo(position) > TapSlop * TapSlop)
            return;
        if (!IsPinnedCardActionable(hand, holder))
            return;
        var model = holder.CardNode?.Model;
        if (ShouldPlayPinnedCardOnTap(model))
        {
            PatchHelper.Log($"Play-zone tap played pinned card on release: {model?.Id}");
            TryPlayPinnedCardOnTap(hand, holder);
            return;
        }
        TryTargetAssist(hand, holder, position);
    }

    private static bool CanStartGuardedTap(NPlayerHand hand, Vector2 position, out NHandCardHolder holder)
    {
        holder = null;
        if (!States.TryGetValue(hand, out var state) || state.PinnedHolder is not NHandCardHolder pinned || !IsPinnedCardActionable(hand, pinned))
            return false;
        if (GetTapZone(hand, pinned, position.Y) != TapZone.Play || IsPositionOverAnyHandCard(hand, position))
            return false;
        var model = pinned.CardNode?.Model;
        if (!ShouldPlayPinnedCardOnTap(model) && !(IsTargetAssistEnabled() && IsSingleCreatureTarget(model)))
            return false;
        holder = pinned;
        return true;
    }

    private static bool IsPinnedCardActionable(NPlayerHand hand, NHandCardHolder holder)
    {
        if (!IsTapPreviewEnabled() || hand.CurrentMode != NPlayerHand.Mode.Play || hand.PeekButton.IsPeeking || hand.InCardPlay || !AreCardActionsAllowed(hand))
            return false;
        return States.TryGetValue(hand, out var state) && IsSameValidHolder(state.PinnedHolder, holder) && !hand.IsAwaitingPlay(holder) && holder.CardNode != null;
    }

    // Forgiving enemy targeting: a quick tap that missed every creature hitbox picks
    // the nearest valid target within the assist radius, through the same
    // StartCardPlay + NTargetManager path as a direct tap on the creature.
    private static void TryTargetAssist(NPlayerHand hand, NHandCardHolder holder, Vector2 position)
    {
        var model = holder.CardNode?.Model;
        var room = NCombatRoom.Instance;
        if (model == null || room == null || !IsTargetAssistEnabled() || !IsSingleCreatureTarget(model))
            return;
        var bestDistance = hand.GetViewport().GetVisibleRect().Size.Y * TargetAssistScreenFraction;
        NCreature best = null;
        foreach (var creature in room.CreatureNodes)
        {
            if (creature == null || !GodotObject.IsInstanceValid(creature) || !creature.IsVisibleInTree())
                continue;
            var hitbox = creature.Hitbox;
            if (hitbox == null || !hitbox.IsVisibleInTree() || hitbox.MouseFilter == Control.MouseFilterEnum.Ignore)
                continue;
            var distance = DistanceToRect(ScreenRect(hitbox), position);
            if (distance <= 0f)
                return; // Inside a creature: Godot's own hit test already owned this tap.
            if (distance < bestDistance && creature.Entity is { IsHittable: true } entity && CanTargetPinnedCard(model, entity))
            {
                best = creature;
                bestDistance = distance;
            }
        }
        if (best == null)
            return;
        PatchHelper.Log($"Enemy tap assist: {model.Id} -> {best.Name} ({bestDistance:0}px from tap).");
        StartCardPlay(hand, holder, startedViaShortcut: true);
        var targetManager = NTargetManager.Instance;
        if (targetManager == null || !targetManager.IsInSelection)
            return; // CanPlay failed; NMouseCardPlay already cancelled and explained why.
        targetManager.OnNodeHovered(best);
        // This release already went past NTargetManager._Input, so finish here.
        FinishTargetingMethod?.Invoke(targetManager, new object[] { false });
    }

    internal static bool IsSingleCreatureTarget(CardModel card)
    {
        return card != null && card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly;
    }

    internal static Rect2 ScreenRect(Control control)
    {
        var transform = control.GetGlobalTransformWithCanvas();
        var scale = transform.Scale;
        return new Rect2(transform.Origin, new Vector2(control.Size.X * Mathf.Abs(scale.X), control.Size.Y * Mathf.Abs(scale.Y)));
    }

    internal static float DistanceToRect(Rect2 rect, Vector2 point)
    {
        var dx = Mathf.Max(Mathf.Max(rect.Position.X - point.X, 0f), point.X - rect.End.X);
        var dy = Mathf.Max(Mathf.Max(rect.Position.Y - point.Y, 0f), point.Y - rect.End.Y);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsPositionOverAnyHandCard(NPlayerHand hand, Vector2 position)
    {
        foreach (var activeHolder in hand.ActiveHolders)
        {
            if (GodotObject.IsInstanceValid(activeHolder) && activeHolder.Visible && activeHolder.Hitbox.IsInsideTree() && activeHolder.Hitbox.GetGlobalRect().HasPoint(position))
                return true;
        }
        return false;
    }

    private static void OnCreatureHitboxGuiInput(NCreature creature, InputEvent inputEvent)
    {
        try
        {
            if (!MobileInputPolicyPatches.UseTouchControls || !MobileInputPolicyPatches.IsTouchPointerEvent(inputEvent) || !IsPress(inputEvent))
                return;
            var hand = NPlayerHand.Instance;
            if (hand != null && TryStartPinnedCardPlayAtCreature(hand, creature))
                NTargetManager.Instance?.OnNodeHovered(creature);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Creature target tap failed: {exception.Message}");
        }
    }

    private static bool TryStartPinnedCardPlayAtCreature(NPlayerHand hand, NCreature creature)
    {
        if (!IsTapPreviewEnabled() || hand.CurrentMode != NPlayerHand.Mode.Play || hand.PeekButton.IsPeeking || hand.InCardPlay || !AreCardActionsAllowed(hand))
            return false;
        if (!States.TryGetValue(hand, out var state) || state.PinnedHolder is not NHandCardHolder holder || !IsSameValidHolder(holder, holder) || hand.IsAwaitingPlay(holder) || holder.CardNode == null)
            return false;
        if (!CanTargetPinnedCard(holder.CardNode.Model, creature.Entity))
            return false;
        StartCardPlay(hand, holder, startedViaShortcut: true);
        return true;
    }

    internal static void StartCardPlay(NPlayerHand hand, NHandCardHolder holder, bool startedViaShortcut)
    {
        if (StartCardPlayMethod == null)
            return;
        ClearPinned(hand, keepDragCandidate: true);
        StartCardPlayMethod.Invoke(hand, new object[] { holder, startedViaShortcut });
    }

    private static bool ShouldPlayPinnedCardOnTap(CardModel card)
    {
        if (card == null)
            return false;
        return card.TargetType switch
        {
            TargetType.AnyEnemy or TargetType.AnyPlayer or TargetType.AnyAlly or TargetType.TargetedNoCreature => false,
            _ => true,
        };
    }

    private static void TryPlayPinnedCardOnTap(NPlayerHand hand, NHandCardHolder holder)
    {
        var model = holder.CardNode?.Model;
        if (model == null || !IsSameValidHolder(holder, holder))
            return;
        ClearPinned(hand);
        RunManager.Instance.HoveredModelTracker.OnLocalCardSelected(model);
        try
        {
            if (!model.TryManualPlay(null) && IsSameValidHolder(holder, holder))
            {
                SuppressHoverUntilExit(holder);
                // Manual play bypasses NMouseCardPlay's rejection feedback. Reuse
                // the payload's reason/localization rules without restarting a drag.
                if (!model.CanPlay(out var reason, out var preventer))
                {
                    var dialogue = GetPlayerDialogueLine(reason, preventer);
                    if (dialogue != null)
                        NCombatRoom.Instance.CombatVfxContainer.AddChildSafely(NThoughtBubbleVfx.Create(dialogue.GetFormattedText(), model.Owner.Creature, 1.0));
                }
            }
        }
        finally
        {
            RunManager.Instance.HoveredModelTracker.OnLocalCardDeselected();
        }
    }

    internal static bool CanTargetPinnedCard(CardModel card, Creature target)
    {
        if (card == null || target == null || !target.IsAlive)
            return false;
        return card.TargetType switch
        {
            TargetType.AnyEnemy => target.Side != card.Owner.Creature.Side,
            TargetType.AnyPlayer => target.IsPlayer,
            TargetType.AnyAlly => target.IsPlayer && !LocalContext.IsMe(target.Player),
            _ => false,
        };
    }

    private static bool TryGetDragPosition(InputEvent input, out Vector2 position)
    {
        if (input is InputEventMouseMotion mouseMotion)
        {
            position = mouseMotion.Position;
            return true;
        }
        if (input is InputEventScreenDrag screenDrag)
        {
            position = screenDrag.Position;
            return true;
        }
        position = Vector2.Zero;
        return false;
    }

    private static bool TryGetTapPosition(InputEvent input, out Vector2 position)
    {
        if (input is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
        {
            position = mouseButton.Position;
            return true;
        }
        if (input is InputEventScreenTouch screenTouch)
        {
            position = screenTouch.Position;
            return true;
        }
        position = Vector2.Zero;
        return false;
    }

    private static bool IsPress(InputEvent input)
    {
        return input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }
            || input is InputEventScreenTouch { Pressed: true };
    }

    private static bool IsDirectDragRelease(InputEvent input)
    {
        return input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }
            || input is InputEventScreenTouch { Pressed: false };
    }

    private static void SetLastFocusedHolderIndex(NPlayerHand hand, NHandCardHolder holder)
    {
        try
        {
            Members.LastFocusedHolderIndex?.SetValue(hand, holder.GetIndex());
        }
        catch
        {
        }
    }

    private static void RefreshLayout(NPlayerHand hand)
    {
        try
        {
            hand.ForceRefreshCardIndices();
        }
        catch
        {
        }
    }

    private static void InvokeRefreshFocusState(NCardHolder holder)
    {
        Members.HolderMethod(holder.GetType(), "RefreshFocusState")?.Invoke(holder, null);
    }

    private static void InvokeDoCardHoverEffects(NCardHolder holder, bool isHovered)
    {
        Members.HolderMethod(holder.GetType(), "DoCardHoverEffects")?.Invoke(holder, new object[] { isHovered });
    }

    private static bool IsSameValidHolder(NCardHolder current, NHandCardHolder candidate)
    {
        return ReferenceEquals(current, candidate) && GodotObject.IsInstanceValid(candidate) && candidate.IsInsideTree();
    }

    private static RetapAction GetRetapAction()
    {
        return AndroidSettingsBridge.GetString("touch_lift_retap_action", "put_down").Trim().ToLowerInvariant().Replace("-", "_") switch
        {
            "play" => RetapAction.Play,
            "none" => RetapAction.None,
            _ => RetapAction.PutDown,
        };
    }

    private enum RetapAction
    {
        PutDown,
        Play,
        None,
    }

    // Reflection members the hand/holder paths used to look up on every call
    // (AreCardActionsAllowed runs on each drag event while a card is armed).
    // A nested class keeps initialization lazy and confined: a lookup that throws
    // surfaces at the call site (inside the same try/catch as before) instead of
    // breaking MobileTapPreviewPatches' own type initializer.
    private static class Members
    {
        private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        internal static readonly FieldInfo IsFocused = typeof(NCardHolder).GetField("_isFocused", Instance);
        internal static readonly FieldInfo IsHovered = typeof(NCardHolder).GetField("_isHovered", Instance);
        internal static readonly MethodInfo AreCardActionsAllowed = typeof(NPlayerHand).GetMethod("AreCardActionsAllowed", Instance);
        internal static readonly PropertyInfo FocusedHolder = typeof(NPlayerHand).GetProperty("FocusedHolder", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        internal static readonly MethodInfo FocusedHolderSetter = FocusedHolder?.GetSetMethod(true);
        internal static readonly FieldInfo FocusedHolderBackingField = typeof(NPlayerHand).GetField("<FocusedHolder>k__BackingField", Instance);
        internal static readonly FieldInfo LastFocusedHolderIndex = typeof(NPlayerHand).GetField("_lastFocusedHolderIdx", Instance);
        // Holder methods are resolved on the runtime holder type, as before.
        private static readonly ConcurrentDictionary<(Type Type, string Name), MethodInfo> HolderMethods = new();

        internal static MethodInfo HolderMethod(Type type, string name) =>
            HolderMethods.GetOrAdd((type, name), static key => key.Type.GetMethod(key.Name, Instance));
    }

    private sealed class PreviewState
    {
        public NCardHolder PinnedHolder;
        public NHandCardHolder DragHolder;
        public NHandCardHolder PendingPutDownHolder;
        public Vector2 DragStartPosition;
    }

    private sealed class GuardedTap
    {
        public bool Active;
        public bool Claimed;
        public NHandCardHolder Holder;
        public Vector2 Position;
        public ulong Frame;
        public ulong StartUsec;

        public void Reset()
        {
            Active = Claimed = false;
            Holder = null;
            Position = Vector2.Zero;
            Frame = StartUsec = 0;
        }
    }

    private sealed class PinnedState
    {
    }

    private sealed class SuppressedHoverState
    {
    }
}
