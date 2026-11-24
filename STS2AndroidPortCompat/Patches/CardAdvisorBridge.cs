using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

/// <summary>
/// Game side of the personal card-advisor overlay (separate app com.kkorii.sts2advisor).
/// When the card reward screen (or the merchant's card shelf) is shown, sends a small v1
/// JSON state (run, deck, relics, the offered cards with their on-screen rects in window
/// pixels) through GodotApp.sendAdvisorState as an explicit broadcast; "*_closed" when it
/// goes away. Android only, setting card_advisor_bridge (default on). Never throws into
/// the game: every hook is wrapped and failures only log.
/// </summary>
public static class CardAdvisorBridge
{
    public const string SettingKey = "card_advisor_bridge";

    // Reward cards slide from the row centre to their slots in 0.5 s; the merchant shelf
    // slides in over 0.7 s. The first send uses the slot the card is heading to, the
    // second re-measures once the motion has finished.
    private const double RewardSettleSeconds = 0.6;
    private const double ShopSettleSeconds = 0.8;
    private const float RewardCardSpacing = 350f;
    private const float ChooseCardSpacing = 340f;
    private const float ShopOpenY = 80f;

    // Resolved on first use (a reward/shop screen in a run), never during the startup
    // patch pass: early type/static initialisation there can abort Godot 4.5 on Android
    // ("Unreferenced static string to 0: _recognize_path"). This class has no static
    // initialisers on purpose.
    private static class Refs
    {
        internal static readonly FieldInfo RewardOptionsField = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options");
        internal static readonly FieldInfo RewardCardRowField = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_cardRow");
        internal static readonly FieldInfo ChooseCardsField = AccessTools.Field(typeof(NChooseACardSelectionScreen), "_cards");
        internal static readonly FieldInfo ChooseCardRowField = AccessTools.Field(typeof(NChooseACardSelectionScreen), "_cardRow");
        internal static readonly FieldInfo ShopSlotsContainerField = AccessTools.Field(typeof(NMerchantInventory), "_slotsContainer");
        internal static readonly MethodInfo ShopCardSlotsMethod = AccessTools.Method(typeof(NMerchantInventory), "GetCardSlots");
        internal static readonly PropertyInfo ExtraHoverTipsProperty = AccessTools.Property(typeof(CardModel), "ExtraHoverTips");
        internal static readonly FieldInfo LocFallbackField = AccessTools.Field(typeof(LocTable), "_fallback");

        internal static readonly System.Text.RegularExpressions.Regex BbCode = new(@"\[/?[^\]]*\]");

