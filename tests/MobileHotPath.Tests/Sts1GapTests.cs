using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using STS2Mobile.Android;
using STS2Mobile.Patches;

// Synthetic shapes for the relic choice screens (see decomp NChooseARelicSelection,
// NEventOptionButton, AncientEventModel).
namespace MegaCrit.Sts2.Core.Models
{
    public class RelicModel { }
    public class EventModel { }
    public class AncientEventModel : EventModel { }
}
namespace MegaCrit.Sts2.Core.Events
{
    public class EventOption
    {
        public RelicModel Relic { get; set; }
        public bool IsLocked { get; set; }
    }
}
namespace MegaCrit.Sts2.Core.Nodes.Relics
{
    public class NRelicBasicHolder : MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton { }
}
namespace MegaCrit.Sts2.Core.Nodes.Events
{
    public class NEventOptionButton : MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton
    {
        public EventModel Event { get; set; }
        public EventOption Option { get; set; }
        public int Picks;
        public void Tap() => OnRelease();
        [MethodImpl(MethodImplOptions.NoInlining)] protected virtual void OnRelease() { Picks++; }
    }
}
namespace MegaCrit.Sts2.Core.Nodes.Rooms
{
    public class NEventRoom : Control { public static NEventRoom Instance { get; set; } }
}
namespace MegaCrit.Sts2.Core.Nodes.Screens
{
    public class NChooseARelicSelection : Control
    {
        private readonly TaskCompletionSource<NRelicBasicHolder> _completionSource = new();
        private bool _screenComplete;
        public Task<NRelicBasicHolder> Completion => _completionSource.Task;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SelectHolder(NRelicBasicHolder relicHolder) { _screenComplete = true; _completionSource.TrySetResult(relicHolder); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnSkipButtonReleased(NButton _) { _screenComplete = true; _completionSource.TrySetResult(null); }
        [MethodImpl(MethodImplOptions.NoInlining)] public void AfterOverlayClosed() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void AfterOverlayHidden() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void AfterOverlayShown() { }
    }
}

internal static class Sts1GapTests
{
    private const string ConfirmName = "AndroidRelicChoiceConfirm";

    public static void Run()
    {
        OS.MobileFeature = true;
        MobileInputPolicyPatches.TouchControls = true;
        IconTapTooltip();
        RelicChoiceScreen();
        AncientRelicOptions();
    }

