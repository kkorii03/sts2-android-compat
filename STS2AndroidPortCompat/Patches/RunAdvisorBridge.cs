using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Out-of-combat screens for the separate personal combat app (com.kkorii.sts2combat): the whole
/// merchant (cards, relics, potions, card removal), the rest site options, and the deck upgrade
/// screen with each card's upgraded numbers. Only what the player can see on screen; nothing is
/// shown or changed in the game. Same setting as the combat bridge (combat_state_bridge).
/// Every hook only observes and never throws into the game.
/// </summary>
public static class RunAdvisorBridge
{
    private const double ShopSettleSeconds = 0.9;
    private const double RestSettleSeconds = 0.8;
    private static int _generation;
    private static bool _shown;
    private static GodotObject _java;
    private static bool _failed;

    private static bool Enabled => !_failed && NativeFeel.IsAndroid && AndroidSettingsBridge.GetBool(CombatStateBridge.SettingKey, true);

    public static void Apply(Harmony harmony)
    {
        if (!NativeFeel.IsAndroid)
            return;
        var self = typeof(RunAdvisorBridge);
        var shop = typeof(NMerchantInventory);
        PatchHelper.Patch(harmony, shop, nameof(NMerchantInventory.Open), postfix: PatchHelper.Method(self, nameof(ShopPostfix)));
        PatchHelper.Patch(harmony, shop, "OnPurchaseCompleted", postfix: PatchHelper.Method(self, nameof(ShopPostfix)));
        PatchHelper.Patch(harmony, shop, "Close", postfix: PatchHelper.Method(self, nameof(ShopLeavePrefix)));
        PatchHelper.Patch(harmony, shop, "_ExitTree", prefix: PatchHelper.Method(self, nameof(ShopLeavePrefix)));
        PatchHelper.Patch(harmony, typeof(RestSiteOption), nameof(RestSiteOption.Generate), postfix: PatchHelper.Method(self, nameof(RestPostfix)));
        PatchHelper.Patch(harmony, typeof(NRestSiteRoom), nameof(NRestSiteRoom.AfterSelectingOption), postfix: PatchHelper.Method(self, nameof(LeavePrefix)));
        PatchHelper.Patch(harmony, typeof(NRestSiteRoom), nameof(NRestSiteRoom.BeforeExitingRoom), prefix: PatchHelper.Method(self, nameof(LeavePrefix)));
        PatchHelper.Patch(harmony, typeof(NMapScreen), nameof(NMapScreen.Open), postfix: PatchHelper.Method(self, nameof(MapPostfix)));
        PatchHelper.Patch(harmony, typeof(NMapScreen), nameof(NMapScreen.Close), prefix: PatchHelper.Method(self, nameof(MapGonePrefix)));
        PatchHelper.Patch(harmony, typeof(NEventRoom), "SetOptions", postfix: PatchHelper.Method(self, nameof(AncientPostfix)));
        PatchHelper.Patch(harmony, typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked), prefix: PatchHelper.Method(self, nameof(AncientGonePrefix)));
        PatchHelper.Patch(harmony, typeof(NEventRoom), "_ExitTree", prefix: PatchHelper.Method(self, nameof(AncientGonePrefix)));
        PatchHelper.Patch(harmony, typeof(RunManager), nameof(RunManager.CleanUp), prefix: PatchHelper.Method(self, nameof(LeavePrefix))); // save and quit, abandon
        PatchHelper.Patch(harmony, typeof(NDeckUpgradeSelectScreen), nameof(NDeckUpgradeSelectScreen.ShowScreen), postfix: PatchHelper.Method(self, nameof(UpgradePostfix)));
        var removal = AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForRemoval));
        if (removal != null)
            harmony.Patch(removal, prefix: new HarmonyMethod(PatchHelper.Method(self, nameof(RemovalPrefix))));
        var transform = AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForTransformation));
        if (transform != null)
            harmony.Patch(transform, prefix: new HarmonyMethod(PatchHelper.Method(self, nameof(TransformPrefix))));
        var enchant = AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForEnchantment), new[] { typeof(IReadOnlyList<CardModel>), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs) });
        if (enchant != null)
            harmony.Patch(enchant, prefix: new HarmonyMethod(PatchHelper.Method(self, nameof(EnchantPrefix))));
        // _ExitTree is declared on the shared grid screen (combat screens too): only ours count.
        PatchHelper.Patch(harmony, typeof(NCardGridSelectionScreen), "_ExitTree", prefix: PatchHelper.Method(self, nameof(GridGonePrefix)));
    }

    private static Action _again; // the shop / rest site advice, sent again when a deck screen opened from it closes
    private static bool _gridOpen;
    private static bool _shopOpen; // the old shop node leaves the tree after the next room has made its options

    public static void GridGonePrefix()
    {
        if (!_gridOpen)
            return;
        _gridOpen = false;
        GonePrefix();
        Again();
    }

    private static void Again()
    {
        try
        {
            if (_again != null)
                Later(null, RestSettleSeconds, ++_generation, _again);
        }
        catch (Exception exception)
        {
            Fail("again", exception);
        }
    }

    // ---- card removal screen (merchant, events) -----------------------------------------

    public static void RemovalPrefix(Player player, CardSelectorPrefs prefs, Func<CardModel, bool> filter)
    {
        try
        {
            if (!Enabled || player == null)
                return;
            var list = player.Deck.Cards.Where(c => c.IsRemovable && (filter == null || filter(c))).ToList();
            if (list.Count == 0 || !prefs.RequireManualConfirmation && list.Count <= prefs.MinSelect)
                return; // the game takes them without a screen
            ++_generation;
            Send("remove_select", w =>
            {
                w.WriteNumber("pick", prefs.MaxSelect);
                w.WriteStartArray("removable");
                foreach (var card in list)
                {
                    w.WriteStartObject();
                    CardFacts(w, card);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            _gridOpen = true;
        }
        catch (Exception exception)
        {
            Fail("removal", exception);
        }
    }

    // ---- transform / enchant a deck card (events, rest site, relics) ------------------------

    public static void TransformPrefix(Player player, CardSelectorPrefs prefs)
    {
        if (player != null)
            DeckSelect("transform_select", player.Deck.Cards.Where(c => c.Type != CardType.Quest && c.IsTransformable).ToList(), prefs, null);
    }

    public static void EnchantPrefix(IReadOnlyList<CardModel> cards, EnchantmentModel enchantment, int amount, CardSelectorPrefs prefs)
    {
        if (cards != null)
            DeckSelect("enchant_select", cards.ToList(), prefs, w =>
            {
                w.WriteString("enchantment", enchantment.Id.Entry);
                w.WriteNumber("amount", amount);
                Try(() => w.WriteString("enchantmentName", Plain(enchantment.Title.GetFormattedText())));
            });
    }

    private static void DeckSelect(string name, List<CardModel> list, CardSelectorPrefs prefs, Action<Utf8JsonWriter> more)
    {
        try
        {
            if (!Enabled || list.Count == 0 || list.Count <= prefs.MinSelect && !prefs.RequireManualConfirmation)
                return;
            ++_generation;
            Send(name, w =>
            {
                w.WriteNumber("pick", prefs.MaxSelect);
                more?.Invoke(w);
                w.WriteStartArray("cards");
                foreach (var card in list)
                {
                    w.WriteStartObject();
                    CardFacts(w, card);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            _gridOpen = true;
        }
        catch (Exception exception)
        {
            Fail(name, exception);
        }
    }

    // ---- merchant ----------------------------------------------------------------------

    public static void ShopPostfix(NMerchantInventory __instance)
    {
        try
        {
            if (!Enabled || __instance == null)
                return;
            var generation = ++_generation;
            _shopOpen = true;
            var node = __instance;
            Later(node, ShopSettleSeconds, generation, _again = () =>
            {
                if (!GodotObject.IsInstanceValid(node) || !node.IsOpen || node.Inventory == null)
                    return;
                Send("shop", w => Shop(w, node.Inventory));
            });
        }
        catch (Exception exception)
        {
            Fail("shop", exception);
        }
    }

    private static void Shop(Utf8JsonWriter w, MerchantInventory inventory)
    {
        w.WriteStartArray("shopCards");
        foreach (var entry in inventory.CardEntries)
        {
            var card = entry.CreationResult?.Card;
            if (!entry.IsStocked || card == null)
                continue;
            w.WriteStartObject();
            CardFacts(w, card);
            w.WriteNumber("price", entry.Cost);
            w.WriteBoolean("onSale", entry.IsOnSale);
            w.WriteBoolean("colorless", inventory.ColorlessCardEntries.Contains(entry));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("shopRelics");
        foreach (var entry in inventory.RelicEntries)
        {
            if (!entry.IsStocked || entry.Model == null)
                continue;
            w.WriteStartObject();
            Relic(w, entry.Model);
            w.WriteNumber("price", entry.Cost);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("shopPotions");
        foreach (var entry in inventory.PotionEntries)
        {
            if (!entry.IsStocked || entry.Model == null)
                continue;
            w.WriteStartObject();
            Potion(w, entry.Model);
            w.WriteNumber("price", entry.Cost);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        var removal = inventory.CardRemovalEntry;
        if (removal != null && removal.IsStocked)
            w.WriteNumber("removalPrice", removal.Cost);
    }

    // ---- rest site ---------------------------------------------------------------------

    public static void RestPostfix(Player player, List<RestSiteOption> __result)
    {
        try
        {
            if (!Enabled || __result == null || player == null)
                return;
            var generation = ++_generation;
            _shopOpen = false; // this room is the current one now
            var options = __result.ToList();
            Later(null, RestSettleSeconds, generation, _again = () => Send("rest_site", w =>
            {
                w.WriteStartArray("restOptions");
                foreach (var option in options)
                {
                    w.WriteStartObject();
                    w.WriteString("id", option.OptionId);
                    Try(() => w.WriteString("name", option.Title.GetFormattedText()));
                    Try(() => w.WriteBoolean("enabled", option.IsEnabled));
                    if (option is HealRestSiteOption)
                        Try(() => w.WriteNumber("heal", (int)HealRestSiteOption.GetHealAmount(player)));
                    if (option is SmithRestSiteOption smith)
                        Try(() => w.WriteNumber("count", smith.SmithCount));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                Upgradable(w, player.Deck.Cards.Where(c => c.IsUpgradable), player.RunState);
            }));
        }
        catch (Exception exception)
        {
            Fail("rest", exception);
        }
    }

    // ---- map -----------------------------------------------------------------------------

    private static bool _mapOpen;

    public static void MapPostfix()
    {
        try
        {
            if (!Enabled || _mapOpen || CombatManager.Instance.IsInProgress)
                return;
            _mapOpen = true;
            Later(null, 0.4, ++_generation, () => Send("map", w => Map(w, RunManager.Instance.DebugOnlyGetState())));
        }
        catch (Exception exception)
        {
            Fail("map", exception);
        }
    }

    /// What the player can scroll through on the map screen: every point, its kind and its paths.
    private static void Map(Utf8JsonWriter w, RunState state)
    {
        var map = state.Map;
        var current = state.CurrentMapPoint;
        if (current != null)
        {
            w.WriteNumber("col", current.coord.col);
            w.WriteNumber("row", current.coord.row);
        }
        w.WriteNumber("rows", map.GetRowCount());
        Try(() => w.WriteString("boss", state.Act.BossEncounter.Id.Entry)); // its icon is on the map
        w.WriteStartArray("visited");
        foreach (var coord in state.VisitedMapCoords)
            if (map.GetPoint(coord) is { } point)
                w.WriteStringValue(point.PointType.ToString());
        w.WriteEndArray();
        w.WriteStartArray("next");
        foreach (var point in current == null ? Enumerable.Empty<MapPoint>() : MapTravel.GetTravelablePointsFrom(state, current))
            Coord(w, point);
        w.WriteEndArray();
        w.WriteStartArray("points");
        foreach (var point in map.GetAllMapPoints())
        {
            w.WriteStartObject();
            w.WriteNumber("c", point.coord.col);
            w.WriteNumber("r", point.coord.row);
            w.WriteString("t", point.PointType.ToString());
            w.WriteStartArray("to");
            foreach (var child in point.Children)
                Coord(w, child);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Coord(Utf8JsonWriter w, MapPoint point)
    {
        w.WriteStartArray();
        w.WriteNumberValue(point.coord.col);
        w.WriteNumberValue(point.coord.row);
        w.WriteEndArray();
    }

    public static void MapGonePrefix()
    {
        if (!_mapOpen)
            return;
        _mapOpen = false;
        GonePrefix();
        Again();
    }

    // ---- Ancient (the choice at the start of an act) --------------------------------------

    private static bool _ancientOpen;

    public static void AncientPostfix(EventModel eventModel)
    {
        try
        {
            if (!Enabled || eventModel == null || eventModel.IsFinished)
                return;
            var ancient = eventModel is AncientEventModel;
            var options = eventModel.CurrentOptions.Where(o => !o.IsProceed).ToList();
            if (options.Count < 2)
                return;
            _ancientOpen = true;
            Later(null, 0.4, ++_generation, _again = () => Send(ancient ? "ancient" : "event", w =>
            {
                w.WriteString(ancient ? "ancient" : "eventId", eventModel.Id.Entry);
                w.WriteStartArray("options");
                foreach (var option in options)
                {
                    w.WriteStartObject();
                    w.WriteString("id", option.Relic?.Id.Entry ?? option.TextKey?.Split('.').LastOrDefault(p => p != "title" && p != "description"));
                    w.WriteBoolean("relic", option.Relic != null);
                    w.WriteString("textKey", option.TextKey);
                    w.WriteBoolean("locked", option.IsLocked);
                    Try(() => w.WriteBoolean("kills", option.WillKillPlayer != null && eventModel.Owner != null && option.WillKillPlayer(eventModel.Owner)));
                    Try(() => w.WriteString("name", Plain(option.Title.GetFormattedText())));
                    Try(() => w.WriteString("text", Plain(option.Description.GetFormattedText())));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }));
        }
        catch (Exception exception)
        {
            Fail("ancient", exception);
        }
    }

    public static void AncientGonePrefix()
    {
        if (!_ancientOpen)
            return;
        _ancientOpen = false;
        _again = null;
        GonePrefix();
    }

    // ---- deck upgrade screen ------------------------------------------------------------

    public static void UpgradePostfix(IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs, IRunState runState)
    {
        try
        {
            if (!Enabled || cards == null)
                return;
            ++_generation;
            var list = cards.ToList();
            var count = prefs.MaxSelect;
            Send("upgrade_select", w =>
            {
                w.WriteNumber("pick", count);
                Upgradable(w, list, runState);
            });
            _gridOpen = true;
        }
        catch (Exception exception)
        {
            Fail("upgrade", exception);
        }
    }

    private static void Upgradable(Utf8JsonWriter w, IEnumerable<CardModel> cards, IRunState runState)
    {
        w.WriteStartArray("upgradable");
        foreach (var card in cards)
        {
            w.WriteStartObject();
            CardFacts(w, card);
            w.WriteStartObject("upgraded");
            Upgraded(w, card, runState);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// The upgraded numbers, the way the game's own "show upgrades" preview makes them: a clone
    /// that is upgraded and then removed from the run again.
    private static void Upgraded(Utf8JsonWriter w, CardModel card, IRunState runState)
    {
        CardModel clone = null;
        try
        {
            clone = runState.CloneCard(card);
            clone.UpgradeInternal();
            CombatRecorder.Card(w, clone);
        }
        catch (Exception exception)
        {
            w.WriteString("error", exception.GetType().Name);
        }
        finally
        {
            if (clone != null)
                Try(() => runState.RemoveCard(clone));
        }
    }

    public static void ShopLeavePrefix()
    {
        if (_shopOpen)
            LeavePrefix();
    }

    public static void LeavePrefix()
    {
        _shopOpen = false;
        _mapOpen = false;
        _ancientOpen = false;
        _again = null;
        GonePrefix();
    }

    public static void GonePrefix()
    {
        try
        {
            _generation++;
            if (!_shown || !Enabled)
                return;
            _shown = false;
            Deliver("{\"event\":\"hide\"}");
        }
        catch (Exception exception)
        {
            Fail("hide", exception);
        }
    }

    // ---- JSON ---------------------------------------------------------------------------

    private static void Send(string name, Action<Utf8JsonWriter> body)
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null || state.Players.Count != 1)
            return;
        Player player = null;
        try { player = LocalContext.GetMe(state.Players); } catch { }
        player ??= state.Players[0];
        var buffer = new ArrayBufferWriter<byte>(8192);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("event", name);
            w.WriteStartObject("run");
            Try(() => w.WriteString("character", player.Character.Id.Entry));
            Try(() => w.WriteNumber("act", state.CurrentActIndex + 1));
            Try(() => w.WriteNumber("floor", state.TotalFloor));
            Try(() => w.WriteNumber("ascension", state.AscensionLevel));
            Try(() => w.WriteNumber("hp", player.Creature.CurrentHp));
            Try(() => w.WriteNumber("maxHp", player.Creature.MaxHp));
            Try(() => w.WriteNumber("gold", player.Gold));
            Try(() => w.WriteNumber("potionSlots", player.MaxPotionCount));
            w.WriteEndObject();
            w.WriteStartArray("deck");
            foreach (var card in player.Deck.Cards)
            {
                w.WriteStartObject();
                CardFacts(w, card);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("relics");
            foreach (var relic in player.Relics)
            {
                w.WriteStartObject();
                Relic(w, relic);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("potions");
            foreach (var potion in player.Potions)
            {
                w.WriteStartObject();
                Potion(w, potion);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            body(w);
            w.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        CombatRecorder.AdvisorState(json);
        Deliver(json);
        _shown = true;
        PatchHelper.Log($"[RunAdvisor] {name}: {json.Length} chars");
    }

    private static void CardFacts(Utf8JsonWriter w, CardModel card)
    {
        CombatRecorder.Card(w, card);
        Try(() => w.WriteString("rarity", card.Rarity.ToString()));
    }

    private static void Relic(Utf8JsonWriter w, RelicModel relic)
    {
        w.WriteString("id", relic.Id.Entry);
        Try(() => w.WriteString("name", relic.Title.GetFormattedText()));
        Try(() => w.WriteString("rarity", relic.Rarity.ToString()));
        Try(() => w.WriteString("desc", Plain(relic.DynamicDescription.GetFormattedText())));
    }

    private static void Potion(Utf8JsonWriter w, PotionModel potion)
    {
        w.WriteString("id", potion.Id.Entry);
        Try(() => w.WriteString("name", potion.Title.GetFormattedText()));
        Try(() => w.WriteString("rarity", potion.Rarity.ToString()));
        Try(() => w.WriteString("desc", Plain(potion.DynamicDescription.GetFormattedText())));
    }

    private static readonly System.Text.RegularExpressions.Regex BbCode = new(@"\[/?[^\]]*\]");

    private static string Plain(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        text = BbCode.Replace(text, "").Replace('\n', ' ').Trim();
        return text.Length > 200 ? text.Substring(0, 200) : text;
    }

    private static void Later(Node node, double seconds, int generation, Action action)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            return;
        tree.CreateTimer(seconds).Timeout += () =>
        {
            if (generation != _generation || !Enabled)
                return;
            try { action(); }
            catch (Exception exception) { Fail("send", exception); }
        };
    }

    private static void Try(Action write)
    {
        try { write(); } catch { }
    }

    private static void Deliver(string json)
    {
        if (_java == null || !GodotObject.IsInstanceValid(_java))
            _java = (GodotObject)Engine.GetSingleton("JavaClassWrapper").Call("wrap", "com.godot.game.GodotApp");
        _java.Call("sendCombatState", json);
    }

    private static void Fail(string where, Exception exception)
    {
        PatchHelper.Log($"[RunAdvisor] {where} failed: {exception.Message}"); // the game goes on; the next screen tries again
    }
}
