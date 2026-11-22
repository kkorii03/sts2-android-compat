using System;
using System.Collections.Generic;

namespace STS2Mobile.Patches;

/// <summary>
/// Engine-free bookkeeping for <see cref="MobileHoverReleasePatches"/>: decides
/// whether a finger lift was a still, single-finger tap and whether the hover it
/// leaves behind may be cleared.
/// </summary>
public sealed class MobileHoverReleaseTracker
{
    public const float TapSlop = 30f;
    private readonly HashSet<int> _touches = new();
    private bool _tracking;
    private bool _moved;
    private bool _multi;
    private float _startX;
    private float _startY;

    /// <summary>Changes on every press and reset, so a deferred clear can tell a newer gesture started.</summary>
    public ulong Serial { get; private set; }

    // Godot sends the emulated mouse press before the first finger's own touch
    // press, so no finger is registered yet for a fresh single-finger gesture.
    public void MousePressed(float x, float y)
    {
        _touches.Clear();
        _tracking = true;
        _moved = false;
        _multi = false;
        _startX = x;
        _startY = y;
        Serial++;
    }

    public void TouchPressed(int index)
    {
        _touches.Add(index);
        if (_touches.Count > 1)
            _multi = true;
    }

    public void TouchReleased(int index)
    {
        _touches.Remove(index);
    }

    public void Moved(float x, float y)
    {
        if (!_tracking || _moved)
            return;
        var dx = x - _startX;
        var dy = y - _startY;
        if (dx * dx + dy * dy > TapSlop * TapSlop)
            _moved = true;
    }

    /// <summary>Emulated left release: true when the gesture was a still single-finger tap.</summary>
    public bool MouseReleased(bool canceled)
    {
        var tap = _tracking && !_moved && !_multi && !canceled;
        _tracking = false;
        return tap;
    }

    public void Reset()
    {
        _touches.Clear();
        _tracking = _moved = _multi = false;
        Serial++;
    }
}

public static class MobileHoverReleasePolicy
{
    // Hover is the state these controls show, so clearing it would undo the tap:
    // card holders (tap-pinned hand card, reward/grid selection previews), the map
    // legend's node highlight and shop slots (pending purchase preview).
    private static readonly HashSet<string> HoverOwnedTypeNames = new(StringComparer.Ordinal)
    {
        "NMapLegendItem",
        "NMerchantSlot",
        "NMerchantCard",
        "NMerchantRelic",
        "NMerchantPotion",
        "NMerchantCardRemoval",
    };

    public static bool IsHoverOwnedType(string typeName)
    {
        return typeName != null
            && (typeName.EndsWith("CardHolder", StringComparison.Ordinal) || HoverOwnedTypeNames.Contains(typeName));
    }

    /// <param name="hoveredChain">Type names of the hovered control and its ancestors; null when nothing is hovered.</param>
    public static bool ShouldClear(bool touchControls, bool hoverTooltips, bool pointerDown, bool sameGesture,
        bool targetingOrCardPlay, bool stickyTooltip, IEnumerable<string> hoveredChain)
    {
        // Immediate tooltip mode shows tips on hover: the leftover hover is how a tap reads them.
        if (!touchControls || hoverTooltips || pointerDown || !sameGesture || targetingOrCardPlay || stickyTooltip || hoveredChain == null)
            return false;
        var any = false;
        foreach (var name in hoveredChain)
        {
            any = true;
            if (IsHoverOwnedType(name))
                return false;
        }
        return any;
    }
}
