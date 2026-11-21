using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Finger-friendly run map. Vanilla only drag-scrolls when the press lands on the
/// map background (NMapScreen._GuiInput); a press on a map point is swallowed by the
/// point, so the map cannot be dragged from a node and a short drag that ends on the
/// pressed node still travels. This adds: drag-from-anywhere-on-the-map with a slop
/// threshold, fling momentum, tap/drag disambiguation (a drag never selects), and a
/// small tap-assist radius around reachable nodes for taps on empty map space.
/// </summary>
public static class MobileMapTouchPatches
{
    private const string SettingKey = "mobile_map_touch";
    private const float DragSlop = 24f;
    private const float TapAssistPadding = 48f;
    private const ulong TapMaxDurationUsec = 600_000;
    private const ulong VelocityWindowUsec = 100_000;
    private const ulong VelocityStaleUsec = 80_000;
    private const float MinFlingVelocity = 250f;
    private const float MaxFlingVelocity = 6000f;
    private const float StopFlingVelocity = 25f;
    private const float CatchFlingVelocity = 150f;
    private const float FlingFriction = 4f;

    private static AccessTools.FieldRef<NMapScreen, Vector2> _targetDragPos;
    private static AccessTools.FieldRef<NMapScreen, bool> _isDragging;
    private static AccessTools.FieldRef<NMapScreen, Dictionary<MapCoord, NMapPoint>> _mapPoints;
    private static AccessTools.FieldRef<NClickableControl, bool> _isPressed;
    private static FieldInfo _actAnimTween;
    private static FieldInfo _drawingInput;
    private static MethodInfo _canScroll;
    private static MethodInfo _forceClick;
    private static float _scrollLimitLow = -600f;
    private static float _scrollLimitHigh = 1800f;

    private static readonly Gesture Current = new();
    private static NMapScreen _flingScreen;
    private static float _flingVelocity;

    public static void Apply(Harmony harmony)
    {
        try
        {
            _targetDragPos = AccessTools.FieldRefAccess<NMapScreen, Vector2>("_targetDragPos");
            _isDragging = AccessTools.FieldRefAccess<NMapScreen, bool>("_isDragging");
            _mapPoints = AccessTools.FieldRefAccess<NMapScreen, Dictionary<MapCoord, NMapPoint>>("_mapPointDictionary");
            _isPressed = AccessTools.FieldRefAccess<NClickableControl, bool>("_isPressed");
            _actAnimTween = AccessTools.Field(typeof(NMapScreen), "_actAnimTween");
            _drawingInput = AccessTools.Field(typeof(NMapScreen), "_drawingInput");
            _canScroll = AccessTools.Method(typeof(NMapScreen), "CanScroll");
            _forceClick = AccessTools.Method(typeof(NClickableControl), "ForceClick");
            if (_actAnimTween == null || _drawingInput == null || _canScroll == null || _forceClick == null)
                throw new MissingMemberException("NMapScreen/NClickableControl map input members");
            ReadScrollLimits();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"SKIPPED mobile map touch controls: {exception.Message}");
            return;
        }

