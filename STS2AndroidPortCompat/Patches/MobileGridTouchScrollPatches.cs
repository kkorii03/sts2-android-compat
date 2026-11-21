using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Finger-friendly scrolling for the game's own scroll surfaces outside combat:
/// card grids (deck/pile views, remove/upgrade/transform/enchant selection, card
/// library), NScrollableContainer screens (settings, relic collection, run history,
/// bestiary, ...) and the rewards list.
///
/// Vanilla drags those with raw mouse deltas and no slop, so a drag that starts on a
/// card/button moves the content under the finger and the release then lands on the
/// same item and clicks it (selects the card, opens inspect, toggles the tickbox,
/// claims the reward). Whether a press on an item reaches the scroller at all depends
/// on the item's mouse filter, and the rewards list has no drag scrolling at all.
///
/// This adds, for touch only: drag-from-anywhere-on-the-content with a slop
/// threshold, a release after a drag (or a tap that catches a fling) never clicks,
/// fling momentum, and light overscroll damping. Same shape as
/// <see cref="MobileMapTouchPatches"/>: press noted in the input phase, confirmed in
/// the GUI phase by Godot's own hit test, motions consumed only while confirmed,
/// every failure falls back to vanilla.
/// </summary>
public static class MobileGridTouchScrollPatches
{
    private const string SettingKey = "mobile_grid_touch_scroll";
    private const float DragSlop = 24f;
    private const float OverscrollFactor = 0.4f;
    private const float MinFlingVelocity = 250f;
    private const float MaxFlingVelocity = 6000f;
    private const float StopFlingVelocity = 25f;
    private const float CatchFlingVelocity = 150f;
    private const float FlingFriction = 4f;

    private static readonly List<Adapter> Adapters = new();
    private static AccessTools.FieldRef<NClickableControl, bool> _isPressed;
    private static AccessTools.FieldRef<NCardHolder, InputEventMouseButton> _currentPressedAction;

    private static readonly Gesture Current = new();
    private static Control _flingTarget;
    private static Adapter _flingAdapter;
    private static float _flingVelocity;

    public static void Apply(Harmony harmony)
    {
        try
        {
            _isPressed = AccessTools.FieldRefAccess<NClickableControl, bool>("_isPressed");
            _currentPressedAction = AccessTools.FieldRefAccess<NCardHolder, InputEventMouseButton>("_currentPressedAction");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"SKIPPED mobile grid touch scroll: {exception.Message}");
            return;
        }

