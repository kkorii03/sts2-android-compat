using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class MobileHandLayoutPatches
{
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    // Reflection results per hand type, resolved on first use (not per call/holder).
    private static Type _activeHoldersType;
    private static PropertyInfo _activeHoldersProperty;
    private static Type _focusedHolderType;
    private static PropertyInfo _focusedHolderProperty;
    // OS.HasFeature("mobile") is fixed for the process; 0 = not sampled, 1 = true, 2 = false.
    private static int _mobileFeature;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NPlayerHand), "RefreshLayout",
            prefix: PatchHelper.Method(typeof(MobileHandLayoutPatches), nameof(RefreshLayoutPrefix)),
            postfix: PatchHelper.Method(typeof(MobileHandLayoutPatches), nameof(RefreshLayoutPostfix)));
    }

    // __state is each call's own capture (each card's Position/Scale just before
    // vanilla RefreshLayout, by instance id), so a nested RefreshLayout (hover
    // signal during layout) cannot clear the outer call's snapshot.
    public static void RefreshLayoutPostfix(object __instance, Dictionary<ulong, (Vector2 Position, Vector2 Scale)> __state)
    {
        try
        {
            var yOffset = GetAdditionalHandCardYOffset();
            var cardFactor = GetHandCardScaleFactor();
            if (Mathf.IsZeroApprox(yOffset) && cardFactor <= 1f)
                return;

            var activeHolders = GetActiveHoldersProperty(__instance.GetType())?.GetValue(__instance) as System.Collections.IEnumerable;
            if (activeHolders == null)
                return;

            var holders = new List<NHandCardHolder>();
            foreach (var item in activeHolders)
            {
                if (item is NHandCardHolder holder && GodotObject.IsInstanceValid(holder) && holder.Visible)
                    holders.Add(holder);
            }
            if (holders.Count == 0)
                return;

            // Bigger cards: grow every resting card and spread the row by the same amount
            // (clamped to the screen) so they overlap no more than vanilla. The focused
            // (lifted, normally the pinned) card keeps vanilla scale/height and only
            // follows the spread sideways.
            // Bigger resting cards grow downward (top edge stays at vanilla), like STS1 mobile.
            var baseScale = HandPosHelper.GetScale(holders.Count);
            var spread = cardFactor > 1f ? GetHandSpread(holders, baseScale, cardFactor) : 1f;
            foreach (var holder in holders)
            {
                var focused = IsFocusedHolder(__instance, holder);
                var target = holder.TargetPosition;
                var restingY = target.Y + yOffset + MobileCombatSizing.GrowDownOffset(holder.Hitbox?.Size.Y ?? 0f, baseScale.Y, cardFactor);
                var newTarget = new Vector2(target.X * spread, focused ? target.Y : restingY);
                (Vector2 Position, Vector2 Scale) before = default;
                var hasBefore = __state != null && __state.TryGetValue(holder.GetInstanceId(), out before);
                if (newTarget != target)
                {
                    // Vanilla's SetTarget* already took one animation step toward its own
                    // target; undo it so the card moves only toward ours (no twitch).
                    if (hasBefore)
                        holder.Position = focused ? new Vector2(before.Position.X, holder.Position.Y) : before.Position;
                    holder.SetTargetPosition(newTarget);
                }
                if (!focused && cardFactor > 1f)
                {
                    if (hasBefore)
                        holder.Scale = before.Scale;
                    holder.SetTargetScale(baseScale * cardFactor);
                }
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile hand layout offset failed: {exception.Message}");
        }
    }

    public static void RefreshLayoutPrefix(object __instance, out Dictionary<ulong, (Vector2 Position, Vector2 Scale)> __state)
    {
        __state = null;
        try
        {
            if (Mathf.IsZeroApprox(GetAdditionalHandCardYOffset()) && GetHandCardScaleFactor() <= 1f)
                return;
            if (GetActiveHoldersProperty(__instance.GetType())?.GetValue(__instance) is not System.Collections.IEnumerable activeHolders)
                return;
            var capture = new Dictionary<ulong, (Vector2 Position, Vector2 Scale)>();
            foreach (var item in activeHolders)
            {
                if (item is NHandCardHolder holder && GodotObject.IsInstanceValid(holder))
                    capture[holder.GetInstanceId()] = (holder.Position, holder.Scale);
            }
            __state = capture;
        }
        catch (Exception exception)
        {
            __state = null;
            PatchHelper.Log($"Mobile hand layout capture failed: {exception.Message}");
        }
    }

    // "Focused" means exactly the card vanilla RefreshLayout lifted (FocusedHolder).
    // A pinned card is normally that card (the tap preview keeps FocusedHolder on
    // it); if vanilla laid it out as a resting card anyway, treating it as focused
    // here would leave it at vanilla resting height and scale, smaller than and
    // sunk into the enlarged row, so it gets the resting adjustments like its neighbours.
    private static bool IsFocusedHolder(object playerHand, NHandCardHolder holder)
    {
        var focused = GetFocusedHolderProperty(playerHand.GetType())?.GetValue(playerHand);
        return ReferenceEquals(focused, holder);
    }

    private static PropertyInfo GetActiveHoldersProperty(Type type)
    {
        if (!ReferenceEquals(type, _activeHoldersType))
        {
            // Assigned only after a successful lookup, so a throwing lookup keeps
            // failing (and being logged) exactly as the uncached call did.
            _activeHoldersProperty = type.GetProperty("ActiveHolders", MemberFlags);
            _activeHoldersType = type;
        }
        return _activeHoldersProperty;
    }

    private static PropertyInfo GetFocusedHolderProperty(Type type)
    {
        if (!ReferenceEquals(type, _focusedHolderType))
        {
            _focusedHolderProperty = type.GetProperty("FocusedHolder", MemberFlags);
            _focusedHolderType = type;
        }
        return _focusedHolderProperty;
    }

    private static bool HasMobileFeature()
    {
        var cached = _mobileFeature;
        if (cached != 0)
            return cached == 1;
        var mobile = OS.HasFeature("mobile");
        _mobileFeature = mobile ? 1 : 2;
        return mobile;
    }

    private static float GetHandCardScaleFactor()
    {
        if (!HasMobileFeature())
            return 1f;
        return MobileCombatSizing.Factor(
            AndroidSettingsBridge.GetInt(MobileCombatSizing.HandCardScaleKey, MobileCombatSizing.HandCardScaleDefault),
            MobileCombatSizing.HandCardScaleMin, MobileCombatSizing.HandCardScaleMax);
    }

    private static float GetHandSpread(List<NHandCardHolder> holders, Vector2 baseScale, float cardFactor)
    {
        var maxAbsX = 0f;
        var halfCardWidth = 0f;
        foreach (var holder in holders)
        {
            maxAbsX = Mathf.Max(maxAbsX, Mathf.Abs(holder.TargetPosition.X));
            if (holder.Hitbox != null)
                halfCardWidth = Mathf.Max(halfCardWidth, holder.Hitbox.Size.X * 0.5f);
        }

        // Room from the hand's center to the nearer screen side, in the container's units.
        var halfLimit = 0f;
        if (holders[0].GetParent() is Control container && container.IsInsideTree())
        {
            var transform = container.GetGlobalTransform();
            var scaleX = transform.X.X;
            var width = container.GetViewportRect().Size.X;
            if (scaleX > 0.001f && width > 0f)
                halfLimit = Mathf.Min(transform.Origin.X, width - transform.Origin.X) / scaleX;
        }
        return MobileCombatSizing.HandSpread(cardFactor, maxAbsX, halfCardWidth * baseScale.X * cardFactor, halfLimit);
    }

    private static float GetAdditionalHandCardYOffset()
    {
        if (!HasMobileFeature())
            return 0f;
        if (!AndroidSettingsBridge.GetBool("show_more_hand_card_text", true))
            return 0f;
        var percent = Mathf.Clamp(AndroidSettingsBridge.GetInt("show_more_hand_card_text_lift_height_percent", 50), 25, 100);
        return -80f * percent / 100f;
    }
}
