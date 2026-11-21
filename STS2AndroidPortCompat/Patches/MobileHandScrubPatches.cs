using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// STS1-mobile hand browsing on top of the tap-to-pin model: a finger that
/// starts on the hand and slides sideways moves the pin to the card under it
/// (picked like Godot hover: topmost hitbox in tree order). Lifting keeps that
/// card pinned. Rising out of the hand starts play of the pinned card through
/// the same StartCardPlay path the tap preview's direct drag uses. Until the
/// first sideways move, tap/re-tap/direct-drag behave exactly as before.
/// </summary>
public static class MobileHandScrubPatches
{
    private const float ScrubStartDistance = 20f;
    private const float ScrubPlayRise = 60f;
    private const int NoTouch = -1;

    private static int _touchIndex = NoTouch;
    private static NPlayerHand _hand;
    private static Vector2 _startPosition;
    private static float _lowestY;
    private static bool _scrubbing;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NGame), "_Input", prefix: PatchHelper.Method(typeof(MobileHandScrubPatches), nameof(GameInputPrefix)));
        PatchHelper.Patch(harmony, typeof(NPlayerHand), "OnHolderUnfocused", postfix: PatchHelper.Method(typeof(MobileHandScrubPatches), nameof(OnHolderUnfocusedPostfix)));
    }

    public static bool GameInputPrefix(NGame __instance, InputEvent inputEvent)
    {
        try
        {
            switch (inputEvent)
            {
                case InputEventScreenTouch touch:
                    HandleTouch(touch);
                    break;
                case InputEventScreenDrag drag when drag.Index == _touchIndex:
                    if (HandleDrag(drag.Position))
                    {
                        __instance.GetViewport()?.SetInputAsHandled();
                        return false;
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile hand scrub input failed: {exception.Message}");
            Reset();
        }
        return true;
    }

    public static void OnHolderUnfocusedPostfix(NPlayerHand __instance, NHandCardHolder holder)
    {
        try
        {
            if (!IsEnabled() || __instance.FocusedHolder != null || __instance.CurrentMode != NPlayerHand.Mode.Play || __instance.InCardPlay)
                return;
            var pinned = MobileTapPreviewPatches.GetPinnedHolder(__instance);
            if (pinned == null || (ReferenceEquals(pinned, holder) && !_scrubbing))
                return;
            MobileTapPreviewPatches.ReassertPinnedFocus(__instance);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile hand scrub focus restore failed: {exception.Message}");
        }
    }

    private static void HandleTouch(InputEventScreenTouch touch)
    {
        if (touch.Pressed && !touch.Canceled)
        {
            if (_touchIndex != NoTouch)
            {
                // A second finger (two-finger inspect) ends browsing; keep the pin.
                Reset();
                return;
            }
            var hand = NPlayerHand.Instance;
            if (!IsEnabled() || !CanBrowse(hand) || FindHolderAt(hand, touch.Position) == null)
                return;
            _touchIndex = touch.Index;
            _hand = hand;
            _startPosition = touch.Position;
            _lowestY = touch.Position.Y;
            _scrubbing = false;
            return;
        }
        if (touch.Index != _touchIndex)
            return;
        if (_scrubbing && GodotObject.IsInstanceValid(_hand))
            PatchHelper.Log($"Hand scrub ended on: {MobileTapPreviewPatches.GetPinnedHolder(_hand)?.CardModel?.Id}");
        Reset();
    }

    // Returns true when this drag started a card play and should be consumed.
    private static bool HandleDrag(Vector2 position)
    {
        var hand = _hand;
        if (!IsEnabled() || !GodotObject.IsInstanceValid(hand) || !ReferenceEquals(NPlayerHand.Instance, hand) || !CanBrowse(hand))
        {
            Reset();
            return false;
        }
        _lowestY = Mathf.Max(_lowestY, position.Y);

        if (!_scrubbing)
        {
            var delta = position - _startPosition;
            if (Mathf.Abs(delta.X) < ScrubStartDistance || Mathf.Abs(delta.X) < Mathf.Abs(delta.Y))
                return false;
            _scrubbing = true;
            MobileTapPreviewPatches.CancelTapGestureForScrub(hand);
            PatchHelper.Log("Hand scrub started.");
        }

        var pinned = MobileTapPreviewPatches.GetPinnedHolder(hand) as NHandCardHolder;
        if (pinned != null && _lowestY - position.Y >= ScrubPlayRise && position.Y < GetHandTopY(hand, pinned)
            && MobileTapPreviewPatches.ShouldHandle(hand, pinned))
        {
            PatchHelper.Log($"Hand scrub drag-to-play: {pinned.CardModel?.Id}");
            Reset();
            MobileTapPreviewPatches.StartCardPlay(hand, pinned, startedViaShortcut: false);
            return true;
        }

        var candidate = FindHolderAt(hand, position);
        if (candidate != null && !ReferenceEquals(candidate, pinned) && MobileTapPreviewPatches.ShouldHandle(hand, candidate))
            MobileTapPreviewPatches.PinForScrub(hand, candidate);
        return false;
    }

    private static bool IsEnabled()
    {
        return MobileTapPreviewPatches.IsTapPreviewEnabled() && AndroidSettingsBridge.GetBool("mobile_hand_scrub", true);
    }

    private static bool CanBrowse(NPlayerHand hand)
    {
        return hand != null && MobileTapPreviewPatches.IsCombatInputActive() && hand.CurrentMode == NPlayerHand.Mode.Play
            && !hand.PeekButton.IsPeeking && !hand.InCardPlay;
    }

    // Same pick as Godot GUI hover: last (topmost) active holder whose hitbox contains the point.
    private static NHandCardHolder FindHolderAt(NPlayerHand hand, Vector2 position)
    {
        var holders = hand.ActiveHolders;
        for (var i = holders.Count - 1; i >= 0; i--)
        {
            var holder = holders[i];
            if (!GodotObject.IsInstanceValid(holder))
                continue;
            var hitbox = holder.Hitbox;
            if (hitbox != null && hitbox.IsEnabled && hitbox.IsVisibleInTree() && hitbox.GetGlobalRect().HasPoint(position))
                return holder;
        }
        return null;
    }

    // Top edge of the resting (unpinned) hand; the lifted pinned card is ignored.
    private static float GetHandTopY(NPlayerHand hand, NHandCardHolder pinned)
    {
        var top = float.MaxValue;
        foreach (var holder in hand.ActiveHolders)
        {
            if (ReferenceEquals(holder, pinned) || !GodotObject.IsInstanceValid(holder) || holder.Hitbox == null)
                continue;
            top = Mathf.Min(top, holder.Hitbox.GetGlobalRect().Position.Y);
        }
        return top < float.MaxValue ? top : hand.GetViewport().GetVisibleRect().Size.Y * 0.75f;
    }

    private static void Reset()
    {
        _touchIndex = NoTouch;
        _hand = null;
        _scrubbing = false;
    }
}
