using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// STS1-mobile style placement for the injected selection confirm buttons: after a
/// tap selects an item, the confirm button appears right next to that item (below it
/// first, then beside it, then above it) so the thumb that just tapped can confirm
/// without crossing the screen.  A spot is accepted only when it stays inside the
/// safe area (top bar band excluded), does not touch the selected item, and keeps a
/// clearance from every other tappable control on the screen and from visible hover
/// tips.  If no such spot exists the stock bottom-right position is used, exactly as
/// before.  While near-thumb placement is active the button is hidden (not slid
/// across the screen) when disabled, so it never passes over other items.
/// </summary>
internal static class MobileConfirmPlacement
{
    internal const string SettingKey = "mobile_confirm_near_thumb";

    private const float MinWidth = 120f;
    private const float MinHeight = 56f;
    private const float Gap = 16f;
    private const float Clearance = 20f;
    private const float ObstacleGrowthRatio = 0.12f;
    private const float ItemGrowthRatio = 0.03f;
    private const float EdgeMargin = 24f;
    private const float TopReserveRatio = 0.15f;
    private const float MaxObstacleAreaRatio = 0.4f;

    private static readonly BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo ShowPosField = typeof(NConfirmButton).GetField("_showPos", InstanceFlags);
    private static readonly FieldInfo HidePosField = typeof(NConfirmButton).GetField("_hidePos", InstanceFlags);
    private static readonly FieldInfo MoveTweenField = typeof(NConfirmButton).GetField("_moveTween", InstanceFlags);
    private static readonly MethodInfo WindowChangeMethod = typeof(NConfirmButton).GetMethod("OnWindowChange", InstanceFlags, null, Type.EmptyTypes, null);
    private static readonly FieldInfo CostLabelField = typeof(NMerchantSlot).GetField("_costLabel", InstanceFlags);
    private static readonly ConditionalWeakTable<NConfirmButton, PlacementState> States = new();

    internal static bool IsEnabled()
    {
        return AndroidSettingsBridge.GetBool(SettingKey, true);
    }

    /// <summary>Call after <c>button.Enable()</c>; moves the button next to <paramref name="anchor"/>.</summary>
    internal static void ShowNear(NConfirmButton button, Control anchor)
    {
        if (!IsValid(button))
            return;
        try
        {
            if (!IsEnabled() || !IsValid(anchor) || !button.IsInsideTree() || ShowPosField == null || HidePosField == null)
            {
                RestoreStock(button);
                return;
            }

            var state = States.GetOrCreateValue(button);
            if (state.Shown && button.Visible && ReferenceEquals(state.Anchor, anchor))
                return;

            state.Modified = true;
            state.Shown = true;
            state.Anchor = anchor;
            EnsureMinimumSize(button);
            (MoveTweenField?.GetValue(button) as Tween)?.Kill();

            if (TryFindSpot(button, anchor, out var localPosition))
            {
                ShowPosField.SetValue(button, localPosition);
                HidePosField.SetValue(button, localPosition);
                button.Position = localPosition;
            }
            else
            {
                // No clear spot next to the item: use the stock bottom-right slot, placed
                // instantly instead of sliding in.
                WindowChangeMethod?.Invoke(button, null);
            }

            var wasVisible = button.Visible;
            button.Visible = true;
            if (!wasVisible)
                FadeIn(button, state);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Near-thumb confirm placement failed; using stock position: {exception.Message}");
            RestoreStock(button);
        }
    }

    /// <summary>Call after <c>button.Disable()</c>.</summary>
    internal static void Hide(NConfirmButton button)
    {
        if (!IsValid(button))
            return;
        if (!IsEnabled())
        {
            RestoreStock(button);
            return;
        }
        var state = States.GetOrCreateValue(button);
        state.Modified = true;
        state.Shown = false;
        state.Anchor = null;
        state.Fade?.Kill();
        button.Visible = false;
    }

    private static void RestoreStock(NConfirmButton button)
    {
        if (!States.TryGetValue(button, out var state) || !state.Modified)
            return;
        state.Modified = false;
        state.Shown = false;
        state.Anchor = null;
        state.Fade?.Kill();
        var modulate = button.Modulate;
        modulate.A = 1f;
        button.Modulate = modulate;
        button.Visible = true;
        if (button.IsInsideTree())
            WindowChangeMethod?.Invoke(button, null);
    }

