using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using STS2Mobile.Android;
using STS2Mobile.Patches;

// Screen-context gate (map/modal over combat), hand-scrub finger ownership and
// hit testing, and the legacy (tap guard off) play-zone press.
internal static class HandContextTests
{
    private sealed class OtherScreen : IScreenContext { }

    public static void Run()
    {
        CombatContextGate();
        ScrubFingerOwnershipAndHitTest();
        LegacyPlayZoneTapIgnoresSecondFinger();
        NewHand();
    }

    private static void CombatContextGate()
    {
        var context = ActiveScreenContext.Instance;
        Require(MobileTapPreviewPatches.IsCombatInputActive(), "combat room current: combat input active");
        var hand = NewHand();
        var holder = AddHolder(hand, new Vector2(0, 900));
        hand.OnHolderPressed(holder);
        Require(MobileTapPreviewPatches.IsPinned(holder), "fixture pins the card");
        context.Current = new OtherScreen();
        try
        {
            Require(!MobileTapPreviewPatches.IsCombatInputActive(), "another screen context (map) current: combat input inactive");
            TouchTap(new Vector2(300, 100));
            NGame.Instance._Input(new InputEventScreenDrag { Position = new Vector2(0, -80) });
            Require(holder.CardModel.ManualPlays == 0 && hand.CardPlays == 0, "play-zone tap and direct drag under the map never play the hidden pinned card");
        }
        finally
        {
            context.Current = null;
        }
        TouchTap(new Vector2(300, 100));
        Require(holder.CardModel.ManualPlays == 1, "back in combat the guarded play-zone tap plays again");

        hand.OnHolderPressed(holder);
        NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = true, Position = new Vector2(300, 100) });
        context.Current = new OtherScreen();
        try
        {
            Time.Msec += 50;
            NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = false, Position = new Vector2(300, 100) });
            Require(holder.CardModel.ManualPlays == 1, "a guarded tap whose release lands after the map opened never plays");
        }
        finally
        {
            context.Current = null;
        }
    }

    private static void ScrubFingerOwnershipAndHitTest()
    {
        var hand = NewHand();
        var flat = AddHolder(hand, new Vector2(0, 800));
        // Fan-rotated a quarter turn: covers x 200..300, outside its axis-aligned global rect (x 300..400).
        var rotated = AddHolder(hand, new Vector2(300, 800), MathF.PI / 2);

        var context = ActiveScreenContext.Instance;
        context.Current = new OtherScreen();
        try
        {
            Touch(0, true, new Vector2(50, 850));
            Drag(0, new Vector2(250, 850));
            Touch(0, false, new Vector2(250, 850));
            Require(MobileTapPreviewPatches.GetPinnedHolder(hand) == null, "no scrub under the map");
        }
        finally
        {
            context.Current = null;
        }

        Touch(0, true, new Vector2(500, 100));
        Touch(1, true, new Vector2(250, 850));
        Drag(1, new Vector2(50, 850));
        Require(MobileTapPreviewPatches.GetPinnedHolder(hand) == null, "a finger pressed while another is down does not scrub");
        Touch(1, false, new Vector2(50, 850));
        Touch(0, false, new Vector2(500, 100));

        Touch(0, true, new Vector2(50, 850));
        Drag(0, new Vector2(75, 850));
        Require(MobileTapPreviewPatches.GetPinnedHolder(hand) == null, "25 px of tap jitter does not start a scrub");
        Drag(0, new Vector2(250, 850));
        Require(ReferenceEquals(MobileTapPreviewPatches.GetPinnedHolder(hand), rotated), "scrub picks a rotated hitbox through its inverse canvas transform");
        Drag(0, new Vector2(50, 850));
        Require(ReferenceEquals(MobileTapPreviewPatches.GetPinnedHolder(hand), flat), "scrub moves back to the flat card");
        Touch(0, false, new Vector2(50, 850));
        Require(hand.CardPlays == 0, "sideways scrubbing never plays");
    }

    private static void LegacyPlayZoneTapIgnoresSecondFinger()
    {
        AndroidSettingsBridge.TapGuardEnabled = false;
        try
        {
            var hand = NewHand();
            var holder = AddHolder(hand, new Vector2(0, 900));
            hand.OnHolderPressed(holder);
            NGame.Instance._Input(new InputEventScreenTouch { Index = 1, Pressed = true, Position = new Vector2(300, 100) });
            Require(holder.CardModel.ManualPlays == 0 && MobileTapPreviewPatches.IsPinned(holder), "legacy play-zone: a second finger's touch press never plays");
            NGame.Instance._Input(new InputEventScreenTouch { Index = 1, Pressed = false, Position = new Vector2(300, 100) });
            NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = true, Position = new Vector2(300, 100) });
            Require(holder.CardModel.ManualPlays == 1, "legacy play-zone: the emulated mouse press still plays");
            NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = false, Position = new Vector2(300, 100) });
        }
        finally
        {
            AndroidSettingsBridge.TapGuardEnabled = true;
        }
    }

    private static NPlayerHand NewHand()
    {
        MobileTapPreviewPatches.ResetForInputModeChange();
        MobileHandScrubPatches.Reset();
        Viewport.Shared.MousePosition = Vector2.Zero;
        return NPlayerHand.Instance = new NPlayerHand();
    }

    private static NHandCardHolder AddHolder(NPlayerHand hand, Vector2 position, float rotation = 0f)
    {
        var holder = new NHandCardHolder();
        holder.Hitbox.Position = position;
        holder.Hitbox.Rotation = rotation;
        hand.AddChild(holder);
        hand.ActiveHolders.Add(holder);
        holder._Ready();
        return holder;
    }

    private static void Touch(int index, bool pressed, Vector2 position)
    {
        NGame.Instance._Input(new InputEventScreenTouch { Index = index, Pressed = pressed, Position = position });
    }

    private static void Drag(int index, Vector2 position)
    {
        NGame.Instance._Input(new InputEventScreenDrag { Index = index, Position = position });
    }

    private static void TouchTap(Vector2 position)
    {
        NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = true, Position = position });
        Time.Msec += 50;
        NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = false, Position = position });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