        var type = typeof(MobileGridTouchScrollPatches);
        TryAdd(harmony, "card grid", () => new GridAdapter(), typeof(NCardGrid), nameof(GridGuiInputPrefix), nameof(GridProcessPostfix));
        TryAdd(harmony, "scrollable container", () => new ContainerAdapter(), typeof(NScrollableContainer), nameof(ContainerGuiInputPrefix), nameof(ContainerProcessPostfix));
        TryAdd(harmony, "rewards list", () => new RewardsAdapter(), typeof(NRewardsScreen), nameof(RewardsGuiInputPrefix), nameof(RewardsProcessPostfix));
        if (Adapters.Count == 0)
        {
            PatchHelper.Log("SKIPPED mobile grid touch scroll: no scroll surface resolved.");
            return;
        }
        PatchHelper.Patch(harmony, typeof(NGame), "_Input", prefix: PatchHelper.Method(type, nameof(GameInputPrefix)));
        PatchHelper.Patch(harmony, typeof(NGame), "_Notification", postfix: PatchHelper.Method(type, nameof(GameNotificationPostfix)));
        PatchHelper.Patch(harmony, typeof(NClickableControl), "_GuiInput", prefix: PatchHelper.Method(type, nameof(ClickableGuiInputPrefix)));
    }

    private static void TryAdd(Harmony harmony, string label, Func<Adapter> create, Type nodeType, string guiPrefix, string processPostfix)
    {
        Adapter adapter;
        try
        {
            adapter = create();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"SKIPPED mobile touch scroll for {label}: {exception.Message}");
            return;
        }
        var type = typeof(MobileGridTouchScrollPatches);
        PatchHelper.Patch(harmony, nodeType, "_GuiInput", prefix: PatchHelper.Method(type, guiPrefix));
        PatchHelper.Patch(harmony, nodeType, "_Process", postfix: PatchHelper.Method(type, processPostfix));
        Adapters.Add(adapter);
    }

    private static bool Enabled()
    {
        return MobileInputPolicyPatches.UseTouchControls && AndroidSettingsBridge.GetBool(SettingKey, true);
    }

    // Input phase: NGame is an ancestor of every screen, so this runs after their own
    // _Input and before any GUI delivery.
    public static void GameInputPrefix(NGame __instance, InputEvent inputEvent)
    {
        try
        {
            switch (inputEvent)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                    if (button.Pressed)
                        OnPress(button);
                    else
                        OnRelease(button);
                    break;
                case InputEventMouseMotion motion when Current.Confirmed:
                    if (OnMotion(motion))
                        __instance.GetViewport()?.SetInputAsHandled();
                    break;
                case InputEventScreenTouch { Pressed: false } when Current.Confirmed && !Input.IsMouseButtonPressed(MouseButton.Left):
                    // The emulated mouse release precedes the touch release; reaching this
                    // with a live gesture means something upstream ate that release.
                    AbandonGesture();
                    break;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile grid touch input failed: {exception.Message}");
            AbandonGesture();
        }
    }

    public static void GameNotificationPostfix(int what)
    {
        if (what == (int)Node.NotificationApplicationPaused
            || what == (int)Node.NotificationApplicationFocusOut
            || what == (int)Node.NotificationWMWindowFocusOut)
            Reset();
    }

    // GUI phase: the press reached the scroll surface itself (background between items).
    public static void GridGuiInputPrefix(NCardGrid __instance, InputEvent inputEvent) => TryConfirmDirect(__instance, inputEvent);

    public static void ContainerGuiInputPrefix(NScrollableContainer __instance, InputEvent inputEvent) => TryConfirmDirect(__instance, inputEvent);

    public static void RewardsGuiInputPrefix(NRewardsScreen __instance, InputEvent inputEvent) => TryConfirmDirect(__instance, inputEvent);

    // GUI phase: the press reached a clickable item (Godot's own hit test decided this).
    public static void ClickableGuiInputPrefix(NClickableControl __instance, InputEvent inputEvent)
    {
        if (!Current.Pending || !IsFreshTouchPress(inputEvent))
            return;
        try
        {
            if (!TryFindSurface(__instance, out var target, out var adapter))
            {
                // The press belongs to a fixed button (or a nested scroller), not to scroll
                // content; a Pass filter must not let it bubble into a surface drag.
                if (!Current.Confirmed)
                    Current.Reset();
                return;
            }
            if (Current.Confirmed)
            {
                // A Pass mouse filter forwards the same press to ancestors that are clickable too.
                if (ReferenceEquals(Current.Target, target))
                    Current.Pressed.Add(__instance);
                return;
            }
            if (!adapter.CanScroll(target))
            {
                Current.Reset();
                return;
            }
            Confirm(target, adapter);
            Current.Pressed.Add(__instance);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile grid touch confirm failed: {exception.Message}");
            Current.Reset();
        }
    }

    public static void GridProcessPostfix(NCardGrid __instance, double delta) => StepFling(__instance, delta);

    public static void ContainerProcessPostfix(NScrollableContainer __instance, double delta) => StepFling(__instance, delta);

    public static void RewardsProcessPostfix(NRewardsScreen __instance, double delta) => StepFling(__instance, delta);

    /// <summary>Drops the gesture and any fling (pause, focus loss, input-mode change).</summary>
    public static void Reset()
    {
        try
        {
            AbandonGesture();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile grid touch reset failed: {exception.Message}");
            Current.Reset();
        }
        StopFling();
    }

    private static bool IsFreshTouchPress(InputEvent inputEvent)
    {
        return inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } button
            && MobileInputPolicyPatches.IsTouchPointerEvent(button)
            && Current.Frame == Engine.GetProcessFrames();
    }

    private static void TryConfirmDirect(Control surface, InputEvent inputEvent)
    {
        if (!Current.Pending || Current.Confirmed || !IsFreshTouchPress(inputEvent))
            return;
        try
        {
            var adapter = FindAdapter(surface);
            if (adapter == null || !adapter.CanScroll(surface) || !adapter.AcceptsDirectPress(surface, Current.PressPosition))
            {
                Current.Reset();
                return;
            }
            var scrollbar = adapter.Scrollbar(surface);
            if (scrollbar != null && GodotObject.IsInstanceValid(scrollbar) && scrollbar.IsVisibleInTree()
                && ScreenRect(scrollbar).HasPoint(Current.PressPosition))
            {
                Current.Reset();
                return;
            }
            Confirm(surface, adapter);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile grid touch confirm failed: {exception.Message}");
            Current.Reset();
        }
    }

    private static void Confirm(Control target, Adapter adapter)
    {
        Current.Target = target;
        Current.Adapter = adapter;
        Current.Confirmed = true;
    }

    private static Adapter FindAdapter(Node node)
    {
        foreach (var adapter in Adapters)
        {
            if (adapter.Matches(node))
                return adapter;
        }
        return null;
    }

    private static bool TryFindSurface(Control clickable, out Control target, out Adapter adapter)
    {
        for (var node = clickable.GetParent(); node != null; node = node.GetParent())
        {
            // Nested scrollers (open dropdown lists, plain Godot scroll containers) keep vanilla input.
            if (node is ScrollContainer || node is NDropdownContainer)
                break;
            adapter = FindAdapter(node);
            if (adapter == null)
                continue;
            target = (Control)node;
            var content = adapter.Content(target);
            // Scrollbars and fixed buttons are children of the surface but not of its content.
            return content != null && GodotObject.IsInstanceValid(content) && content.IsAncestorOf(clickable);
        }
        target = null;
        adapter = null;
        return false;
    }

    private static void OnPress(InputEventMouseButton button)
    {
        AbandonGesture();
        if (!MobileInputPolicyPatches.IsTouchPointerEvent(button) || !Enabled())
        {
            StopFling();
            return;
        }
        var caughtFling = false;
        if (_flingVelocity != 0f && IsAlive(_flingTarget))
        {
            caughtFling = Mathf.Abs(_flingVelocity) > CatchFlingVelocity;
            // Stop where the content is now, not where the fling was heading.
            _flingAdapter.SetTarget(_flingTarget, _flingAdapter.CurrentPosition(_flingTarget));
        }
        StopFling();
        Current.Pending = true;
        Current.Frame = Engine.GetProcessFrames();
        Current.PressPosition = button.Position;
        Current.CaughtFling = caughtFling;
    }

    private static bool OnMotion(InputEventMouseMotion motion)
    {
        if (!MobileInputPolicyPatches.IsTouchPointerEvent(motion))
            return false;
        var target = Current.Target;
        var adapter = Current.Adapter;
        if (!IsAlive(target) || adapter == null || !Enabled() || !target.IsVisibleInTree() || !adapter.CanScroll(target))
        {
            AbandonGesture();
            return false;
        }
        Current.PendingDelta += motion.Relative.Y;
        if (!Current.Scrolling)
        {
            var moved = motion.Position - Current.PressPosition;
            if (!MobileScrollGestureMath.ExceedsSlop(moved.X, moved.Y, DragSlop))
                return true;
            Current.Scrolling = true;
            adapter.SetDragging(target, true);
            Current.DraggingFlagSet = true;
            RemovePressedHoverTips();
        }
        adapter.GetLimits(target, out var low, out var high);
        adapter.SetTarget(target, MobileScrollGestureMath.ApplyDrag(adapter.GetTarget(target), Current.PendingDelta, low, high, OverscrollFactor));
        Current.Scrolled += Current.PendingDelta;
        Current.PendingDelta = 0f;
        Current.Velocity.Add(Time.GetTicksUsec(), Current.Scrolled);
        return true;
    }

    private static void OnRelease(InputEventMouseButton button)
    {
        if (!Current.Confirmed)
        {
            Current.Reset();
            return;
        }
        try
        {
            var target = Current.Target;
            var adapter = Current.Adapter;
            if (!IsAlive(target) || adapter == null)
                return;
            if (Current.DraggingFlagSet)
                adapter.SetDragging(target, false);
            if (Current.Scrolling || Current.CaughtFling || button.Canceled)
            {
                // The release still reaches the GUI (Godot must clear its mouse focus);
                // un-press the items so their release handlers are no-ops.
                SuppressPressedClicks();
                if (Current.Scrolling && !button.Canceled)
                    StartFling(target, adapter, Current.Velocity.Estimate(Time.GetTicksUsec()));
            }
        }
        finally
        {
            Current.Reset();
        }
    }

    private static void AbandonGesture()
    {
        var target = Current.Target;
        if (IsAlive(target) && Current.Adapter != null)
        {
            if (Current.DraggingFlagSet)
                Current.Adapter.SetDragging(target, false);
            // A drag never clicks: a late release must not activate the start item.
            if (Current.Scrolling || Current.CaughtFling)
                SuppressPressedClicks();
        }
        Current.Reset();
    }

    private static void SuppressPressedClicks()
    {
        foreach (var clickable in Current.Pressed)
        {
            if (!IsAlive(clickable))
                continue;
            _isPressed(clickable) = false;
            var holder = FindHolder(clickable);
            if (holder != null)
                _currentPressedAction(holder) = null;
        }
    }

    private static void RemovePressedHoverTips()
    {
        foreach (var clickable in Current.Pressed)
        {
            if (!IsAlive(clickable))
                continue;
            NHoverTipSet.Remove(clickable);
            var holder = FindHolder(clickable);
            if (holder != null)
                NHoverTipSet.Remove(holder);
        }
    }

    private static NCardHolder FindHolder(Node clickable)
    {
        for (var node = clickable; node != null && !ReferenceEquals(node, Current.Target); node = node.GetParent())
        {
            if (node is NCardHolder holder)
                return holder;
        }
        return null;
    }

    private static void StartFling(Control target, Adapter adapter, float velocity)
    {
        if (Mathf.Abs(velocity) < MinFlingVelocity)
            return;
        _flingTarget = target;
        _flingAdapter = adapter;
        _flingVelocity = MobileScrollGestureMath.ClampMagnitude(velocity, MaxFlingVelocity);
    }

    private static void StepFling(Control instance, double delta)
    {
        if (_flingVelocity == 0f || !ReferenceEquals(_flingTarget, instance))
            return;
        try
        {
            if (!Enabled() || !instance.IsVisibleInTree() || Current.Confirmed || !_flingAdapter.CanScroll(instance))
            {
                StopFling();
                return;
            }
            var target = _flingAdapter.GetTarget(instance);
            var velocity = _flingVelocity;
            _flingAdapter.GetLimits(instance, out var low, out var high);
            var keepGoing = MobileScrollGestureMath.StepFling(ref target, ref velocity, (float)delta, FlingFriction, low, high, StopFlingVelocity);
            _flingAdapter.SetTarget(instance, target);
            _flingVelocity = velocity;
            if (!keepGoing)
                StopFling();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile grid fling failed: {exception.Message}");
            StopFling();
        }
    }

    private static void StopFling()
    {
        _flingVelocity = 0f;
        _flingTarget = null;
        _flingAdapter = null;
    }

    private static Rect2 ScreenRect(Control control)
    {
        var transform = control.GetGlobalTransformWithCanvas();
        var scale = transform.Scale;
        return new Rect2(transform.Origin, new Vector2(control.Size.X * Mathf.Abs(scale.X), control.Size.Y * Mathf.Abs(scale.Y)));
    }

    private static bool IsAlive(GodotObject value) => value != null && GodotObject.IsInstanceValid(value);

    private sealed class Gesture
    {
        public readonly List<NClickableControl> Pressed = new();
        public readonly MobileScrollGestureMath.VelocityTracker Velocity = new();
        public Control Target;
        public Adapter Adapter;
        public bool Pending;
        public bool Confirmed;
        public bool Scrolling;
        public bool CaughtFling;
        public bool DraggingFlagSet;
        public ulong Frame;
        public Vector2 PressPosition;
        public float PendingDelta;
        public float Scrolled;

        public void Reset()
        {
            Pressed.Clear();
            Velocity.Reset();
            Target = null;
            Adapter = null;
            Pending = Confirmed = Scrolling = CaughtFling = DraggingFlagSet = false;
            Frame = 0;
            PressPosition = Vector2.Zero;
            PendingDelta = Scrolled = 0f;
        }
    }

    /// <summary>Uniform access to one vanilla scroll surface's private drag state.</summary>
    private abstract class Adapter
    {
        public abstract bool Matches(Node node);
        public abstract Control Content(Control surface);
        public abstract bool CanScroll(Control surface);
        public abstract float GetTarget(Control surface);
        public abstract void SetTarget(Control surface, float value);
        public abstract float CurrentPosition(Control surface);
        public abstract void GetLimits(Control surface, out float low, out float high);
        public abstract Control Scrollbar(Control surface);
        public virtual void SetDragging(Control surface, bool dragging) { }
        public virtual bool AcceptsDirectPress(Control surface, Vector2 position) => true;

        protected static Func<T, TResult> Getter<T, TResult>(string property)
        {
            var getter = AccessTools.PropertyGetter(typeof(T), property)
                ?? throw new MissingMemberException(typeof(T).Name, property);
            return AccessTools.MethodDelegate<Func<T, TResult>>(getter);
        }
    }

    private sealed class GridAdapter : Adapter
    {
        private readonly AccessTools.FieldRef<NCardGrid, float> _targetDrag = AccessTools.FieldRefAccess<NCardGrid, float>("_targetDrag");
        private readonly AccessTools.FieldRef<NCardGrid, bool> _isDragging = AccessTools.FieldRefAccess<NCardGrid, bool>("_isDragging");
        private readonly AccessTools.FieldRef<NCardGrid, Control> _scrollContainer = AccessTools.FieldRefAccess<NCardGrid, Control>("_scrollContainer");
        private readonly AccessTools.FieldRef<NCardGrid, NScrollbar> _scrollbar = AccessTools.FieldRefAccess<NCardGrid, NScrollbar>("_scrollbar");
        private readonly Func<NCardGrid, bool> _canScroll = Getter<NCardGrid, bool>("CanScroll");
        private readonly Func<NCardGrid, float> _limitBottom = Getter<NCardGrid, float>("ScrollLimitBottom");
        private readonly Func<NCardGrid, float> _limitTop = Getter<NCardGrid, float>("ScrollLimitTop");

        public override bool Matches(Node node) => node is NCardGrid;
        public override Control Content(Control surface) => _scrollContainer((NCardGrid)surface);
        public override bool CanScroll(Control surface) => surface.IsVisibleInTree() && _scrollContainer((NCardGrid)surface) != null && _canScroll((NCardGrid)surface);
        public override float GetTarget(Control surface) => _targetDrag((NCardGrid)surface);
        public override void SetTarget(Control surface, float value) => _targetDrag((NCardGrid)surface) = value;
        public override float CurrentPosition(Control surface) => _scrollContainer((NCardGrid)surface).Position.Y;
        public override Control Scrollbar(Control surface) => _scrollbar((NCardGrid)surface);
        public override void SetDragging(Control surface, bool dragging) => _isDragging((NCardGrid)surface) = dragging;

        public override void GetLimits(Control surface, out float low, out float high)
        {
            var grid = (NCardGrid)surface;
            var bottom = _limitBottom(grid);
            var top = _limitTop(grid);
            low = Mathf.Min(bottom, top);
            high = Mathf.Max(bottom, top);
        }
    }

    private sealed class ContainerAdapter : Adapter
    {
        private readonly AccessTools.FieldRef<NScrollableContainer, float> _targetDragPosY = AccessTools.FieldRefAccess<NScrollableContainer, float>("_targetDragPosY");
        private readonly AccessTools.FieldRef<NScrollableContainer, bool> _isDragging = AccessTools.FieldRefAccess<NScrollableContainer, bool>("_isDragging");
        private readonly AccessTools.FieldRef<NScrollableContainer, float> _paddingTop = AccessTools.FieldRefAccess<NScrollableContainer, float>("_paddingTop");
        private readonly AccessTools.FieldRef<NScrollableContainer, Control> _content = AccessTools.FieldRefAccess<NScrollableContainer, Control>("_content");
        private readonly Func<NScrollableContainer, float> _limitBottom = Getter<NScrollableContainer, float>("ScrollLimitBottom");

        public override bool Matches(Node node) => node is NScrollableContainer;
        public override Control Content(Control surface) => _content((NScrollableContainer)surface);
        public override float GetTarget(Control surface) => _targetDragPosY((NScrollableContainer)surface);
        public override void SetTarget(Control surface, float value) => _targetDragPosY((NScrollableContainer)surface) = value;
        public override Control Scrollbar(Control surface) => ((NScrollableContainer)surface).Scrollbar;
        public override void SetDragging(Control surface, bool dragging) => _isDragging((NScrollableContainer)surface) = dragging;

        public override bool CanScroll(Control surface)
        {
            var container = (NScrollableContainer)surface;
            var content = _content(container);
            // Vanilla shows the scrollbar exactly when the content overflows its viewport.
            return container.IsVisibleInTree() && content != null && GodotObject.IsInstanceValid(content)
                && container.Scrollbar != null && container.Scrollbar.Visible;
        }

        public override float CurrentPosition(Control surface)
        {
            var container = (NScrollableContainer)surface;
            return _content(container).Position.Y - _paddingTop(container);
        }

        public override void GetLimits(Control surface, out float low, out float high)
        {
            var bottom = _limitBottom((NScrollableContainer)surface);
            low = Mathf.Min(bottom, 0f);
            high = Mathf.Max(bottom, 0f);
        }

        // Settings sliders (NSlider never accepts its press) and other non-NClickableControl
        // widgets can let a press bubble to the container; their own drags must keep working.
        public override bool AcceptsDirectPress(Control surface, Vector2 position)
        {
            var content = _content((NScrollableContainer)surface);
            return content != null && !HasBlockingControlAt(content, position, 0);
        }

        private static bool HasBlockingControlAt(Node node, Vector2 position, int depth)
        {
            if (depth > 32)
                return false;
            var count = node.GetChildCount();
            for (var i = 0; i < count; i++)
            {
                var child = node.GetChild(i);
                if (child is CanvasItem { Visible: false })
                    continue;
                if (child is Control control && IsBlocker(control)
                    && control.MouseFilter != Control.MouseFilterEnum.Ignore
                    && ScreenRect(control).HasPoint(position))
                    return true;
                if (HasBlockingControlAt(child, position, depth + 1))
                    return true;
            }
            return false;
        }

        private static bool IsBlocker(Control control)
        {
            return control is Godot.Range or LineEdit or TextEdit or BaseButton or ScrollContainer or ItemList or NDropdownContainer;
        }
    }

    private sealed class RewardsAdapter : Adapter
    {
        // Private const _scrollLimitTop in v0.111.
        private const float LimitTop = 35f;
        private readonly AccessTools.FieldRef<NRewardsScreen, Vector2> _targetDragPos = AccessTools.FieldRefAccess<NRewardsScreen, Vector2>("_targetDragPos");
        private readonly AccessTools.FieldRef<NRewardsScreen, Control> _rewardsContainer = AccessTools.FieldRefAccess<NRewardsScreen, Control>("_rewardsContainer");
        private readonly AccessTools.FieldRef<NRewardsScreen, Control> _rewardContainerMask = AccessTools.FieldRefAccess<NRewardsScreen, Control>("_rewardContainerMask");
        private readonly AccessTools.FieldRef<NRewardsScreen, NScrollbar> _scrollbar = AccessTools.FieldRefAccess<NRewardsScreen, NScrollbar>("_scrollbar");
        private readonly Func<NRewardsScreen, bool> _canScroll = Getter<NRewardsScreen, bool>("CanScroll");
        private readonly Func<NRewardsScreen, float> _limitBottom = Getter<NRewardsScreen, float>("ScrollLimitBottom");

        public override bool Matches(Node node) => node is NRewardsScreen;
        public override Control Content(Control surface) => _rewardsContainer((NRewardsScreen)surface);
        public override float GetTarget(Control surface) => _targetDragPos((NRewardsScreen)surface).Y;
        public override float CurrentPosition(Control surface) => _rewardsContainer((NRewardsScreen)surface).Position.Y;
        public override Control Scrollbar(Control surface) => _scrollbar((NRewardsScreen)surface);

        public override bool CanScroll(Control surface)
        {
            var screen = (NRewardsScreen)surface;
            return screen.IsVisibleInTree() && _rewardsContainer(screen) != null && _canScroll(screen);
        }

        public override void SetTarget(Control surface, float value)
        {
            ref var target = ref _targetDragPos((NRewardsScreen)surface);
            target.Y = value;
        }

        public override void GetLimits(Control surface, out float low, out float high)
        {
            var bottom = _limitBottom((NRewardsScreen)surface);
            low = Mathf.Min(bottom, LimitTop);
            high = Mathf.Max(bottom, LimitTop);
        }

        // The screen is full-rect; only the list window scrolls.
        public override bool AcceptsDirectPress(Control surface, Vector2 position)
        {
            var mask = _rewardContainerMask((NRewardsScreen)surface);
            return mask != null && GodotObject.IsInstanceValid(mask) && mask.IsVisibleInTree() && ScreenRect(mask).HasPoint(position);
        }
    }
}
