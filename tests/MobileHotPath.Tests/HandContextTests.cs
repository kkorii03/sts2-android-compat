using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
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
        CancelTapPassesThroughAndDeadBand();
        PutDownReleaseKeepsScrubAlive();
        PinnedCardOwnHoverExitKeepsLift();
        ScreenChangeDropsArmedGestures();
        HookSurvivesInstanceIdCollision();
        NewHand();
    }

    // A tap below the play zone off the cards (End Turn, piles) puts the pinned
    // card down without consuming the press; a tap just above the resting hand
    // does nothing; the play zone ends above that band.
    private static void CancelTapPassesThroughAndDeadBand()
    {
        var hand = NewHand();
        var pinned = AddHolder(hand, new Vector2(0, 760));
        AddHolder(hand, new Vector2(200, 760)); // resting hand top 760: dead band 720..760 at 1000 px
        hand.OnHolderPressed(pinned);
        var handled = Viewport.Shared.HandledInputs;
        FingerTap(new Vector2(900, 900));
        Require(!MobileTapPreviewPatches.IsPinned(pinned) && Viewport.Shared.HandledInputs == handled && pinned.CardModel.ManualPlays == 0,
            "a tap on End Turn/piles puts the pinned card down and the press still reaches the button");

        hand.OnHolderPressed(pinned);
        FingerTap(new Vector2(500, 740));
        Require(MobileTapPreviewPatches.IsPinned(pinned) && pinned.CardModel.ManualPlays == 0,
            "a tap in the dead band above the hand neither plays nor puts down the pinned card");
        FingerTap(new Vector2(500, 700));
        Require(pinned.CardModel.ManualPlays == 1, "a tap above the dead band still plays the pinned card");
    }

    // The put-down re-tap's touch release (arriving before the emulated mouse
    // release) used to be consumed and skip the scrub's finger bookkeeping.
    private static void PutDownReleaseKeepsScrubAlive()
    {
        var hand = NewHand();
        var a = AddHolder(hand, new Vector2(0, 800));
        var b = AddHolder(hand, new Vector2(200, 800));
        hand.OnHolderPressed(a);
        Touch(0, true, new Vector2(50, 850));
        hand.OnHolderPressed(a);
        Touch(0, false, new Vector2(50, 850));
        NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = false, Position = new Vector2(50, 850) });
        Require(!MobileTapPreviewPatches.IsPinned(a), "re-tap put-down completes on the emulated release");
        Touch(0, true, new Vector2(50, 850));
        Drag(0, new Vector2(250, 850));
        Require(ReferenceEquals(MobileTapPreviewPatches.GetPinnedHolder(hand), b), "after a re-tap put-down the next finger still scrubs the hand");
        Touch(0, false, new Vector2(250, 850));
    }

    private static void PinnedCardOwnHoverExitKeepsLift()
    {
        var hand = NewHand();
        var a = AddHolder(hand, new Vector2(0, 800));
        hand.OnHolderPressed(a);
        hand.OnHolderUnfocused(a);
        Require(ReferenceEquals(hand.FocusedHolder, a) && MobileTapPreviewPatches.IsPinned(a),
            "vanilla hover exit of the pinned card itself cannot drop its lift");
    }

    private static void ScreenChangeDropsArmedGestures()
    {
        var hand = NewHand();
        var a = AddHolder(hand, new Vector2(0, 900));
        hand.OnHolderPressed(a);
        hand.OnHolderPressed(a); // re-tap: put-down and direct drag armed
        var context = ActiveScreenContext.Instance;
        context.Current = new OtherScreen();
        try
        {
            Touch(0, true, new Vector2(500, 100));
            Touch(0, false, new Vector2(500, 100));
        }
        finally
        {
            context.Current = null;
        }
        var handled = Viewport.Shared.HandledInputs;
        NGame.Instance._Input(new InputEventScreenDrag { Position = new Vector2(0, -80) });
        NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = false, Position = new Vector2(500, 100) });
        Require(hand.CardPlays == 0 && MobileTapPreviewPatches.IsPinned(a) && Viewport.Shared.HandledInputs == handled,
            "a gesture armed before the map opened cannot play on a later drag or eat a later release");
    }

    private static void HookSurvivesInstanceIdCollision()
    {
        var hand = NewHand();
        var first = AddHolder(hand, new Vector2(0, 900));
        var before = GodotObject.NextId;
        var perHolder = new NHandCardHolder().GetInstanceId() - before;
        GodotObject.NextId = first.GetInstanceId() + (1UL << 32) - perHolder;
        var second = AddHolder(hand, new Vector2(200, 900));
        Require(second.GetInstanceId() != first.GetInstanceId() && (int)second.GetInstanceId() == (int)first.GetInstanceId(),
            "fixture: the two holders' ids collide in their low 32 bits");
        hand.OnHolderPressed(second);
        hand.OnHolderPressed(second);
        second.Hitbox.Emit(NClickableControl.SignalName.MouseReleased, new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left });
        Require(!MobileTapPreviewPatches.IsPinned(second), "a holder whose id collides in the low 32 bits still gets its release hook");
    }

    // First finger: raw touch and its emulated mouse, the touch release first.
    private static void FingerTap(Vector2 position)
    {
        Touch(0, true, position);
        NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = true, Position = position });
        Time.Msec += 50;
        Touch(0, false, position);
        NGame.Instance._Input(new InputEventMouseButton { Device = -1, ButtonIndex = MouseButton.Left, Pressed = false, Position = position });
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
