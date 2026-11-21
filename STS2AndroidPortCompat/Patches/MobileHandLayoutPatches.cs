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
    // Each resting card's Position/Scale just before vanilla RefreshLayout, by instance id.
    private static readonly Dictionary<ulong, (Vector2 Position, Vector2 Scale)> PreLayout = new();

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NPlayerHand), "RefreshLayout",
            prefix: PatchHelper.Method(typeof(MobileHandLayoutPatches), nameof(RefreshLayoutPrefix)),
            postfix: PatchHelper.Method(typeof(MobileHandLayoutPatches), nameof(RefreshLayoutPostfix)));
    }

    public static void RefreshLayoutPostfix(object __instance)
    {
        try
        {
            var yOffset = GetAdditionalHandCardYOffset();
            var cardFactor = GetHandCardScaleFactor();
            if (Mathf.IsZeroApprox(yOffset) && cardFactor <= 1f)
                return;

            var activeHolders = __instance.GetType().GetProperty("ActiveHolders", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) as System.Collections.IEnumerable;
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
            // (clamped to the screen) so they overlap no more than vanilla. The focused or
            // pinned card keeps vanilla scale/height and only follows the spread sideways.
            // Bigger resting cards grow downward (top edge stays at vanilla), like STS1 mobile.
            var baseScale = HandPosHelper.GetScale(holders.Count);
            var spread = cardFactor > 1f ? GetHandSpread(holders, baseScale, cardFactor) : 1f;
            foreach (var holder in holders)
            {
                var focused = IsFocusedHolder(__instance, holder);
                var target = holder.TargetPosition;
                var restingY = target.Y + yOffset + MobileCombatSizing.GrowDownOffset(holder.Hitbox?.Size.Y ?? 0f, baseScale.Y, cardFactor);
                var newTarget = new Vector2(target.X * spread, focused ? target.Y : restingY);
                var hasBefore = PreLayout.TryGetValue(holder.GetInstanceId(), out var before);
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
        finally
        {
            PreLayout.Clear();
        }
    }

    public static void RefreshLayoutPrefix(object __instance)
    {
        PreLayout.Clear();
        try
        {
            if (Mathf.IsZeroApprox(GetAdditionalHandCardYOffset()) && GetHandCardScaleFactor() <= 1f)
                return;
            if (__instance.GetType().GetProperty("ActiveHolders", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) is not System.Collections.IEnumerable activeHolders)
                return;
            foreach (var item in activeHolders)
            {
                if (item is NHandCardHolder holder && GodotObject.IsInstanceValid(holder))
                    PreLayout[holder.GetInstanceId()] = (holder.Position, holder.Scale);
            }
        }
        catch (Exception exception)
        {
            PreLayout.Clear();
            PatchHelper.Log($"Mobile hand layout capture failed: {exception.Message}");
        }
    }

    private static bool IsFocusedHolder(object playerHand, NHandCardHolder holder)
    {
        if (MobileTapPreviewPatches.IsPinned(holder))
            return true;
        var focused = playerHand.GetType().GetProperty("FocusedHolder", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(playerHand);
        return ReferenceEquals(focused, holder);
    }

    private static float GetHandCardScaleFactor()
    {
        if (!OS.HasFeature("mobile"))
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
        if (!OS.HasFeature("mobile"))
            return 0f;
        if (!AndroidSettingsBridge.GetBool("show_more_hand_card_text", true))
            return 0f;
        var percent = Mathf.Clamp(AndroidSettingsBridge.GetInt("show_more_hand_card_text_lift_height_percent", 50), 25, 100);
        return -80f * percent / 100f;
    }
}
