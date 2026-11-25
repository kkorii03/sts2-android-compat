using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Sends what the player can see in a single-player combat to the separate personal app
/// com.kkorii.sts2combat (explicit broadcast, no hidden draw order, no RNG). Nothing is shown or
/// computed in the game. Setting combat_state_bridge (default on).
/// </summary>
public static class CombatStateBridge
{
    public const string SettingKey = "combat_state_bridge";
    private const double SettleSeconds = 0.35;

    private static GodotObject _java;
    private static int _generation;

    internal static int Generation => _generation;

    public static void Apply(Harmony harmony)
    {
        if (!NativeFeel.IsAndroid)
            return;
        var self = typeof(CombatStateBridge);
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterPlayerTurnStart), prefix: PatchHelper.Method(self, nameof(RefreshPrefix)));
        // After the turn-start card choices (Tyranny and the like), which cancel the state above.
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterAutoPrePlayPhaseEntered), prefix: PatchHelper.Method(self, nameof(RefreshPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterCardPlayed), prefix: PatchHelper.Method(self, nameof(RefreshPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterPotionUsed), prefix: PatchHelper.Method(self, nameof(RefreshPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.BeforeSideTurnEnd), prefix: PatchHelper.Method(self, nameof(HidePrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterCombatEnd), prefix: PatchHelper.Method(self, nameof(HidePrefix)));
        // No AfterCombatEnd on death, save and quit, or abandon.
        PatchHelper.Patch(harmony, typeof(CombatManager), nameof(CombatManager.LoseCombat), prefix: PatchHelper.Method(self, nameof(HidePrefix)));
        PatchHelper.Patch(harmony, typeof(RunManager), nameof(RunManager.CleanUp), prefix: PatchHelper.Method(self, nameof(HidePrefix)));
        // A box left over from a game that was closed in the middle of a fight.
        PatchHelper.Patch(harmony, typeof(MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu), "_Ready", postfix: PatchHelper.Method(self, nameof(HidePrefix)));
        // "Choose N cards" during combat: what may be chosen, so the separate app can advise.
        Select(harmony, "FromCombatPile", nameof(PilePrefix), typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>));
        Select(harmony, "FromSimpleGrid", nameof(GridPrefix), typeof(PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(CardSelectorPrefs));
        Select(harmony, "FromHand", nameof(HandPrefix), typeof(PlayerChoiceContext), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(AbstractModel));
        Select(harmony, "FromHandForDiscard", nameof(HandPrefix), typeof(PlayerChoiceContext), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(AbstractModel));
        Select(harmony, "FromHandForUpgrade", nameof(HandUpgradePrefix), typeof(PlayerChoiceContext), typeof(Player), typeof(AbstractModel));
        Select(harmony, "FromChooseACardScreen", nameof(ChoosePrefix), typeof(PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(bool));
    }

    private static void Select(Harmony harmony, string name, string prefix, params Type[] types)
    {
        var method = AccessTools.Method(typeof(CardSelectCmd), name, types);
        if (method == null)
        {
            PatchHelper.Log($"[CombatBridge] {name} not found");
            return;
        }
        harmony.Patch(method, prefix: new HarmonyMethod(PatchHelper.Method(typeof(CombatStateBridge), prefix)));
    }

    public static void PilePrefix(CardPile pile, Player player, CardSelectorPrefs prefs, Func<CardModel, bool> filter) =>
        Choice(pile.Type.ToString(), player, pile.Cards.Where(c => filter == null || filter(c)), prefs);

    public static void GridPrefix(IReadOnlyList<CardModel> cardsIn, Player player, CardSelectorPrefs prefs) => Choice("Grid", player, cardsIn, prefs);

    public static void HandPrefix(Player player, CardSelectorPrefs prefs, Func<CardModel, bool> filter, MethodBase __originalMethod) =>
        Choice(__originalMethod.Name == "FromHandForDiscard" ? "HandDiscard" : "Hand", player, PileType.Hand.GetPile(player).Cards.Where(c => filter == null || filter(c)), prefs);

    public static void HandUpgradePrefix(Player player) => Choice("HandUpgrade", player, PileType.Hand.GetPile(player).Cards.Where(c => c.IsUpgradable), null);

    public static void ChoosePrefix(IReadOnlyList<CardModel> cards, Player player, bool canSkip) => Choice("Choose", player, cards, null, canSkip);

    private static void Choice(string from, Player player, IEnumerable<CardModel> cards, CardSelectorPrefs? prefs, bool canSkip = false)
    {
        try
        {
            var combat = player?.Creature?.CombatState;
            if (combat == null || !AndroidSettingsBridge.GetBool(SettingKey, true))
                return;
            var list = cards.OrderBy(c => c.Id.Entry, StringComparer.Ordinal).ToList(); // never the real pile order
            var min = prefs?.MinSelect ?? (canSkip ? 0 : 1);
            var max = prefs?.MaxSelect ?? 1;
            if (list.Count == 0 || list.Count <= min && from != "Choose")
                return; // the game picks by itself
            _generation++;
            var prompt = prefs?.Prompt;
            var snapshot = CombatRecorder.Snapshot("choice", combat, null, null, w =>
            {
                w.WriteStartObject("choice");
                w.WriteString("from", from);
                w.WriteString("promptKey", from == "HandUpgrade" ? "CHOOSE_CARD_UPGRADE_HEADER" : prompt?.LocEntryKey);
                string text = null;
                try { text = prompt?.GetFormattedText(); } catch { }
                w.WriteString("prompt", text);
                w.WriteNumber("min", min);
                w.WriteNumber("max", max);
                w.WriteStartArray("cards");
                foreach (var card in list)
                {
                    w.WriteStartObject();
                    CombatRecorder.Card(w, card);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            if (snapshot == null)
                return;
            var json = Encoding.UTF8.GetString(snapshot);
            CombatRecorder.AdvisorState(json);
            Deliver(json);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[CombatBridge] choice failed: {exception.Message}"); // the next state still goes out
        }
    }

    public static void RefreshPrefix(ICombatState combatState)
    {
        try
        {
            if (combatState == null || !AndroidSettingsBridge.GetBool(SettingKey, true))
                return;
            var generation = ++_generation;
            // Wait for the play to finish moving cards, then send the latest state once.
            if (Engine.GetMainLoop() is SceneTree tree)
                tree.CreateTimer(SettleSeconds).Timeout += () =>
                {
                    if (generation != _generation)
                        return;
                    try
                    {
                        // Potions also work after the fight and in the enemy turn: nothing to advise then.
                        var phase = combatState.Players.FirstOrDefault()?.PlayerCombatState?.Phase;
                        if (!CombatManager.Instance.IsInProgress || phase is null or PlayerTurnPhase.None or PlayerTurnPhase.End)
                            return;
                        var snapshot = CombatRecorder.Snapshot("combat", combatState, null, null);
                        if (snapshot == null)
                            return;
                        _told = _seen = Look(combatState);
                        _settled = 0;
                        Deliver(Encoding.UTF8.GetString(snapshot));
                        CombatPositions.Start(combatState, generation);
                    }
                    catch (Exception exception)
                    {
                        Fail(exception);
                    }
                };
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    // What was sent last, what it looked like at the last look, and for how many looks it has been like that.
    private static int _told, _seen, _settled;

    /// What a player sees change: a late effect (a card that comes back, a power, a relic) that no hook above follows.
    private static int Look(ICombatState combat)
    {
        var hash = new HashCode();
        foreach (var creature in combat.Creatures)
        {
            hash.Add(creature.CombatId);
            hash.Add(creature.CurrentHp);
            hash.Add(creature.Block);
            foreach (var power in creature.Powers)
            {
                hash.Add(power.Id.Entry);
                hash.Add(power.Amount);
            }
        }
        foreach (var player in combat.Players)
            if (player.PlayerCombatState is { } state)
            {
                hash.Add(state.TurnNumber);
                hash.Add(state.Energy);
                hash.Add(state.Stars);
                foreach (var card in state.Hand.Cards)
                    hash.Add(card);
                hash.Add(state.DrawPile.Cards.Count);
                hash.Add(state.DiscardPile.Cards.Count);
                hash.Add(state.ExhaustPile.Cards.Count);
            }
        return hash.ToHashCode();
    }

    /// Called a few times a second in my turn while nothing covers the fight: sends the state again if it has
    /// changed since and is quiet now.
    internal static void Resync(ICombatState combat)
    {
        if (RunManager.Instance?.ActionExecutor?.IsRunning ?? false)
        {
            _settled = 0;
            return;
        }
        var look = Look(combat);
        if (look == _told || look != _seen)
        {
            _seen = look;
            _settled = 0;
            return;
        }
        if (++_settled < 2)
            return;
        _settled = 0;
        _told = look;
        var snapshot = CombatRecorder.Snapshot("combat", combat, null, null);
        if (snapshot != null)
        {
            Deliver(Encoding.UTF8.GetString(snapshot));
            Resent++;
        }
    }

    internal static int Resent;

    public static void HidePrefix()
    {
        try
        {
            _generation++;
            Deliver("{\"event\":\"hide\"}");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    internal static void Deliver(string json)
    {
        if (_java == null || !GodotObject.IsInstanceValid(_java))
            _java = (GodotObject)Engine.GetSingleton("JavaClassWrapper").Call("wrap", "com.godot.game.GodotApp");
        _java.Call("sendCombatState", json);
    }

    private static void Fail(Exception exception)
    {
        PatchHelper.Log($"[CombatBridge] failed: {exception.Message}"); // the game goes on; the next state is tried again
    }
}