    private static void FadeIn(NConfirmButton button, PlacementState state)
    {
        state.Fade?.Kill();
        var modulate = button.Modulate;
        modulate.A = 0f;
        button.Modulate = modulate;
        state.Fade = button.CreateTween();
        state.Fade.TweenProperty(button, "modulate:a", 1f, 0.12);
    }

    private static void EnsureMinimumSize(NConfirmButton button)
    {
        var parentScale = GetParentScale(button);
        var size = button.Size;
        var target = new Vector2(
            Math.Max(size.X, MinWidth / Math.Max(0.01f, Math.Abs(parentScale.X))),
            Math.Max(size.Y, MinHeight / Math.Max(0.01f, Math.Abs(parentScale.Y))));
        if (target.IsEqualApprox(size))
            return;
        var pivotWasCentered = button.PivotOffset.IsEqualApprox(size / 2f);
        button.Size = target;
        if (pivotWasCentered)
            button.PivotOffset = target / 2f;
        PatchHelper.Log($"Near-thumb confirm button enlarged from {size} to {target}.");
    }

    private static bool TryFindSpot(NConfirmButton button, Control anchor, out Vector2 localPosition)
    {
        localPosition = default;
        if (button.GetParent() is not CanvasItem parent)
            return false;
        var parentTransform = parent.GetGlobalTransform();
        var parentScale = parentTransform.Scale;

        // Footprint in global (logical) units: the button rect plus its direct visual
        // children, which may draw outside the root rect.
        var localFootprint = new Rect2(Vector2.Zero, button.Size);
        foreach (var child in button.GetChildren())
        {
            if (child is Control control && control.Visible && control.Size.X > 0f && control.Size.Y > 0f)
                localFootprint = localFootprint.Merge(control.GetRect());
        }
        var footprintOffset = localFootprint.Position * parentScale;
        var footprintSize = localFootprint.Size * parentScale;

        var visible = button.GetViewport().GetVisibleRect();
        var safe = GetSafeRect(visible);
        if (footprintSize.X > safe.Size.X || footprintSize.Y > safe.Size.Y)
            return false;

        var item = GetAnchorRect(anchor);
        if (item.Size.X <= 0f || item.Size.Y <= 0f)
            return false;
        item = item.GrowIndividual(
            item.Size.X * ItemGrowthRatio, item.Size.Y * ItemGrowthRatio,
            item.Size.X * ItemGrowthRatio, item.Size.Y * ItemGrowthRatio);

        var obstacles = new List<Rect2>();
        var maxObstacleArea = visible.Size.X * visible.Size.Y * MaxObstacleAreaRatio;
        CollectTappables(parent, button, anchor, obstacles, maxObstacleArea);
        CollectHoverTips(obstacles);

        var w = footprintSize.X;
        var h = footprintSize.Y;
        var centerX = item.GetCenter().X;
        var candidates = new[]
        {
            new Vector2(centerX - w / 2f, item.End.Y + Gap),          // below, centered
            new Vector2(item.End.X - w, item.End.Y + Gap),            // below, right-aligned
            new Vector2(item.Position.X, item.End.Y + Gap),           // below, left-aligned
            new Vector2(item.End.X + Gap, item.End.Y - h),            // right side, lower edge
            new Vector2(item.Position.X - Gap - w, item.End.Y - h),   // left side, lower edge
            new Vector2(centerX - w / 2f, item.Position.Y - Gap - h), // above, centered
        };

        foreach (var topLeft in candidates)
        {
            var rect = ClampInto(new Rect2(topLeft, footprintSize), safe);
            if (rect.Intersects(item) || HitsAny(rect, obstacles))
                continue;
            localPosition = parentTransform.AffineInverse() * (rect.Position - footprintOffset);
            return true;
        }
        return false;
    }

    private static Rect2 GetAnchorRect(Control anchor)
    {
        Control hitbox = anchor switch
        {
            NCardHolder holder => holder.Hitbox,
            NMerchantSlot slot => slot.Hitbox,
            _ => null,
        };
        var rect = IsValid(hitbox) ? hitbox.GetGlobalRect() : anchor.GetGlobalRect();
        if (anchor is NMerchantSlot && CostLabelField?.GetValue(anchor) is Control costLabel
            && IsValid(costLabel) && costLabel.IsVisibleInTree() && costLabel.Size.X > 0f && costLabel.Size.Y > 0f)
            rect = rect.Merge(costLabel.GetGlobalRect());
        return rect;
    }

