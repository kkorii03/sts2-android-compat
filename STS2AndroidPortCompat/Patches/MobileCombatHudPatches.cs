using System;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

// Draws the End Turn button and the energy orb larger on phones (STS1 mobile feel), scaled
// about their own centers so they stay where vanilla puts them. Vanilla only tweens their
// Position, never their own Scale, so the scale survives show/hide animations, and the
// touch area grows with the picture. Idea: the iOS launcher's portrait HUD (End Turn 1.42x).
public static class MobileCombatHudPatches
{
    private sealed class Original
    {
        internal readonly Vector2 Scale;
        internal readonly Vector2 Pivot;
        internal Original(Control control)
        {
            Scale = control.Scale;
            Pivot = control.PivotOffset;
        }
    }

    private static readonly ConditionalWeakTable<Control, Original> Originals = new();

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NCombatUi), "Activate", postfix: PatchHelper.Method(typeof(MobileCombatHudPatches), nameof(ActivatePostfix)));
    }

    public static void ActivatePostfix(NCombatUi __instance)
    {
        try
        {
            ApplyTo(__instance);
            // The energy orb is added with AddChildSafely and may get its size a frame later.
            Callable.From(() => ApplyTo(__instance)).CallDeferred();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile combat button scale failed: {exception.Message}");
        }
    }

    // Re-applies the current setting to the live combat screen (in-game settings switch).
    public static void ApplyToCurrentCombat()
    {
        try
        {
            ApplyTo(NCombatRoom.Instance?.Ui);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Mobile combat button rescale failed: {exception.Message}");
        }
    }

    private static void ApplyTo(NCombatUi ui)
    {
        if (ui == null || !GodotObject.IsInstanceValid(ui))
            return;
        var factor = GetFactor();
        ScaleAboutCenter(ui.EndTurnButton, factor, new Vector2(110f, 45f));
        var container = ui.EnergyCounterContainer;
        if (container == null || !GodotObject.IsInstanceValid(container))
            return;
        foreach (var child in container.GetChildren())
        {
            if (child is NEnergyCounter counter)
                ScaleAboutCenter(counter, factor, new Vector2(64f, 64f));
        }
    }

    private static void ScaleAboutCenter(Control control, float factor, Vector2 fallbackPivot)
    {
        if (control == null || !GodotObject.IsInstanceValid(control))
            return;
        var original = Originals.GetValue(control, static node => new Original(node));
        if (factor <= 1f)
        {
            control.Scale = original.Scale;
            control.PivotOffset = original.Pivot;
            return;
        }
        control.PivotOffset = control.Size.X > 1f && control.Size.Y > 1f ? control.Size * 0.5f : fallbackPivot;
        control.Scale = original.Scale * factor;
    }

    private static float GetFactor()
    {
        if (!OS.HasFeature("mobile"))
            return 1f;
        return MobileCombatSizing.Factor(
            AndroidSettingsBridge.GetInt(MobileCombatSizing.CombatButtonScaleKey, MobileCombatSizing.CombatButtonScaleDefault),
            MobileCombatSizing.CombatButtonScaleMin, MobileCombatSizing.CombatButtonScaleMax);
    }
}