    private static void IconTapTooltip()
    {
        var savedHand = NPlayerHand.Instance;
        NPlayerHand.Instance = null;
        NTargetManager.Instance.IsInSelection = false;
        AndroidSettingsBridge.Mode = "long_press";
        MobileTooltipPatches.RefreshModeFromSettings();
        Time.Msec = 60000;

        var creature = new NCreature();
        creature.AddChild(creature.Hitbox);
        var hitbox = creature.Hitbox;
        var tip = NHoverTipSet.CreateAndShow(hitbox);
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = true, Position = new Vector2(10, 10) });
        Require(!tip.Visible, "icon tooltip stays hidden while the finger is down");
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = false, Position = new Vector2(10, 10) });
        Require(tip.Visible, "a tap on a creature/power icon keeps its tooltip after release");
        hitbox.Emit(Control.SignalName.GuiInput, new InputEventScreenTouch { Pressed = false });
        Require(tip.Visible, "the owner's copy of the release does not hide the tapped tooltip");
        tip._Process(0.1);
        Require(tip.Visible, "the tapped tooltip survives frame updates");
        NHoverTipSet.Remove(hitbox);
        tip = NHoverTipSet.CreateAndShow(hitbox);
        tip._Process(0.1);
        Require(tip.Visible, "a game-side rebuild of the tapped icon's tip stays visible");
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = true, Position = new Vector2(500, 500) });
        Require(!tip.Visible, "the next touch anywhere dismisses the tapped tooltip");
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = false, Position = new Vector2(500, 500) });
        Require(!tip.Visible, "a tap on empty space pins nothing");
        NHoverTipSet.Clear();

        var gold = new NTopBarGold();
        tip = NHoverTipSet.CreateAndShow(gold);
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = true });
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = false });
        Require(tip.Visible, "a tap on a top-bar stat keeps its tooltip");
        AndroidSettingsBridge.Mode = "immediate";
        MobileTooltipPatches.RefreshModeFromSettings();
        AndroidSettingsBridge.Mode = "long_press";
        MobileTooltipPatches.RefreshModeFromSettings();
        Require(!tip.Valid || !tip.Visible, "a mode switch drops the pinned tooltip");
        NHoverTipSet.Clear();

        var card = new Control();
        tip = NHoverTipSet.CreateAndShow(card);
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = true });
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = false });
        Require(!tip.Visible, "cards and other non-icon owners keep long-press-only tooltips");
        NHoverTipSet.Clear();

        AndroidSettingsBridge.IconTapTooltipEnabled = false;
        tip = NHoverTipSet.CreateAndShow(hitbox);
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = true });
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = false });
        Require(!tip.Visible, "setting off restores the long-press-only behaviour for icons");
        AndroidSettingsBridge.IconTapTooltipEnabled = true;
        NHoverTipSet.Clear();

        NPlayerHand.Instance = new NPlayerHand { InCardPlay = true };
        tip = NHoverTipSet.CreateAndShow(hitbox);
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = true });
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = false });
        Require(!tip.Visible, "no pinned creature tooltip while a card is being played");
        NPlayerHand.Instance = null;
        NHoverTipSet.Clear();

        var option = new Control();
        tip = NHoverTipSet.CreateAndShow(option);
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = true });
        NGame.Instance._Input(new InputEventScreenTouch { Pressed = false });
        Require(!tip.Visible, "non-icon tooltip hidden after release");
        MobileTooltipPatches.PinTooltip(option);
        Require(tip.Visible, "screens can pin the tooltip of the item they selected");
        MobileTooltipPatches.DismissTooltip(option);
        Require(!tip.Visible, "deselecting dismisses the pinned tooltip");
        NHoverTipSet.Clear();

        AndroidSettingsBridge.Mode = "immediate";
        MobileTooltipPatches.RefreshModeFromSettings();
        NPlayerHand.Instance = savedHand;
    }

    private static void RelicChoiceScreen()
    {
        var screen = NewRelicScreen(out var a, out var b);
        screen.SelectHolder(a);
        var confirm = screen.GetNodeOrNull<NConfirmButton>(ConfirmName);
        Require(!screen.Completion.IsCompleted, "a relic tap selects instead of taking it");
        Require(confirm != null && confirm.Enabled, "selecting a relic arms the stock confirm button");
        Require(b.Modulate.R < 1f && a.Modulate.R == 1f, "the other relics are dimmed");
        screen.SelectHolder(a);
        Require(!confirm.Enabled && b.Modulate.R == 1f && !screen.Completion.IsCompleted, "tapping the selected relic again deselects");
        screen.SelectHolder(b);
        screen.SelectHolder(a);
        Require(confirm.Enabled && a.Modulate.R == 1f && b.Modulate.R < 1f, "tapping another relic moves the selection");
        confirm.Release();
        Require(screen.Completion.IsCompleted && ReferenceEquals(screen.Completion.Result, a), "confirm takes the selected relic");
        Require(!confirm.Enabled && b.Modulate.R == 1f, "confirm disarms and restores the row");

        screen = NewRelicScreen(out a, out _);
        screen.SelectHolder(a);
        confirm = screen.GetNodeOrNull<NConfirmButton>(ConfirmName);
        screen.OnSkipButtonReleased(null);
        Require(screen.Completion.IsCompleted && screen.Completion.Result == null && !confirm.Enabled, "skip still works with a pending selection");

        MobileInputPolicyPatches.TouchControls = false;
        screen = NewRelicScreen(out a, out _);
        screen.SelectHolder(a);
        Require(screen.Completion.IsCompleted, "mouse/controller keeps the one-click relic pick");
        MobileInputPolicyPatches.TouchControls = true;

        AndroidSettingsBridge.RelicChoiceConfirmationEnabled = false;
        screen = NewRelicScreen(out a, out _);
        screen.SelectHolder(a);
        Require(screen.Completion.IsCompleted, "setting off keeps the one-tap relic pick");
        AndroidSettingsBridge.RelicChoiceConfirmationEnabled = true;
    }

    private static void AncientRelicOptions()
    {
        var room = new NEventRoom();
        NEventRoom.Instance = room;
        var options = new Control();
        room.AddChild(options);
        var first = NewOption(new AncientEventModel(), relic: true);
        var second = NewOption(new AncientEventModel(), relic: true);
        var leave = NewOption(new AncientEventModel(), relic: false);
        options.AddChild(first);
        options.AddChild(second);
        options.AddChild(leave);

        first.Tap();
        var confirm = room.GetNodeOrNull<NConfirmButton>(ConfirmName);
        Require(first.Picks == 0 && confirm != null && confirm.Enabled, "an ancient relic option waits for confirm");
        Require(second.Modulate.R < 1f, "the other ancient options are dimmed");
        first.Tap();
        Require(first.Picks == 0 && !confirm.Enabled && second.Modulate.R == 1f, "re-tapping the ancient option deselects it");
        second.Tap();
        confirm.Release();
        Require(second.Picks == 1 && first.Picks == 0 && !confirm.Enabled, "confirm picks the selected ancient option once");
        leave.Tap();
        Require(leave.Picks == 1, "ancient options without a relic stay one-tap");
        var normal = NewOption(new EventModel(), relic: true);
        options.AddChild(normal);
        normal.Tap();
        Require(normal.Picks == 1, "ordinary event options stay one-tap");

        first.Tap();
        Require(confirm.Enabled, "selection armed before the options are rebuilt");
        first.Free();
        Require(!confirm.Enabled, "a freed selection disarms confirm");
        confirm.Release();
        Require(first.Picks == 0, "confirm never fires for a freed option");
        NEventRoom.Instance = null;
    }

    private static NChooseARelicSelection NewRelicScreen(out NRelicBasicHolder a, out NRelicBasicHolder b)
    {
        var screen = new NChooseARelicSelection();
        var row = new Control();
        screen.AddChild(row);
        a = new NRelicBasicHolder();
        b = new NRelicBasicHolder();
        row.AddChild(a);
        row.AddChild(b);
        return screen;
    }

    private static NEventOptionButton NewOption(EventModel eventModel, bool relic)
    {
        return new NEventOptionButton
        {
            Event = eventModel,
            Option = new EventOption { Relic = relic ? new RelicModel() : null },
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

internal sealed class NTopBarGold : Control { }
