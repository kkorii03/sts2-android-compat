using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace STS2Mobile.Patches;

/// <summary>
/// STS1-mobile touch targets for small info icons (top bar, potion slots, relic
/// row, creature powers/intents): each gets an invisible, art-free child Control
/// ("pad") that grows its hit area to at least 7% of the screen height. The pad
/// uses MOUSE_FILTER_PASS, so hover and presses reach the icon itself exactly as
/// on the icon's own rect; the icon's own children are still hit-tested first.
/// Growth stops halfway to a padded neighbour so no icon steals another's area.
/// Creature power/intent pads switch off while a card is selected or targeting
/// is running, so they never cover an enemy the player is aiming at.
/// </summary>
public static class MobileTouchTargetPatches
{
    private const float MinTouchScreenFraction = 0.07f;
    private const string PadName = "MobileTouchPad";

    // Full type name -> whether it sits on a creature (switched off while aiming).
    private static readonly (string TypeName, bool CreatureIcon)[] Targets =
    {
        ("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarButton", false),
        ("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarDeckButton", false),
        ("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarHp", false),
        ("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarGold", false),
        ("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarRoomIcon", false),
        ("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarFloorIcon", false),
        ("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarBossIcon", false),
        ("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarModifier", false),
        ("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortraitTip", false),
        ("MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder", false),
        ("MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventoryHolder", false),
        ("MegaCrit.Sts2.Core.Nodes.Combat.NPower", true),
        ("MegaCrit.Sts2.Core.Nodes.Combat.NIntent", true),
    };

    private static readonly HashSet<Type> CreatureIconTypes = new();
    private static readonly ConditionalWeakTable<Control, Control> Pads = new();
    private static readonly List<PadEntry> Entries = new();
    private static bool _frameHooked;
    private static int _mobile;

    public static void Apply(Harmony harmony)
    {
        var assembly = typeof(NGame).Assembly;
        var postfix = PatchHelper.Method(typeof(MobileTouchTargetPatches), nameof(ReadyPostfix));
        foreach (var (typeName, creatureIcon) in Targets)
        {
            var type = assembly.GetType(typeName);
            if (type == null)
            {
                PatchHelper.Log($"SKIPPED mobile touch target {typeName}: type not found");
                continue;
            }
            if (creatureIcon)
                CreatureIconTypes.Add(type);
            PatchHelper.Patch(harmony, type, "_Ready", postfix: postfix);
        }
    }

    public static void ReadyPostfix(Control __instance)
    {
        try
        {
            if (!IsMobile() || __instance == null || __instance is Container || Pads.TryGetValue(__instance, out _))
                return;
            var pad = new Control
            {
                Name = PadName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                FocusMode = Control.FocusModeEnum.None,
            };
            __instance.AddChild(pad);
            // First child: the icon's own children keep priority inside its art.
            __instance.MoveChild(pad, 0);
            Pads.Add(__instance, pad);
            Entries.Add(new PadEntry(__instance, pad, IsCreatureIcon(__instance)));
            __instance.Connect(Control.SignalName.Resized, Callable.From(() => Layout(__instance, pad)));
            __instance.Connect(CanvasItem.SignalName.ItemRectChanged, Callable.From(() => Layout(__instance, pad)));
            Layout(__instance, pad);
            HookFrame();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile touch target pad failed for {__instance?.GetType().Name}: {exception.Message}");
        }
    }

