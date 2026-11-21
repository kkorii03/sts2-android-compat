using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// STS1-mobile boss relic flow for irreversible relic picks: tap a relic =
/// select it and keep its tooltip up, tap the stock Confirm button = take it,
/// tap the selected relic again = deselect. Covers the "Choose a Relic"
/// overlay (NChooseARelicSelection) and the Ancient event relic options, which
/// are STS2's boss-relic equivalent. Touch only; mouse/controller keep the
/// original one-click pick.
/// </summary>
public static class MobileRelicChoiceConfirmationPatches
{
    internal const string SettingKey = "mobile_relic_choice_confirmation";
    private const string ConfirmButtonName = "AndroidRelicChoiceConfirm";
    private static readonly Color DimmedModulate = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly MethodInfo RelicSelectHolderMethod = AccessTools.Method(typeof(NChooseARelicSelection), "SelectHolder");
    private static readonly MethodInfo EventOptionReleaseMethod = AccessTools.Method(typeof(NEventOptionButton), "OnRelease");
    private static readonly ConditionalWeakTable<Node, ChoiceState> States = new();
    private static readonly ConditionalWeakTable<Node, object> ExitWatched = new();
    private static bool _bypass;

    public static void Apply(Harmony harmony)
    {
        var type = typeof(NChooseARelicSelection);
        var self = typeof(MobileRelicChoiceConfirmationPatches);
        PatchHelper.Patch(harmony, type, "SelectHolder", prefix: PatchHelper.Method(self, nameof(RelicSelectHolderPrefix)));
        PatchHelper.Patch(harmony, type, "OnSkipButtonReleased", prefix: PatchHelper.Method(self, nameof(ClearScreenPrefix)));
        PatchHelper.Patch(harmony, type, "AfterOverlayClosed", prefix: PatchHelper.Method(self, nameof(ClearScreenPrefix)));
        PatchHelper.Patch(harmony, type, "AfterOverlayHidden", postfix: PatchHelper.Method(self, nameof(OverlayHiddenPostfix)));
        PatchHelper.Patch(harmony, type, "AfterOverlayShown", postfix: PatchHelper.Method(self, nameof(OverlayShownPostfix)));
        PatchHelper.Patch(harmony, typeof(NEventOptionButton), "OnRelease", prefix: PatchHelper.Method(self, nameof(EventOptionReleasePrefix)));
    }

    public static bool RelicSelectHolderPrefix(Control __instance, NRelicBasicHolder __0)
    {
        if (_bypass || !IsEnabled() || __instance == null || __0 == null)
            return true;
        try
        {
            if (GetField(__instance, "_screenComplete") is true)
                return true;
            var state = States.GetOrCreateValue(__instance);
            if (!EnsureConfirmButton(__instance, state))
                return true;
            ToggleSelection(state, __0, __0.GetParent());
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Relic choice confirmation failed; using one-tap pick: {exception.Message}");
            return true;
        }
    }

