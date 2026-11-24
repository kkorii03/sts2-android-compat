using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Single-player combat log for checking a separate combat calculator: one JSON line per event
/// (turn start/end, card and potion before/after) with what the player can see. Files stay on
/// the phone. The draw pile is written SORTED and no RNG is read, so the log never holds hidden
/// draw order. Every hook only observes; failures log once and never reach the game.
/// Setting combat_recorder (default on). Installed from LateFeaturePatches.
/// </summary>
public static class CombatRecorder
{
    public const string SettingKey = "combat_recorder";
    private const string Folder = "/storage/emulated/0/Android/data/com.megacrit.sts2re/files/combat-log";
    private const int KeepFiles = 80;

    private static readonly ConcurrentQueue<(string File, byte[] Line)> Pending = new();
    private static int _writing;
    private static string _file;
    private static int _seq;
    private static bool _failed;

    public static void Apply(Harmony harmony)
    {
        if (!NativeFeel.IsAndroid || !AndroidSettingsBridge.GetBool(SettingKey, true))
            return;
        var self = typeof(CombatRecorder);
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.BeforeCombatStart), prefix: PatchHelper.Method(self, nameof(CombatStartPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterPlayerTurnStart), prefix: PatchHelper.Method(self, nameof(PlayerTurnStartPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.BeforeCardPlayed), prefix: PatchHelper.Method(self, nameof(BeforeCardPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterCardPlayed), prefix: PatchHelper.Method(self, nameof(AfterCardPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.BeforePotionUsed), prefix: PatchHelper.Method(self, nameof(BeforePotionPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterPotionUsed), prefix: PatchHelper.Method(self, nameof(AfterPotionPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.BeforeSideTurnEnd), prefix: PatchHelper.Method(self, nameof(BeforeSideEndPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterSideTurnEnd), prefix: PatchHelper.Method(self, nameof(AfterSideEndPrefix)));
        PatchHelper.Patch(harmony, typeof(Hook), nameof(Hook.AfterCombatEnd), prefix: PatchHelper.Method(self, nameof(CombatEndPrefix)));
    }

    public static void CombatStartPrefix(ICombatState combatState)
    {
        _file = null; // new file on the first event of this combat
        Record("combat_start", combatState, null, null);
    }

    public static void PlayerTurnStartPrefix(ICombatState combatState) => Record("turn_start", combatState, null, null);
    public static void BeforeCardPrefix(ICombatState combatState, CardPlay cardPlay) => Record("before_card", combatState, cardPlay, null);
    public static void AfterCardPrefix(ICombatState combatState, CardPlay cardPlay) => Record("after_card", combatState, cardPlay, null);
    public static void BeforePotionPrefix(ICombatState combatState, PotionModel potion) => Record("before_potion", combatState, null, potion);
    public static void AfterPotionPrefix(ICombatState combatState, PotionModel potion) => Record("after_potion", combatState, null, potion);
    public static void BeforeSideEndPrefix(ICombatState combatState, CombatSide side) => Record("before_end_" + side, combatState, null, null);
    public static void AfterSideEndPrefix(ICombatState combatState, CombatSide side) => Record("after_end_" + side, combatState, null, null);

    public static void CombatEndPrefix(ICombatState combatState)
    {
        Record("combat_end", combatState, null, null);
        _file = null;
    }

    /// The player marks "look at this moment" from the settings screen; also works outside combat (no-op there).
    public static bool Mark()
    {
        var combat = CombatManager.Instance?.DebugOnlyGetState();
        if (combat == null || _failed)
            return false;
        Record("marker", combat, null, null);
        return !_failed;
    }

    /// The exact state sent to the card advisor, so reward advice can be checked too.
    public static void AdvisorState(string json)
    {
        if (_failed || !AndroidSettingsBridge.GetBool(SettingKey, true))
            return;
        Pending.Enqueue((Path.Combine(Folder, $"rewards-{DateTime.Now:yyyyMMdd}.jsonl"), System.Text.Encoding.UTF8.GetBytes(json)));
        if (Interlocked.Exchange(ref _writing, 1) == 0)
            Task.Run(Flush);
    }

    public static void Share()
    {
        try
        {
            var wrapper = (Godot.GodotObject)Godot.Engine.GetSingleton("JavaClassWrapper").Call("wrap", "com.godot.game.GodotApp");
            wrapper.Call("shareCombatLogs");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[CombatRecorder] share failed: {exception.Message}");
        }
    }

    private static void Record(string name, ICombatState combat, CardPlay play, PotionModel potion)
    {
        if (_failed)
            return;
        try
        {
            var line = Snapshot(name, combat, play, potion);
            if (line == null)
                return;
            var state = RunManager.Instance.DebugOnlyGetState();
            if (_file == null)
            {
                _file = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-a{state.CurrentActIndex + 1}f{state.TotalFloor}.jsonl");
                _seq = 0;
            }
            Pending.Enqueue((_file, line));
            if (Interlocked.Exchange(ref _writing, 1) == 0)
                Task.Run(Flush);
        }
        catch (Exception exception)
        {
            _failed = true; // this session only
            PatchHelper.Log($"[CombatRecorder] stopped at {name}: {exception}");
        }
    }

    /// What the player can see right now, as one JSON object; null outside a single-player combat.
    public static byte[] Snapshot(string name, ICombatState combat, CardPlay play, PotionModel potion, Action<Utf8JsonWriter> extra = null)
    {
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (combat == null || state == null || state.Players.Count != 1)
                return null;
            var player = state.Players[0];
            var pcs = player.PlayerCombatState;
            if (pcs == null)
                return null;
            var history = CombatManager.Instance.History;
            var buffer = new ArrayBufferWriter<byte>(8192);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteNumber("v", 1);
                w.WriteNumber("seq", _seq++);
                w.WriteString("event", name);
                w.WriteString("character", player.Character.Id.Entry);
                w.WriteNumber("ascension", state.AscensionLevel);
                w.WriteNumber("turn", pcs.TurnNumber);
                w.WriteNumber("energy", pcs.Energy);
                w.WriteNumber("maxEnergy", pcs.MaxEnergy);
                w.WriteNumber("stars", pcs.Stars);
                w.WriteNumber("plays", history.CardPlaysStarted.Count(e => e.HappenedThisTurn(combat) && e.CardPlay.Player == player));
                w.WriteNumber("skills", history.CardPlaysFinished.Count(e => e.HappenedThisTurn(combat) && e.CardPlay.Player == player && e.CardPlay.Card.Type == CardType.Skill));
                w.WriteNumber("attacks", history.CardPlaysFinished.Count(e => e.HappenedThisTurn(combat) && e.CardPlay.Player == player && e.CardPlay.Card.Type == CardType.Attack));
                if (play != null)
                {
                    w.WriteStartObject("play");
                    Card(w, play.Card);
                    w.WriteString("target", play.Target?.CombatId?.ToString());
                    w.WriteBoolean("auto", play.IsAutoPlay);
                    w.WriteNumber("playIndex", play.PlayIndex);
                    w.WriteNumber("playCount", play.PlayCount);
                    w.WriteEndObject();
                }
                if (potion != null)
                    w.WriteString("potion", potion.Id.Entry);
                w.WriteStartArray("creatures");
                foreach (var creature in combat.Creatures)
                    Body(w, creature, combat);
                w.WriteEndArray();
                w.WriteStartArray("hand");
                foreach (var card in pcs.Hand.Cards)
                {
                    w.WriteStartObject();
                    Card(w, card);
                    w.WriteBoolean("canPlay", card.CanPlay(out _, out _));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                Pile(w, "draw", pcs.DrawPile, sorted: true); // never the real order
                Pile(w, "discard", pcs.DiscardPile, sorted: false);
                Pile(w, "exhaust", pcs.ExhaustPile, sorted: false);
                w.WriteStartArray("relics");
                foreach (var relic in player.Relics) // read every time: relics can be swapped mid-run
                {
                    w.WriteStartObject();
                    w.WriteString("id", relic.Id.Entry);
                    if (relic.ShowCounter)
                        w.WriteNumber("counter", relic.DisplayAmount);
                    w.WriteString("status", relic.Status.ToString());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("potions");
                foreach (var p in player.Potions)
                {
                    w.WriteStartObject();
                    w.WriteString("id", p.Id.Entry);
                    w.WriteString("name", p.Title.GetFormattedText());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                extra?.Invoke(w);
                w.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
    }

    internal static void Card(Utf8JsonWriter w, CardModel card)
    {
        w.WriteString("id", card.Id.Entry);
        w.WriteString("name", card.TitleLocString.GetFormattedText());
        w.WriteString("type", card.Type.ToString());
        w.WriteString("targetType", card.TargetType.ToString());
        w.WriteNumber("up", card.CurrentUpgradeLevel);
        w.WriteNumber("cost", card.EnergyCost.CostsX ? -1 : card.EnergyCost.GetWithModifiers(CostModifiers.All));
        if (card.CurrentStarCost >= 0)
            w.WriteNumber("stars", card.CurrentStarCost);
        if (card.Affliction != null)
            w.WriteString("affliction", card.Affliction.Id.Entry);
        if (card.Enchantment != null)
            w.WriteString("enchantment", card.Enchantment.Id.Entry);
        if (card.Keywords.Count > 0)
        {
            w.WriteStartArray("keywords");
            foreach (var keyword in card.Keywords)
                w.WriteStringValue(keyword.ToString());
            w.WriteEndArray();
        }
        w.WriteStartObject("vars");
        foreach (var pair in card.DynamicVars)
            w.WriteNumber(pair.Key, pair.Value.BaseValue);
        w.WriteEndObject();
    }

    private static void Pile(Utf8JsonWriter w, string name, CardPile pile, bool sorted)
    {
        IEnumerable<CardModel> cards = pile.Cards;
        if (sorted)
            cards = cards.OrderBy(c => c.Id.Entry, StringComparer.Ordinal).ThenBy(c => c.CurrentUpgradeLevel);
        w.WriteStartArray(name);
        foreach (var card in cards)
        {
            w.WriteStartObject();
            Card(w, card);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Body(Utf8JsonWriter w, Creature creature, ICombatState combat)
    {
        w.WriteStartObject();
        w.WriteString("combatId", creature.CombatId?.ToString());
        w.WriteString("id", creature.ModelId.Entry);
        w.WriteString("name", creature.Name);
        w.WriteString("side", creature.Side.ToString());
        w.WriteNumber("hp", creature.CurrentHp);
        w.WriteNumber("maxHp", creature.MaxHp);
        w.WriteNumber("block", creature.Block);
        w.WriteStartArray("powers");
        foreach (var power in creature.Powers)
        {
            w.WriteStartObject();
            w.WriteString("id", power.Id.Entry);
            w.WriteNumber("amount", power.Amount);
            try
            {
                // what the player sees when it differs (e.g. Hardened Shell: HP it can still lose this turn)
                var shown = power.DisplayAmount;
                if (shown != power.Amount)
                    w.WriteNumber("display", shown);
            }
            catch
            {
            }
            if (power.SkipNextDurationTick)
                w.WriteBoolean("skipTick", true);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (creature.Monster != null && creature.IsAlive)
        {
            w.WriteString("move", creature.Monster.NextMove.Id);
            w.WriteStartArray("intents");
            foreach (var intent in creature.Monster.NextMove.Intents)
            {
                w.WriteStartObject();
                w.WriteString("type", intent.GetType().Name);
                if (intent is AttackIntent attack)
                {
                    w.WriteNumber("damage", attack.GetSingleDamage(combat.Creatures.Where(c => c.IsPlayer), creature));
                    if (attack.DamageCalc != null)
                        w.WriteNumber("base", attack.DamageCalc());
                    w.WriteNumber("repeats", attack.Repeats);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void Flush()
    {
        do
        {
            try
            {
                Directory.CreateDirectory(Folder);
                while (Pending.TryDequeue(out var item))
                {
                    using var stream = new FileStream(item.File, FileMode.Append, System.IO.FileAccess.Write, FileShare.Read);
                    stream.Write(item.Line, 0, item.Line.Length);
                    stream.WriteByte((byte)'\n');
                }
                foreach (var old in new DirectoryInfo(Folder).GetFiles("*.jsonl").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepFiles))
                    old.Delete();
            }
            catch (Exception exception)
            {
                _failed = true;
                while (Pending.TryDequeue(out _)) { }
                PatchHelper.Log($"[CombatRecorder] write failed: {exception.Message}");
            }
            finally
            {
                Volatile.Write(ref _writing, 0);
            }
        }
        while (!Pending.IsEmpty && Interlocked.Exchange(ref _writing, 1) == 0);
    }
}