        internal static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        };
    }

    private static int _rewardGeneration;
    private static bool _rewardOpen;
    private static int _shopGeneration;
    private static bool _shopOpen;
    private static GodotObject _javaBridge;
    private static bool _javaBridgeFailed;

    public static bool Enabled => NativeFeel.IsAndroid && AndroidSettingsBridge.GetBool(SettingKey, true);

    public static void Apply(Harmony harmony)
    {
        try
        {
            if (!NativeFeel.IsAndroid)
                return;

            var self = typeof(CardAdvisorBridge);
            var reward = typeof(NCardRewardSelectionScreen);
            PatchHelper.Patch(harmony, reward, nameof(NCardRewardSelectionScreen.RefreshOptions), postfix: PatchHelper.Method(self, nameof(RewardShownPostfix)));
            PatchHelper.Patch(harmony, reward, nameof(NCardRewardSelectionScreen.AfterOverlayShown), postfix: PatchHelper.Method(self, nameof(RewardShownPostfix)));
            PatchHelper.Patch(harmony, reward, nameof(NCardRewardSelectionScreen.AfterOverlayHidden), postfix: PatchHelper.Method(self, nameof(RewardGonePostfix)));
            PatchHelper.Patch(harmony, reward, nameof(NCardRewardSelectionScreen.AfterOverlayClosed), prefix: PatchHelper.Method(self, nameof(RewardGonePostfix)));
            PatchHelper.Patch(harmony, reward, "_ExitTree", prefix: PatchHelper.Method(self, nameof(RewardGonePostfix)));

            // "Choose a card" screen (Neow's starting card pick, events, some card effects): same
            // row of cards, reported as a card_reward too.
            var choose = typeof(NChooseACardSelectionScreen);
            PatchHelper.Patch(harmony, choose, nameof(NChooseACardSelectionScreen.AfterOverlayOpened), postfix: PatchHelper.Method(self, nameof(RewardShownPostfix)));
            PatchHelper.Patch(harmony, choose, nameof(NChooseACardSelectionScreen.AfterOverlayShown), postfix: PatchHelper.Method(self, nameof(RewardShownPostfix)));
            PatchHelper.Patch(harmony, choose, nameof(NChooseACardSelectionScreen.AfterOverlayHidden), postfix: PatchHelper.Method(self, nameof(RewardGonePostfix)));
            PatchHelper.Patch(harmony, choose, nameof(NChooseACardSelectionScreen.AfterOverlayClosed), prefix: PatchHelper.Method(self, nameof(RewardGonePostfix)));
            PatchHelper.Patch(harmony, choose, "_ExitTree", prefix: PatchHelper.Method(self, nameof(RewardGonePostfix)));

            var shop = typeof(NMerchantInventory);
            PatchHelper.Patch(harmony, shop, nameof(NMerchantInventory.Open), postfix: PatchHelper.Method(self, nameof(ShopOpenPostfix)));
            PatchHelper.Patch(harmony, shop, "OnPurchaseCompleted", postfix: PatchHelper.Method(self, nameof(ShopChangedPostfix)));
            PatchHelper.Patch(harmony, shop, "Close", postfix: PatchHelper.Method(self, nameof(ShopGonePostfix)));
            PatchHelper.Patch(harmony, shop, "_ExitTree", prefix: PatchHelper.Method(self, nameof(ShopGonePostfix)));
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[CardAdvisor] patching failed: {exception.Message}");
        }
    }

    // ---- card reward -------------------------------------------------------------------

    public static void RewardShownPostfix(Control __instance)
    {
        try
        {
            if (!Enabled || __instance == null)
                return;
            if (__instance is NChooseACardSelectionScreen && CombatManager.Instance.IsInProgress)
                return; // a card effect in combat, not a reward: CombatStateBridge sends it as a choice
            var generation = ++_rewardGeneration;
            var screen = __instance;
            Callable.From(() => SendRewardIfCurrent(screen, generation, predictSlots: true)).CallDeferred();
            var tree = screen.GetTree();
            if (tree != null)
                tree.CreateTimer(RewardSettleSeconds).Timeout += () => SendRewardIfCurrent(screen, generation, predictSlots: false);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[CardAdvisor] reward schedule failed: {exception.Message}");
        }
    }

    public static void RewardGonePostfix()
    {
        try
        {
            _rewardGeneration++;
            if (!_rewardOpen)
                return;
            _rewardOpen = false;
            if (Enabled)
                SendClosed("card_reward_closed");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[CardAdvisor] reward close failed: {exception.Message}");
        }
    }

    private static void SendRewardIfCurrent(Control screen, int generation, bool predictSlots)
    {
        try
        {
            if (generation != _rewardGeneration || !Enabled || !GodotObject.IsInstanceValid(screen)
                || !screen.IsInsideTree() || !screen.IsVisibleInTree())
                return;
            if (!TryGetChoice(screen, out var options, out var cardRow, out var spacing) || options.Count == 0)
                return;

            var holders = new List<NCardHolder>();
            if (cardRow != null && GodotObject.IsInstanceValid(cardRow))
            {
                foreach (var child in cardRow.GetChildren())
                {
                    if (child is NGridCardHolder holder && GodotObject.IsInstanceValid(holder) && !holder.IsQueuedForDeletion())
                        holders.Add(holder);
                }
            }

            var offers = new List<Offer>(options.Count);
            for (var i = 0; i < options.Count; i++)
            {
                var card = options[i];
                if (card == null)
                    continue;
                var holder = holders.FirstOrDefault(h => h.CardModel == card);
                Vector2? canvasShift = null;
                if (predictSlots && holder != null && cardRow != null)
                {
                    // Where the screen sends this holder: slots `spacing` px apart around x = 0.
                    var target = new Vector2((i - (options.Count - 1) * 0.5f) * spacing, 0f);
                    canvasShift = cardRow.GetGlobalTransformWithCanvas().BasisXform(target - holder.Position);
                }
                offers.Add(new Offer(card, holder?.Hitbox, canvasShift, null, false));
            }

            Send("card_reward", offers, screen.GetViewport());
            _rewardOpen = true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[CardAdvisor] reward send failed: {exception.Message}");
        }
    }

    // Offered cards, their row and slot spacing for the reward screen or the "choose a card" screen.
    private static bool TryGetChoice(Control screen, out List<CardModel> cards, out Control cardRow, out float spacing)
    {
        cards = null;
        cardRow = null;
        spacing = RewardCardSpacing;
        if (screen is NCardRewardSelectionScreen)
        {
            if (Refs.RewardOptionsField?.GetValue(screen) is not IReadOnlyList<CardCreationResult> options)
                return false;
            cards = options.Select(o => o?.Card).ToList();
            cardRow = Refs.RewardCardRowField?.GetValue(screen) as Control;
            return true;
        }
        if (screen is NChooseACardSelectionScreen)
        {
            if (Refs.ChooseCardsField?.GetValue(screen) is not IReadOnlyList<CardModel> list)
                return false;
            cards = list.ToList();
            cardRow = Refs.ChooseCardRowField?.GetValue(screen) as Control;
            spacing = ChooseCardSpacing;
            return true;
        }
        return false;
    }

    // ---- merchant ----------------------------------------------------------------------

    public static void ShopOpenPostfix(NMerchantInventory __instance) => ScheduleShop(__instance, ShopSettleSeconds, predict: true);

    public static void ShopChangedPostfix(NMerchantInventory __instance) => ScheduleShop(__instance, 0.05, predict: false);

    private static void ScheduleShop(NMerchantInventory inventory, double settleSeconds, bool predict)
    {
        try
        {
            if (!Enabled || inventory == null)
                return;
            var generation = ++_shopGeneration;
            if (predict)
                Callable.From(() => SendShopIfCurrent(inventory, generation, predictSlots: true)).CallDeferred();
            var tree = inventory.GetTree();
            if (tree != null)
                tree.CreateTimer(settleSeconds).Timeout += () => SendShopIfCurrent(inventory, generation, predictSlots: false);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[CardAdvisor] shop schedule failed: {exception.Message}");
        }
    }

    public static void ShopGonePostfix()
    {
        try
        {
            _shopGeneration++;
            if (!_shopOpen)
                return;
            _shopOpen = false;
            if (Enabled)
                SendClosed("shop_closed");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[CardAdvisor] shop close failed: {exception.Message}");
        }
    }

    private static void SendShopIfCurrent(NMerchantInventory inventory, int generation, bool predictSlots)
    {
        try
        {
            if (generation != _shopGeneration || !Enabled || !GodotObject.IsInstanceValid(inventory)
                || !inventory.IsInsideTree() || !inventory.IsOpen)
                return;
            if (Refs.ShopCardSlotsMethod?.Invoke(inventory, null) is not IEnumerable slots)
                return;

            Vector2? canvasShift = null;
            if (predictSlots && Refs.ShopSlotsContainerField?.GetValue(inventory) is Control container
                && container.GetParent() is CanvasItem containerParent)
            {
                // The shelf slides down to y = 80 when the inventory opens.
                canvasShift = containerParent.GetGlobalTransformWithCanvas().BasisXform(new Vector2(0f, ShopOpenY - container.Position.Y));
            }

            var offers = new List<Offer>();
            foreach (var item in slots)
            {
                if (item is not NMerchantCard slot || !GodotObject.IsInstanceValid(slot) || !slot.Visible)
                    continue;
                if (slot.Entry is not MerchantCardEntry entry || !entry.IsStocked)
                    continue;
                var card = entry.CreationResult?.Card;
                if (card == null)
                    continue;
                offers.Add(new Offer(card, slot.Hitbox, canvasShift, entry.Cost, entry.IsOnSale));
            }

            Send("shop", offers, inventory.GetViewport());
            _shopOpen = true;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[CardAdvisor] shop send failed: {exception.Message}");
        }
    }

    // ---- JSON --------------------------------------------------------------------------

    private readonly struct Offer
    {
        public readonly CardModel Card;
        public readonly Control Hitbox;
        public readonly Vector2? CanvasShift;
        public readonly int? Price;
        public readonly bool OnSale;

        public Offer(CardModel card, Control hitbox, Vector2? canvasShift, int? price, bool onSale)
        {
            Card = card;
            Hitbox = hitbox;
            CanvasShift = canvasShift;
            Price = price;
            OnSale = onSale;
        }
    }

    private static void SendClosed(string eventName)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer, Refs.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("event", eventName);
            writer.WriteNumber("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }
        Deliver(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void Send(string eventName, List<Offer> offers, Viewport viewport)
    {
        var watch = Stopwatch.StartNew();
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, Refs.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("event", eventName);
            writer.WriteNumber("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var window = DisplayServer.WindowGetSize();
            writer.WriteStartObject("screen");
            writer.WriteNumber("w", window.X);
            writer.WriteNumber("h", window.Y);
            writer.WriteEndObject();
            WriteString(writer, "lang", SafeLanguage());
            WriteRunDeckRelics(writer);
            writer.WriteStartArray("options");
            var screenTransform = SafeScreenTransform(viewport);
            for (var i = 0; i < offers.Count; i++)
                WriteOffer(writer, offers[i], i, screenTransform);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        CombatRecorder.AdvisorState(json);
        var buildMs = watch.Elapsed.TotalMilliseconds;
        Deliver(json);
        PatchHelper.Log($"[CardAdvisor] {eventName}: {offers.Count} options, {json.Length} chars, build {buildMs:0.0} ms, total {watch.Elapsed.TotalMilliseconds:0.0} ms");
    }

    private static void WriteRunDeckRelics(Utf8JsonWriter writer)
    {
        RunState state = null;
        Player player = null;
        try
        {
            state = RunManager.Instance?.DebugOnlyGetState();
            if (state != null)
            {
                try { player = LocalContext.GetMe(state.Players); } catch { }
                player ??= state.Players.FirstOrDefault();
            }
        }
        catch
        {
        }

        writer.WriteStartObject("run");
        if (state != null)
        {
            TryWrite(() => WriteString(writer, "character", player?.Character?.Id?.Entry));
            TryWrite(() => writer.WriteNumber("act", state.CurrentActIndex + 1));
            TryWrite(() => writer.WriteNumber("floor", state.TotalFloor));
            TryWrite(() => writer.WriteNumber("ascension", state.AscensionLevel));
            if (player != null)
            {
                TryWrite(() => writer.WriteNumber("hp", player.Creature.CurrentHp));
                TryWrite(() => writer.WriteNumber("maxHp", player.Creature.MaxHp));
                TryWrite(() => writer.WriteNumber("gold", player.Gold));
            }
            TryWrite(() => WriteString(writer, "seed", state.Rng?.StringSeed));
        }
        writer.WriteEndObject();

        writer.WriteStartArray("deck");
        if (player != null)
        {
            try
            {
                foreach (var card in player.Deck.Cards)
                {
                    if (card == null)
                        continue;
                    // Same facts as a reward option (minus rect/price), so the advisor can judge
                    // deck synergy without having to have seen each card as a reward first.
                    writer.WriteStartObject();
                    writer.WriteString("id", card.Id.Entry);
                    writer.WriteBoolean("upgraded", card.IsUpgraded);
                    WriteCardFacts(writer, card);
                    writer.WriteEndObject();
                }
            }
            catch
            {
            }
        }
        writer.WriteEndArray();

        writer.WriteStartArray("relics");
        if (player != null)
        {
            try
            {
                foreach (var relic in player.Relics)
                {
                    if (relic != null)
                        writer.WriteStringValue(relic.Id.Entry);
                }
            }
            catch
            {
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteOffer(Utf8JsonWriter writer, Offer offer, int index, Transform2D? screenTransform)
    {
        var card = offer.Card;
        writer.WriteStartObject();
        writer.WriteNumber("index", index);
        TryWrite(() => writer.WriteString("id", card.Id.Entry));
        TryWrite(() => writer.WriteBoolean("upgraded", card.IsUpgraded));
        TryWrite(() => WriteString(writer, "nameEn", EnglishTitle(card)));
        if (offer.Price.HasValue)
        {
            writer.WriteNumber("price", offer.Price.Value);
            writer.WriteBoolean("onSale", offer.OnSale);
        }
        WriteCardFacts(writer, card);

        if (TryScreenRect(offer, screenTransform, out var rect))
        {
            writer.WriteStartObject("rect");
            writer.WriteNumber("x", Mathf.RoundToInt(rect.Position.X));
            writer.WriteNumber("y", Mathf.RoundToInt(rect.Position.Y));
            writer.WriteNumber("w", Mathf.RoundToInt(rect.Size.X));
            writer.WriteNumber("h", Mathf.RoundToInt(rect.Size.Y));
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("rect");
        }
        writer.WriteEndObject();
    }

    // nameKo, rarity, type, cost (+costX), starCost, tags: shared by reward options and deck cards.
    private static void WriteCardFacts(Utf8JsonWriter writer, CardModel card)
    {
        TryWrite(() => WriteString(writer, "nameKo", card.TitleLocString.GetFormattedText()));
        // Rules text in the game language, BBCode/icons stripped: lets the advisor's AI judge
        // what the card actually does instead of guessing from its name.
        TryWrite(() => WriteString(writer, "desc", PlainText(card.GetDescriptionForPile(PileType.None))));
        TryWrite(() => writer.WriteString("rarity", card.Rarity.ToString()));
        TryWrite(() => writer.WriteString("type", card.Type.ToString()));
        TryWrite(() =>
        {
            var cost = card.EnergyCost;
            writer.WriteNumber("cost", cost.CostsX ? -1 : cost.GetWithModifiers(CostModifiers.None));
            if (cost.CostsX)
                writer.WriteBoolean("costX", true);
        });
        TryWrite(() =>
        {
            var stars = card.CurrentStarCost;
            if (stars >= 0 || card.HasStarCostX)
                writer.WriteNumber("starCost", card.HasStarCostX ? -1 : stars);
        });
        writer.WriteStartArray("tags");
        try
        {
            foreach (var tag in DeriveTags(card))
                writer.WriteStringValue(tag);
        }
        catch
        {
        }
        writer.WriteEndArray();
        // Numbers for the combat advisor (same shape as the combat snapshot).
        TryWrite(() => writer.WriteString("targetType", card.TargetType.ToString()));
        TryWrite(() => { if (card.CurrentStarCost >= 0) writer.WriteNumber("stars", card.CurrentStarCost); });
        TryWrite(() => writer.WriteNumber("up", card.CurrentUpgradeLevel));
        TryWrite(() =>
        {
            if (card.Enchantment == null)
                return;
            writer.WriteString("enchantment", card.Enchantment.Id.Entry);
            writer.WriteNumber("enchantmentAmount", card.Enchantment.Amount);
        });
        writer.WriteStartArray("keywords");
        try { foreach (var keyword in card.Keywords) writer.WriteStringValue(keyword.ToString()); } catch { }
        writer.WriteEndArray();
        writer.WriteStartObject("vars");
        try { foreach (var pair in card.DynamicVars) writer.WriteNumber(pair.Key, pair.Value.BaseValue); } catch { }
        writer.WriteEndObject();
    }

    private static string PlainText(string bbcode)
    {
        if (string.IsNullOrEmpty(bbcode))
            return null;
        var text = Refs.BbCode.Replace(bbcode, "").Replace('\n', ' ').Trim();
        while (text.Contains("  "))
            text = text.Replace("  ", " ");
        return text.Length > 200 ? text.Substring(0, 200) : text;
    }

    private static void TryWrite(Action write)
    {
        try
        {
            write();
        }
        catch
        {
        }
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string value)
    {
        if (value == null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    // ---- geometry ----------------------------------------------------------------------

    private static Transform2D? SafeScreenTransform(Viewport viewport)
    {
        try
        {
            if (viewport != null && GodotObject.IsInstanceValid(viewport))
                return viewport.GetScreenTransform();
        }
        catch
        {
        }
        return null;
    }

    // Hitbox rect in window pixels: canvas transform (with the optional "where it is
    // heading" shift), then the viewport's stretch transform to the window. On Android the
    // game window is the full immersive display, so these are physical screen pixels.
    private static bool TryScreenRect(Offer offer, Transform2D? screenTransform, out Rect2 rect)
    {
        rect = default;
        try
        {
            var hitbox = offer.Hitbox;
            if (hitbox == null || !GodotObject.IsInstanceValid(hitbox) || !hitbox.IsInsideTree())
                return false;
            var canvas = hitbox.GetGlobalTransformWithCanvas();
            if (offer.CanvasShift.HasValue)
                canvas.Origin += offer.CanvasShift.Value;
            var toScreen = screenTransform ?? Transform2D.Identity;
            var full = toScreen * canvas;
            var size = hitbox.Size;
            var a = full * Vector2.Zero;
            var b = full * new Vector2(size.X, 0f);
            var c = full * new Vector2(0f, size.Y);
            var d = full * size;
            var minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
            var minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
            var maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            var maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
            if (maxX - minX < 1f || maxY - minY < 1f)
                return false;
            rect = new Rect2(minX, minY, maxX - minX, maxY - minY);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- localisation ------------------------------------------------------------------

    private static string SafeLanguage()
    {
        try
        {
            return LocManager.Instance?.Language;
        }
        catch
        {
            return null;
        }
    }

    // Non-English tables are built with the English table of the same name as fallback, so
    // the English title is a dictionary lookup, not a second localisation load.
    private static string EnglishTitle(CardModel card)
    {
        var manager = LocManager.Instance;
        if (manager == null)
            return null;
        var key = card.Id.Entry + ".title";
        var table = manager.GetTable("cards");
        if (table == null)
            return null;
        if (!string.Equals(manager.Language, "eng", StringComparison.OrdinalIgnoreCase))
            table = Refs.LocFallbackField?.GetValue(table) as LocTable;
        if (table == null)
            return null;
        try
        {
            return table.GetRawText(key);
        }
        catch
        {
            return null;
        }
    }

    // ---- tags --------------------------------------------------------------------------

    private static IEnumerable<string> DeriveTags(CardModel card)
    {
        var tags = new List<string>(8);
        void Add(string tag)
        {
            if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag))
                tags.Add(tag);
        }

        try
        {
            foreach (var keyword in card.Keywords)
            {
                switch (keyword)
                {
                    case CardKeyword.Exhaust: Add("exhaust"); break;
                    case CardKeyword.Ethereal: Add("ethereal"); break;
                    case CardKeyword.Innate: Add("innate"); break;
                    case CardKeyword.Retain: Add("retain"); break;
                    case CardKeyword.Sly: Add("sly"); break;
                }
            }
        }
        catch
        {
        }

        try
        {
            foreach (var tag in card.Tags)
            {
                switch (tag)
                {
                    case CardTag.Strike: Add("strike"); break;
                    case CardTag.Shiv: Add("shiv"); break;
                    case CardTag.OstyAttack: Add("osty"); break;
                }
            }
        }
        catch
        {
        }

        var generatesCards = false;
        try
        {
            if (Refs.ExtraHoverTipsProperty?.GetValue(card) is IEnumerable<IHoverTip> tips)
            {
                foreach (var tip in tips)
                {
                    try
                    {
                        var model = tip.CanonicalModel;
                        var category = model?.Id?.Category;
                        var entry = model?.Id?.Entry;
                        if (category == null && tip.Id is { } tipId && tipId.IndexOf(' ') < 0)
                        {
                            // Model-backed tips use the model id ("POWER.STRENGTH_POWER").
                            var dot = tipId.IndexOf('.');
                            if (dot > 0 && dot == tipId.LastIndexOf('.'))
                            {
                                category = tipId.Substring(0, dot);
                                entry = tipId.Substring(dot + 1);
                            }
                        }
                        if (category == "POWER" && entry != null)
                        {
                            Add(PowerTag(entry));
                        }
                        else if (category == "ORB")
                        {
                            Add("orb");
                        }
                        else if (category == "CARD" && entry != null)
                        {
                            generatesCards = true;
                            if (entry == "SHIV")
                                Add("shiv");
                        }
                        else
                        {
                            var id = tip.Id ?? string.Empty;
                            if (id.Contains("CHANNELING", StringComparison.OrdinalIgnoreCase) || id.Contains("EVOKE", StringComparison.OrdinalIgnoreCase))
                                Add("orb");
                            else if (id.Contains("FORGE", StringComparison.OrdinalIgnoreCase))
                                Add("forge");
                            else if (id.Contains("SUMMON", StringComparison.OrdinalIgnoreCase))
                                Add("summon");
                            else if (id.Contains("ENERGY", StringComparison.OrdinalIgnoreCase))
                                Add("energy");
                            else if (id.Contains("BLOCK", StringComparison.OrdinalIgnoreCase))
                                Add("block");
                            else if (id.Contains("TRANSFORM", StringComparison.OrdinalIgnoreCase))
                                Add("transform");
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            foreach (var name in card.DynamicVars.Keys)
            {
                switch (name)
                {
                    case "Block":
                    case "CalculatedBlock":
                        Add("block");
                        break;
                    case "Cards":
                        if (!generatesCards)
                            Add("draw");
                        break;
                    case "Energy": Add("energy"); break;
                    case "Stars": Add("star"); break;
                    case "Forge": Add("forge"); break;
                    case "Summon": Add("summon"); break;
                    case "OstyDamage": Add("osty"); break;
                    case "Heal": Add("heal"); break;
                    case "HpLoss": Add("hp_loss"); break;
                    case "MaxHp": Add("max_hp"); break;
                    case "Gold": Add("gold"); break;
                    case "Repeat": Add("multi_hit"); break;
                    default:
                        if (name.EndsWith("Power", StringComparison.Ordinal) && name.Length > 5)
                            Add(PowerTag(name));
                        break;
                }
            }
        }
        catch
        {
        }

        try
        {
            if (card.GainsBlock)
                Add("block");
            if (card.OrbEvokeType != OrbEvokeType.None)
                Add("orb");
            if (card.TargetType == TargetType.AllEnemies)
                Add("aoe");
            if (card.Type == CardType.Power)
                Add("scaling");
        }
        catch
        {
        }
        return tags;
    }

    // "StrengthPower" / "STRENGTH_POWER" -> "strength"; "VulnerablePower" -> "vulnerable".
    private static string PowerTag(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch == '_')
            {
                if (builder.Length > 0 && builder[builder.Length - 1] != '_')
                    builder.Append('_');
                continue;
            }
            if (char.IsUpper(ch) && i > 0 && char.IsLower(name[i - 1]))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(ch));
        }
        var text = builder.ToString();
        if (text.EndsWith("_power", StringComparison.Ordinal))
            text = text.Substring(0, text.Length - "_power".Length);
        else if (text.EndsWith("power", StringComparison.Ordinal) && text.Length > 5)
            text = text.Substring(0, text.Length - "power".Length);
        return text.Trim('_');
    }

    // ---- Java bridge -------------------------------------------------------------------

    private static void Deliver(string json)
    {
        if (_javaBridgeFailed)
            return;
        try
        {
            if (_javaBridge == null || !GodotObject.IsInstanceValid(_javaBridge))
                _javaBridge = (GodotObject)Engine.GetSingleton("JavaClassWrapper").Call("wrap", "com.godot.game.GodotApp");
            _javaBridge.Call("sendAdvisorState", json);
        }
        catch (Exception exception)
        {
            // An APK without GodotApp.sendAdvisorState: stop trying for this session.
            _javaBridgeFailed = true;
            PatchHelper.Log($"[CardAdvisor] Java bridge unavailable: {exception.Message}");
        }
    }
}