    public static bool EventOptionReleasePrefix(NEventOptionButton __instance)
    {
        if (_bypass || !IsEnabled() || __instance == null)
            return true;
        try
        {
            var option = __instance.Option;
            if (__instance.Event is not AncientEventModel || option == null || option.Relic == null || option.IsLocked)
                return true;
            var room = NEventRoom.Instance;
            if (room == null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
                return true;
            var state = States.GetOrCreateValue(room);
            if (!EnsureConfirmButton(room, state))
                return true;
            ToggleSelection(state, __instance, __instance.GetParent());
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Ancient relic confirmation failed; using one-tap pick: {exception.Message}");
            return true;
        }
    }

    public static void ClearScreenPrefix(Node __instance)
    {
        var state = GetState(__instance);
        if (state != null)
            ClearSelection(state);
    }

    public static void OverlayHiddenPostfix(Node __instance)
    {
        var state = GetState(__instance);
        if (state != null)
            HideConfirm(state.ConfirmButton);
    }

    public static void OverlayShownPostfix(Node __instance)
    {
        var state = GetState(__instance);
        if (state != null && IsEnabled() && IsUsable(state.Selected))
            ShowConfirm(state.ConfirmButton, state.Selected);
    }

    private static void ToggleSelection(ChoiceState state, NClickableControl item, Node siblingsParent)
    {
        if (ReferenceEquals(state.Selected, item))
        {
            ClearSelection(state);
            MobileTooltipPatches.DismissTooltip(item);
            return;
        }
        RestoreDimmed(state);
        state.Selected = item;
        if (!ExitWatched.TryGetValue(item, out _))
        {
            ExitWatched.Add(item, null);
            // Options can be rebuilt (ancient dialogue, another player) without a
            // confirm; never leave Confirm armed for a freed item.
            item.Connect(Node.SignalName.TreeExiting, Callable.From(() =>
            {
                if (ReferenceEquals(state.Selected, item))
                    ClearSelection(state);
            }));
        }
        if (siblingsParent != null)
        {
            foreach (var child in siblingsParent.GetChildren())
            {
                if (child is not CanvasItem sibling || ReferenceEquals(child, item) || child.GetType() != item.GetType())
                    continue;
                state.Dimmed.Add((new WeakReference<CanvasItem>(sibling), sibling.Modulate));
                sibling.Modulate = DimmedModulate;
            }
        }
        // Tooltip first, so the confirm placement can steer around it.
        MobileTooltipPatches.PinTooltip(item);
        ShowConfirm(state.ConfirmButton, item);
    }

    private static void Confirm(Node host)
    {
        var state = GetState(host);
        var selected = state?.Selected;
        if (state == null || !IsUsable(selected))
        {
            if (state != null)
                ClearSelection(state);
            return;
        }
        ClearSelection(state);
        _bypass = true;
        try
        {
            if (selected is NRelicBasicHolder holder && RelicSelectHolderMethod != null)
                RelicSelectHolderMethod.Invoke(host, new object[] { holder });
            else if (selected is NEventOptionButton option && EventOptionReleaseMethod != null)
                EventOptionReleaseMethod.Invoke(option, null);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Relic choice confirm failed: {exception.InnerException?.Message ?? exception.Message}");
        }
        finally
        {
            _bypass = false;
        }
    }

    private static void ClearSelection(ChoiceState state)
    {
        RestoreDimmed(state);
        state.Selected = null;
        HideConfirm(state.ConfirmButton);
    }

    private static void RestoreDimmed(ChoiceState state)
    {
        foreach (var (weak, modulate) in state.Dimmed)
        {
            if (weak.TryGetTarget(out var item) && GodotObject.IsInstanceValid(item))
                item.Modulate = modulate;
        }
        state.Dimmed.Clear();
    }

    private static bool EnsureConfirmButton(Node host, ChoiceState state)
    {
        if (state.ConfirmButton != null && GodotObject.IsInstanceValid(state.ConfirmButton) && state.ConfirmButton.IsInsideTree())
            return true;
        state.ConfirmButton = null;
        var button = host.GetNodeOrNull<NConfirmButton>(ConfirmButtonName)
            ?? ResourceLoader.Load<PackedScene>("res://scenes/ui/confirm_button.tscn")?.Instantiate<NConfirmButton>();
        if (button == null)
            return false;
        if (button.GetParent() == null)
        {
            button.Name = ConfirmButtonName;
            host.AddChild(button);
            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Confirm(host)));
        }
        HideConfirm(button);
        state.ConfirmButton = button;
        PatchHelper.Log($"Relic choice confirm button ready on {host.GetType().Name}.");
        return true;
    }

    private static void ShowConfirm(NConfirmButton button, Control anchor)
    {
        if (button == null || !GodotObject.IsInstanceValid(button))
            return;
        button.Enable();
        MobileConfirmPlacement.ShowNear(button, anchor);
    }

    private static void HideConfirm(NConfirmButton button)
    {
        if (button == null || !GodotObject.IsInstanceValid(button))
            return;
        button.Disable();
        MobileConfirmPlacement.Hide(button);
    }

    private static bool IsUsable(NClickableControl item)
    {
        return item != null && GodotObject.IsInstanceValid(item) && item.IsInsideTree()
            && item.IsEnabled && item.IsVisibleInTree();
    }

    private static bool IsEnabled()
    {
        return MobileInputPolicyPatches.UseTouchControls && AndroidSettingsBridge.GetBool(SettingKey, true);
    }

    private static ChoiceState GetState(Node host)
    {
        return host != null && States.TryGetValue(host, out var state) ? state : null;
    }

    private static object GetField(object target, string name)
    {
        return target?.GetType().GetField(name, InstanceFlags)?.GetValue(target);
    }

    private sealed class ChoiceState
    {
        public NClickableControl Selected;
        public NConfirmButton ConfirmButton;
        public readonly List<(WeakReference<CanvasItem> Item, Color Modulate)> Dimmed = new();
    }
}