    private static void CollectTappables(Node node, NConfirmButton button, Control anchor, List<Rect2> obstacles, float maxArea)
    {
        foreach (var child in node.GetChildren())
        {
            if (ReferenceEquals(child, button) || ReferenceEquals(child, anchor))
                continue;
            if (child is CanvasItem canvasItem && !canvasItem.Visible)
                continue;
            if (child.IsQueuedForDeletion())
                continue;
            Control tappable = child switch
            {
                NCardHolder holder => holder.Hitbox,
                NMerchantSlot slot => slot.Hitbox,
                NClickableControl clickable => clickable,
                _ => null,
            };
            if (tappable != null)
            {
                if (IsValid(tappable) && tappable.IsVisibleInTree())
                    AddObstacle(tappable.GetGlobalRect(), obstacles, maxArea);
                continue;
            }
            if (child is not CanvasLayer)
                CollectTappables(child, button, anchor, obstacles, maxArea);
        }
    }

    private static void AddObstacle(Rect2 rect, List<Rect2> obstacles, float maxArea)
    {
        var area = rect.Size.X * rect.Size.Y;
        if (rect.Size.X <= 1f || rect.Size.Y <= 1f || area > maxArea)
            return;
        var growX = rect.Size.X * ObstacleGrowthRatio + Clearance;
        var growY = rect.Size.Y * ObstacleGrowthRatio + Clearance;
        obstacles.Add(rect.GrowIndividual(growX, growY, growX, growY));
    }

    private static void CollectHoverTips(List<Rect2> obstacles)
    {
        var container = NGame.Instance?.HoverTipsContainer;
        if (!IsValid(container))
            return;
        foreach (var tipSet in container.GetChildren())
        {
            if (tipSet.IsQueuedForDeletion() || tipSet is not CanvasItem { Visible: true })
                continue;
            foreach (var part in tipSet.GetChildren())
            {
                if (part is Control control && control.Visible && control.GetChildCount() > 0
                    && control.Size.X > 1f && control.Size.Y > 1f)
                    obstacles.Add(control.GetGlobalRect().Grow(8f));
            }
        }
    }

    private static Rect2 GetSafeRect(Rect2 visible)
    {
        var insetX = EdgeMargin;
        var insetTop = visible.Size.Y * TopReserveRatio;
        var insetBottom = EdgeMargin;
        try
        {
            var safeArea = DisplayServer.GetDisplaySafeArea();
            var window = DisplayServer.WindowGetSize();
            if (safeArea.Size.X > 0 && safeArea.Size.Y > 0 && window.X > 0 && window.Y > 0)
            {
                // Use the larger side on each axis so a 180-degree flip cannot put the
                // button under a cutout or the gesture area.
                var scaleX = visible.Size.X / window.X;
                var scaleY = visible.Size.Y / window.Y;
                var cutX = Math.Max(safeArea.Position.X, window.X - safeArea.End.X) * scaleX;
                var cutY = Math.Max(safeArea.Position.Y, window.Y - safeArea.End.Y) * scaleY;
                insetX = Math.Max(insetX, cutX);
                insetTop = Math.Max(insetTop, cutY);
                insetBottom = Math.Max(insetBottom, cutY);
            }
        }
        catch
        {
        }
        return new Rect2(
            visible.Position + new Vector2(insetX, insetTop),
            new Vector2(Math.Max(0f, visible.Size.X - 2f * insetX), Math.Max(0f, visible.Size.Y - insetTop - insetBottom)));
    }

    private static Rect2 ClampInto(Rect2 rect, Rect2 bounds)
    {
        var x = Math.Clamp(rect.Position.X, bounds.Position.X, bounds.End.X - rect.Size.X);
        var y = Math.Clamp(rect.Position.Y, bounds.Position.Y, bounds.End.Y - rect.Size.Y);
        return new Rect2(new Vector2(x, y), rect.Size);
    }

    private static bool HitsAny(Rect2 rect, List<Rect2> obstacles)
    {
        foreach (var obstacle in obstacles)
        {
            if (rect.Intersects(obstacle))
                return true;
        }
        return false;
    }

    private static Vector2 GetParentScale(Control control)
    {
        return control.GetParent() is CanvasItem parent ? parent.GetGlobalTransform().Scale : Vector2.One;
    }

    private static bool IsValid(GodotObject value)
    {
        return value != null && GodotObject.IsInstanceValid(value);
    }

    private sealed class PlacementState
    {
        public bool Modified;
        public bool Shown;
        public Control Anchor;
        public Tween Fade;
    }
}