        var type = typeof(MobileMapTouchPatches);
        PatchHelper.Patch(harmony, typeof(NMapScreen), "_Input", prefix: PatchHelper.Method(type, nameof(MapInputPrefix)));
        PatchHelper.Patch(harmony, typeof(NMapScreen), "_GuiInput", prefix: PatchHelper.Method(type, nameof(MapGuiInputPrefix)));
        PatchHelper.Patch(harmony, typeof(NClickableControl), "_GuiInput", prefix: PatchHelper.Method(type, nameof(ClickableGuiInputPrefix)));
        PatchHelper.Patch(harmony, typeof(NMapScreen), "_Process", postfix: PatchHelper.Method(type, nameof(MapProcessPostfix)));
    }

    private static void ReadScrollLimits()
    {
        // Private consts; fall back to the v0.111 literals if they move or stop being consts.
        _scrollLimitLow = ReadConst("_scrollLimitBottom", _scrollLimitLow);
        _scrollLimitHigh = ReadConst("_scrollLimitTop", _scrollLimitHigh);
    }

    private static float ReadConst(string name, float fallback)
    {
        try
        {
            var field = AccessTools.Field(typeof(NMapScreen), name);
            return field != null && field.IsLiteral && field.GetRawConstantValue() is float value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool Enabled()
    {
        return MobileInputPolicyPatches.UseTouchControls && AndroidSettingsBridge.GetBool(SettingKey, true);
    }

    // Input phase: runs before any GUI delivery for every event while the map is processing.
    public static void MapInputPrefix(NMapScreen __instance, InputEvent inputEvent)
    {
        try
        {
            switch (inputEvent)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                    if (button.Pressed)
                        OnPress(__instance, button);
                    else
                        OnRelease(__instance, button);
                    break;
                case InputEventMouseMotion motion when Current.Confirmed && ReferenceEquals(Current.Screen, __instance):
                    if (OnMotion(__instance, motion))
                        __instance.GetViewport()?.SetInputAsHandled();
                    break;
                case InputEventScreenTouch { Pressed: false } when Current.Confirmed:
                    // Godot dispatches the emulated mouse release before the touch release,
                    // so a confirmed gesture here means something upstream ate that release.
                    AbandonGesture();
                    break;
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile map touch input failed: {exception.Message}");
            AbandonGesture();
        }
    }

    // GUI phase: the press reached the map background.
    public static void MapGuiInputPrefix(NMapScreen __instance, InputEvent inputEvent)
    {
        TryConfirm(__instance, inputEvent, null);
    }

    // GUI phase: the press reached a map point (Godot's own hit test decided this).
    public static void ClickableGuiInputPrefix(NClickableControl __instance, InputEvent inputEvent)
    {
        if (__instance is NMapPoint point)
            TryConfirm(Current.Screen, inputEvent, point);
    }

    public static void MapProcessPostfix(NMapScreen __instance, double delta)
    {
        if (_flingVelocity == 0f || !ReferenceEquals(_flingScreen, __instance))
            return;
        try
        {
            if (!Enabled() || !__instance.IsOpen || !__instance.IsVisibleInTree() || Current.Confirmed || !CanGesture(__instance))
            {
                StopFling();
                return;
            }
            var dt = (float)delta;
            ref var target = ref _targetDragPos(__instance);
            target.Y += _flingVelocity * dt;
            _flingVelocity *= Mathf.Exp(-FlingFriction * dt);
            // Past the limits vanilla's bounce-back takes over.
            if (target.Y <= _scrollLimitLow || target.Y >= _scrollLimitHigh || Mathf.Abs(_flingVelocity) < StopFlingVelocity)
                StopFling();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile map fling failed: {exception.Message}");
            StopFling();
        }
    }

    private static void AbandonGesture()
    {
        if (Current.DraggingFlagSet && Current.Screen != null && GodotObject.IsInstanceValid(Current.Screen))
            _isDragging(Current.Screen) = false;
        Current.Reset();
    }

    private static void OnPress(NMapScreen screen, InputEventMouseButton button)
    {
        AbandonGesture();
        if (!MobileInputPolicyPatches.IsTouchPointerEvent(button) || !Enabled())
            return;
        var caughtFling = ReferenceEquals(_flingScreen, screen) && Mathf.Abs(_flingVelocity) > CatchFlingVelocity;
        if (_flingVelocity != 0f && ReferenceEquals(_flingScreen, screen))
            _targetDragPos(screen) = new Vector2(0f, ScrollContainerY(screen));
        StopFling();
        Current.Screen = screen;
        Current.Pending = true;
        Current.Frame = Engine.GetProcessFrames();
        Current.StartUsec = Time.GetTicksUsec();
        Current.PressPosition = button.Position;
        Current.CaughtFling = caughtFling;
    }

    private static void TryConfirm(NMapScreen screen, InputEvent inputEvent, NMapPoint point)
    {
        try
        {
            if (!Current.Pending || screen == null || !ReferenceEquals(Current.Screen, screen))
                return;
            if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } button
                || !MobileInputPolicyPatches.IsTouchPointerEvent(button) || Current.Frame != Engine.GetProcessFrames())
                return;
            if (Current.Confirmed)
            {
                // A point with a Pass mouse filter forwards the same press to the screen.
                return;
            }
            if (!screen.IsOpen || !screen.IsVisibleInTree() || !ActiveScreenContext.Instance.IsCurrent(screen) || !CanGesture(screen))
            {
                Current.Reset();
                return;
            }
            Current.Confirmed = true;
            Current.PressedPoint = point;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile map touch confirm failed: {exception.Message}");
            Current.Reset();
        }
    }

    private static bool CanGesture(NMapScreen screen)
    {
        if (screen.IsTraveling || _drawingInput.GetValue(screen) != null || _actAnimTween.GetValue(screen) != null)
            return false;
        if (screen.Drawings != null && screen.Drawings.GetLocalDrawingMode() != DrawingMode.None)
            return false;
        return _canScroll.Invoke(screen, null) is true;
    }

    private static bool OnMotion(NMapScreen screen, InputEventMouseMotion motion)
    {
        if (!MobileInputPolicyPatches.IsTouchPointerEvent(motion))
            return false;
        if (!CanGesture(screen))
        {
            // Drawing started or travel began mid-gesture: hand everything back to vanilla.
            AbandonGesture();
            return false;
        }
        Current.PendingDeltaY += motion.Relative.Y;
        if (!Current.Scrolling)
        {
            if (motion.Position.DistanceSquaredTo(Current.PressPosition) < DragSlop * DragSlop)
                return true;
            Current.Scrolling = true;
            _isDragging(screen) = true;
            Current.DraggingFlagSet = true;
            if (Current.PressedPoint != null && GodotObject.IsInstanceValid(Current.PressedPoint))
                NHoverTipSet.Remove(Current.PressedPoint);
        }
        _targetDragPos(screen) += new Vector2(0f, Current.PendingDeltaY);
        Current.ScrolledY += Current.PendingDeltaY;
        Current.PendingDeltaY = 0f;
        Current.AddSample(Time.GetTicksUsec());
        return true;
    }

    private static void OnRelease(NMapScreen screen, InputEventMouseButton button)
    {
        if (!Current.Confirmed || !ReferenceEquals(Current.Screen, screen))
        {
            Current.Reset();
            return;
        }
        try
        {
            if (Current.DraggingFlagSet)
                _isDragging(screen) = false;
            if (Current.Scrolling || Current.CaughtFling || button.Canceled)
            {
                // The release still reaches the GUI (Godot must clear its mouse focus);
                // un-press the points so NClickableControl.OnReleaseHandler is a no-op.
                ClearPressedPoints(screen);
                if (Current.Scrolling && !button.Canceled)
                    StartFling(screen, Current.EstimateVelocity(Time.GetTicksUsec()));
                return;
            }
            if (Current.PressedPoint == null && Time.GetTicksUsec() - Current.StartUsec <= TapMaxDurationUsec)
                TryTapAssist(screen, Current.PressPosition);
        }
        finally
        {
            Current.Reset();
        }
    }

    private static void ClearPressedPoints(NMapScreen screen)
    {
        var points = _mapPoints(screen);
        if (points == null)
            return;
        foreach (var point in points.Values)
        {
            if (point != null && GodotObject.IsInstanceValid(point))
                _isPressed(point) = false;
        }
    }

    private static void TryTapAssist(NMapScreen screen, Vector2 position)
    {
        if (!screen.IsTravelEnabled || screen.IsTraveling)
            return;
        var points = _mapPoints(screen);
        if (points == null)
            return;
        NMapPoint best = null;
        var bestDistance = TapAssistPadding;
        foreach (var point in points.Values)
        {
            if (point == null || !GodotObject.IsInstanceValid(point) || !point.IsVisibleInTree())
                continue;
            var distance = DistanceToRect(ScreenRect(point), position);
            if (distance <= 0f)
                return; // Inside a point that Godot did not route the press to: leave it alone.
            if (point.IsEnabled && distance < bestDistance)
            {
                best = point;
                bestDistance = distance;
            }
        }
        if (best == null)
            return;
        PatchHelper.Log($"Map tap assist: selecting {best.Point?.coord} ({bestDistance:0}px from tap).");
        // ForceClick runs NMapPoint.OnRelease, which keeps every vanilla travel check.
        _forceClick.Invoke(best, null);
    }

    private static Rect2 ScreenRect(Control control)
    {
        var transform = control.GetGlobalTransformWithCanvas();
        var scale = transform.Scale;
        var size = new Vector2(control.Size.X * Mathf.Abs(scale.X), control.Size.Y * Mathf.Abs(scale.Y));
        return new Rect2(transform.Origin, size);
    }

    private static float DistanceToRect(Rect2 rect, Vector2 point)
    {
        var dx = Mathf.Max(Mathf.Max(rect.Position.X - point.X, 0f), point.X - rect.End.X);
        var dy = Mathf.Max(Mathf.Max(rect.Position.Y - point.Y, 0f), point.Y - rect.End.Y);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private static float ScrollContainerY(NMapScreen screen)
    {
        // TheMap is the node vanilla lerps toward _targetDragPos.
        return screen.GetNodeOrNull<Control>("TheMap")?.Position.Y ?? _targetDragPos(screen).Y;
    }

    private static void StartFling(NMapScreen screen, float velocity)
    {
        if (Mathf.Abs(velocity) < MinFlingVelocity)
            return;
        _flingScreen = screen;
        _flingVelocity = Mathf.Clamp(velocity, -MaxFlingVelocity, MaxFlingVelocity);
    }

    private static void StopFling()
    {
        _flingVelocity = 0f;
        _flingScreen = null;
    }

    private sealed class Gesture
    {
        private const int SampleCount = 8;
        private readonly ulong[] _sampleTimes = new ulong[SampleCount];
        private readonly float[] _sampleY = new float[SampleCount];
        private int _samples;
        private int _next;

        public NMapScreen Screen;
        public NMapPoint PressedPoint;
        public bool Pending;
        public bool Confirmed;
        public bool Scrolling;
        public bool CaughtFling;
        public bool DraggingFlagSet;
        public ulong Frame;
        public ulong StartUsec;
        public Vector2 PressPosition;
        public float PendingDeltaY;
        public float ScrolledY;

        public void AddSample(ulong time)
        {
            _sampleTimes[_next] = time;
            _sampleY[_next] = ScrolledY;
            _next = (_next + 1) % SampleCount;
            if (_samples < SampleCount)
                _samples++;
        }

        public float EstimateVelocity(ulong now)
        {
            if (_samples < 2)
                return 0f;
            var newest = (_next - 1 + SampleCount) % SampleCount;
            var newestTime = _sampleTimes[newest];
            if (now - newestTime > VelocityStaleUsec)
                return 0f; // The finger rested before lifting.
            var oldest = newest;
            for (var i = 1; i < _samples; i++)
            {
                var index = (newest - i + SampleCount) % SampleCount;
                if (newestTime - _sampleTimes[index] > VelocityWindowUsec)
                    break;
                oldest = index;
            }
            var seconds = (newestTime - _sampleTimes[oldest]) / 1_000_000f;
            return seconds < 0.008f ? 0f : (_sampleY[newest] - _sampleY[oldest]) / seconds;
        }

        public void Reset()
        {
            Screen = null;
            PressedPoint = null;
            Pending = Confirmed = Scrolling = CaughtFling = DraggingFlagSet = false;
            Frame = StartUsec = 0;
            PressPosition = Vector2.Zero;
            PendingDeltaY = ScrolledY = 0f;
            _samples = _next = 0;
        }
    }
}