    private static bool IsCreatureIcon(Control owner)
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
        {
            if (CreatureIconTypes.Contains(type))
                return true;
        }
        return false;
    }

    private static void HookFrame()
    {
        if (_frameHooked)
            return;
        var tree = NGame.Instance?.GetTree();
        if (tree == null)
            return;
        tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(SyncFilters));
        _frameHooked = true;
    }

    // Grow to the minimum size around the icon's own rect, capped at half the gap
    // to each padded sibling so the nearer icon always wins its own side.
    private static void Layout(Control owner, Control pad)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(owner) || !GodotObject.IsInstanceValid(pad) || !owner.IsInsideTree())
                return;
            var viewport = owner.GetViewport();
            var screenHeight = viewport?.GetVisibleRect().Size.Y ?? 0f;
            var scale = owner.GetGlobalTransformWithCanvas().Scale;
            var scaleX = Mathf.Abs(scale.X);
            var scaleY = Mathf.Abs(scale.Y);
            var size = owner.Size;
            if (screenHeight <= 0f || scaleX < 0.0001f || scaleY < 0.0001f || size.X <= 0f || size.Y <= 0f)
            {
                pad.Visible = false;
                return;
            }
            var minimum = screenHeight * MinTouchScreenFraction;
            var growX = Mathf.Max(0f, minimum / scaleX - size.X) * 0.5f;
            var growY = Mathf.Max(0f, minimum / scaleY - size.Y) * 0.5f;
            float left = growX, right = growX, top = growY, bottom = growY;
            CapToNeighbours(owner, ref left, ref right, ref top, ref bottom);
            if (left <= 0f && right <= 0f && top <= 0f && bottom <= 0f)
            {
                pad.Visible = false;
                return;
            }
            pad.Position = new Vector2(-left, -top);
            pad.Size = new Vector2(size.X + left + right, size.Y + top + bottom);
            pad.Visible = true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile touch target layout failed: {exception.Message}");
        }
    }

    private static void CapToNeighbours(Control owner, ref float left, ref float right, ref float top, ref float bottom)
    {
        if (owner.GetParent() is not Control parent)
            return;
        var ownerScale = owner.Scale;
        var sx = Mathf.Abs(ownerScale.X) < 0.0001f ? 1f : Mathf.Abs(ownerScale.X);
        var sy = Mathf.Abs(ownerScale.Y) < 0.0001f ? 1f : Mathf.Abs(ownerScale.Y);
        var self = new Rect2(owner.Position, owner.Size * new Vector2(sx, sy));
        foreach (var child in parent.GetChildren())
        {
            if (ReferenceEquals(child, owner) || child is not Control sibling || !sibling.Visible || !Pads.TryGetValue(sibling, out _))
                continue;
            var other = new Rect2(sibling.Position, sibling.Size * new Vector2(Mathf.Abs(sibling.Scale.X), Mathf.Abs(sibling.Scale.Y)));
            var overlapsY = other.Position.Y < self.End.Y + bottom * sy && other.End.Y > self.Position.Y - top * sy;
            var overlapsX = other.Position.X < self.End.X + right * sx && other.End.X > self.Position.X - left * sx;
            if (overlapsY)
            {
                if (other.Position.X >= self.End.X)
                    right = Mathf.Min(right, (other.Position.X - self.End.X) * 0.5f / sx);
                else if (other.End.X <= self.Position.X)
                    left = Mathf.Min(left, (self.Position.X - other.End.X) * 0.5f / sx);
            }
            if (overlapsX)
            {
                if (other.Position.Y >= self.End.Y)
                    bottom = Mathf.Min(bottom, (other.Position.Y - self.End.Y) * 0.5f / sy);
                else if (other.End.Y <= self.Position.Y)
                    top = Mathf.Min(top, (self.Position.Y - other.End.Y) * 0.5f / sy);
            }
        }
        left = Mathf.Max(0f, left);
        right = Mathf.Max(0f, right);
        top = Mathf.Max(0f, top);
        bottom = Mathf.Max(0f, bottom);
    }

    // The pad mirrors the icon: no hit area for mouse users, for an icon that
    // ignores the mouse itself, or (creature icons) while the player is aiming.
    private static void SyncFilters()
    {
        try
        {
            var touch = MobileInputPolicyPatches.UseTouchControls;
            var aiming = touch && IsAiming();
            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                var entry = Entries[i];
                if (!entry.TryGet(out var owner, out var pad))
                {
                    Entries.RemoveAt(i);
                    continue;
                }
                var wanted = !touch || owner.MouseFilter == Control.MouseFilterEnum.Ignore || (entry.CreatureIcon && aiming)
                    ? Control.MouseFilterEnum.Ignore
                    : Control.MouseFilterEnum.Pass;
                if (entry.LastFilter != wanted)
                {
                    pad.MouseFilter = wanted;
                    entry.LastFilter = wanted;
                }
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile touch target sync failed: {exception.Message}");
        }
    }

    private static bool IsAiming()
    {
        try
        {
            // Aiming (card or potion) only exists in combat; skip NTargetManager.Instance
            // (it throws outside a run) on every other frame.
            var hand = NPlayerHand.Instance;
            if (hand == null || !GodotObject.IsInstanceValid(hand))
                return false;
            if (hand.InCardPlay || MobileTapPreviewPatches.GetPinnedHolder(hand) != null)
                return true;
            var targeting = NTargetManager.Instance;
            return targeting != null && targeting.IsInSelection;
        }
        catch
        {
            return false; // Outside a run.
        }
    }

    private static bool IsMobile()
    {
        if (_mobile == 0)
            _mobile = OS.HasFeature("mobile") || OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        return _mobile == 1;
    }

    private sealed class PadEntry
    {
        private readonly WeakReference<Control> _owner;
        private readonly WeakReference<Control> _pad;
        public readonly bool CreatureIcon;
        // Pads start ignored; the first frame sync turns them on.
        public Control.MouseFilterEnum LastFilter = Control.MouseFilterEnum.Ignore;

        public PadEntry(Control owner, Control pad, bool creatureIcon)
        {
            _owner = new WeakReference<Control>(owner);
            _pad = new WeakReference<Control>(pad);
            CreatureIcon = creatureIcon;
        }

        public bool TryGet(out Control owner, out Control pad)
        {
            pad = null;
            return _owner.TryGetTarget(out owner) && GodotObject.IsInstanceValid(owner)
                && _pad.TryGetTarget(out pad) && GodotObject.IsInstanceValid(pad);
        }
    }
}
