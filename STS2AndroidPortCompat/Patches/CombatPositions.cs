using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Where the hand cards and the enemies are on screen (window pixels, like CardAdvisorBridge's rects),
/// for the separate app of CombatStateBridge: one poll loop that lives as long as one bridge generation.
/// Draws nothing and sends only when something moved.
/// </summary>
internal sealed class CombatPositions
{
    private const double PollSeconds = 0.2;
    private const int MinMove = 3;

    // No static initialisers here on purpose (see CardAdvisorBridge.Refs).
    private static AccessTools.FieldRef<NHandCardHolder, Vector2> _targetScale;
    private static bool _loggedFailure;

    private readonly int _generation;
    private readonly ICombatState _combat;
    private readonly PlayerCombatState _player;
    private readonly Action _tick;
    private readonly List<NHandCardHolder> _holders = new();
    private readonly StringBuilder _json = new();
    // What is on screen now, and what was sent last: cards first (empty rect = no holder), then enemies.
    private List<object> _keys = new(), _sentKeys = new();
    private List<Rect2I> _rects = new(), _sentRects = new();
    private int _cards, _sentCards;
    private bool _sentCovered;
    private Vector2I _sentWindow;
    private bool _sent;

    private CombatPositions(ICombatState combat, PlayerCombatState player, int generation)
    {
        _combat = combat;
        _player = player;
        _generation = generation;
        _tick = Tick;
    }

    /// The loop of an older generation ends at its next tick, so only one is ever alive.
    public static void Start(ICombatState combat, int generation)
    {
        var player = combat.Players.Count == 1 ? combat.Players[0].PlayerCombatState : null;
        if (player != null)
            new CombatPositions(combat, player, generation).Tick();
    }

    private void Tick()
    {
        try
        {
            if (_generation != CombatStateBridge.Generation || !CombatManager.Instance.IsInProgress || !AndroidSettingsBridge.GetBool(CombatStateBridge.SettingKey, true))
                return;
            var phase = _player.Phase;
            if (phase is not (PlayerTurnPhase.Start or PlayerTurnPhase.AutoPrePlay or PlayerTurnPhase.Play))
                return;
            var room = NCombatRoom.Instance;
            if (room == null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
                return;
            if (phase == PlayerTurnPhase.Play) // before it: wait, no new snapshot may come when it begins
            {
                Send(room);
                if (!_sentCovered)
                    CombatStateBridge.Resync(_combat);
            }
            room.GetTree().CreateTimer(PollSeconds).Timeout += _tick;
        }
        catch (Exception exception)
        {
            if (_loggedFailure)
                return;
            _loggedFailure = true;
            PatchHelper.Log($"[CombatBridge] positions failed: {exception}"); // the next generation starts a new loop
        }
    }

    private void Send(NCombatRoom room)
    {
        var hand = room.Ui.Hand;
        // Map, capstones (piles, deck, settings), overlays, modals and inspect screens all take the screen context.
        var covered = hand.IsInCardSelection || NOverlayStack.Instance?.ScreenCount > 0 || !ActiveScreenContext.Instance.IsCurrent(room);
        var window = DisplayServer.WindowGetSize();
        _keys.Clear();
        _rects.Clear();
        _cards = 0;
        if (!covered)
        {
            var toScreen = room.GetViewport().GetScreenTransform();
            MeasureHand(hand, toScreen);
            foreach (var node in room.CreatureNodes)
            {
                var creature = node?.Entity;
                if (creature == null || !creature.IsEnemy || !creature.IsAlive || creature.CombatId == null || !GodotObject.IsInstanceValid(node))
                    continue;
                var hitbox = node.Hitbox;
                if (hitbox == null || !GodotObject.IsInstanceValid(hitbox) || !hitbox.IsInsideTree())
                    continue;
                _keys.Add(creature);
                _rects.Add(Pixels(toScreen * hitbox.GetGlobalTransformWithCanvas(), Vector2.Zero, hitbox.Size));
            }
        }
        if (_sent && covered == _sentCovered && window == _sentWindow && !Changed())
            return;

        _json.Clear();
        _json.Append("{\"event\":\"pos\",\"screen\":{\"w\":").Append(window.X).Append(",\"h\":").Append(window.Y)
            .Append("},\"covered\":").Append(covered ? "true" : "false").Append(",\"hand\":[");
        for (var i = 0; i < _cards; i++)
        {
            if (i > 0)
                _json.Append(',');
            if (_rects[i].Size == Vector2I.Zero)
                _json.Append("null");
            else
                AppendRect(_rects[i]);
        }
        _json.Append("],\"foes\":{");
        for (var i = _cards; i < _keys.Count; i++)
        {
            if (i > _cards)
                _json.Append(',');
            _json.Append('"').Append(((Creature)_keys[i]).CombatId.ToString()).Append("\":");
            AppendRect(_rects[i]);
        }
        _json.Append("}}");
        CombatStateBridge.Deliver(_json.ToString());

        _sent = true;
        _sentCovered = covered;
        _sentWindow = window;
        _sentCards = _cards;
        (_keys, _sentKeys) = (_sentKeys, _keys);
        (_rects, _sentRects) = (_sentRects, _rects);
    }

    private void MeasureHand(NPlayerHand hand, Transform2D toScreen)
    {
        var container = hand.CardHolderContainer;
        var full = toScreen * container.GetGlobalTransformWithCanvas();
        _targetScale ??= AccessTools.FieldRefAccess<NHandCardHolder, Vector2>("_targetScale");
        _holders.Clear();
        var count = container.GetChildCount();
        for (var i = 0; i < count; i++)
        {
            // A dragged or played card's holder has left this container.
            if (container.GetChild(i) is NHandCardHolder holder && holder.CardNode != null && holder.Visible && !holder.IsQueuedForDeletion())
                _holders.Add(holder);
        }
        var cards = _player.Hand.Cards;
        _cards = cards.Count;
        for (var c = 0; c < _cards; c++)
        {
            var rect = default(Rect2I);
            for (var i = 0; i < _holders.Count; i++)
            {
                var holder = _holders[i];
                if (holder.CardModel != cards[c])
                    continue;
                // The lifted card is scaled and straightened at once (its targets stay those of the
                // resting card); the others ease toward their targets.
                var lifted = holder == hand.FocusedHolder;
                var scale = lifted ? holder.Scale : _targetScale(holder);
                if (scale.X < 0.01f) // not laid out yet
                    scale = holder.Scale;
                var angle = Mathf.DegToRad(lifted ? holder.RotationDegrees : holder.TargetAngle);
                // The holder's origin is the card's centre: the hitbox is 300x422 at (-150,-211), no pivot.
                var hitbox = holder.Hitbox;
                var size = hitbox.Size * scale;
                var top = hitbox.Position * scale + new Vector2(size.X * 0.5f, 0f);
                // Upright rect hung from where the tilted card's top edge has its middle.
                rect = Pixels(full, holder.TargetPosition + top.Rotated(angle) - new Vector2(size.X * 0.5f, 0f), size);
                break;
            }
            _keys.Add(cards[c]);
            _rects.Add(rect);
        }
    }

    // Transforms are scale and shift only (nothing above the holders/creatures is rotated).
    private static Rect2I Pixels(Transform2D transform, Vector2 position, Vector2 size)
    {
        var a = transform * position;
        var b = transform * (position + size);
        var x = Mathf.RoundToInt(Math.Min(a.X, b.X));
        var y = Mathf.RoundToInt(Math.Min(a.Y, b.Y));
        return new Rect2I(x, y, Math.Max(1, Mathf.RoundToInt(Math.Abs(b.X - a.X))), Math.Max(1, Mathf.RoundToInt(Math.Abs(b.Y - a.Y))));
    }

    private bool Changed()
    {
        if (_cards != _sentCards || _keys.Count != _sentKeys.Count)
            return true;
        for (var i = 0; i < _keys.Count; i++)
        {
            var a = _rects[i];
            var b = _sentRects[i];
            if (!ReferenceEquals(_keys[i], _sentKeys[i]) || (a.Size == Vector2I.Zero) != (b.Size == Vector2I.Zero)
                || Math.Abs(a.Position.X - b.Position.X) >= MinMove || Math.Abs(a.Position.Y - b.Position.Y) >= MinMove
                || Math.Abs(a.Size.X - b.Size.X) >= MinMove || Math.Abs(a.Size.Y - b.Size.Y) >= MinMove)
                return true;
        }
        return false;
    }

    private void AppendRect(Rect2I r) =>
        _json.Append('[').Append(r.Position.X).Append(',').Append(r.Position.Y).Append(',').Append(r.Size.X).Append(',').Append(r.Size.Y).Append(']');
}
