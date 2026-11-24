using System.Collections.ObjectModel;
using System.Text.Json;

namespace ValoAutoPick;

// Immutable view of a VALORANT storefront payload (store/v3/storefront).
// Only identifiers, prices and timers are kept; display data is resolved elsewhere.

public sealed record Storefront(
    StorefrontOfferSection DailySkins,
    StorefrontOfferSection Accessories,
    StorefrontBundleSection FeaturedBundles,
    StorefrontOfferSection? NightMarket)
{
    public bool HasNightMarket => NightMarket is not null;
}

public sealed record StorefrontOfferSection(IReadOnlyList<StorefrontOffer> Offers, long RemainingSeconds);

// Cost is the offer's base price. DiscountedCost / DiscountPercent are only set when the payload carries them
// (night market). BonusOfferId is the night-market specific offer id; OfferId is always the inner Offer.OfferID.
public sealed record StorefrontOffer(
    string OfferId,
    string ItemId,
    string ItemTypeId,
    IReadOnlyDictionary<string, long> Cost,
    IReadOnlyDictionary<string, long>? DiscountedCost = null,
    double? DiscountPercent = null,
    string? BonusOfferId = null);

public sealed record StorefrontBundleSection(IReadOnlyList<StorefrontBundle> Bundles, long RemainingSeconds);

public sealed record StorefrontBundle(
    string BundleId,
    string DataAssetId,
    IReadOnlyList<StorefrontBundleItem> Items,
    IReadOnlyDictionary<string, long>? TotalBaseCost,
    IReadOnlyDictionary<string, long>? TotalDiscountedCost,
    double? TotalDiscountPercent,
    long RemainingSeconds);

// OfferId is null when the bundle has no ItemOffers entry for this item (e.g. wholesale-only bundles).
public sealed record StorefrontBundleItem(
    string? OfferId,
    string ItemId,
    string ItemTypeId,
    long Amount,
    IReadOnlyDictionary<string, long> Cost,
    IReadOnlyDictionary<string, long>? DiscountedCost,
    double? DiscountPercent);

public static class StorefrontParser
{
    private const string RootPath = "$";

    public static Storefront Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        // ArgumentException covers strings with invalid UTF-16 that cannot be transcoded for the reader.
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Storefront payload is not valid JSON.", e);
        }
    }

    // The returned records copy every value out of the element, so the owning JsonDocument may be disposed afterwards.
    public static Storefront Parse(JsonElement root)
    {
        RequireKind(root, JsonValueKind.Object, RootPath);
        return new Storefront(
            ReadDailySkins(root),
            ReadAccessories(root),
            ReadFeaturedBundles(root),
            ReadNightMarket(root));
    }

    private static StorefrontOfferSection ReadDailySkins(JsonElement root)
    {
        const string path = RootPath + ".SkinsPanelLayout";
        var panel = RequireProperty(root, "SkinsPanelLayout", RootPath, JsonValueKind.Object);
        var offers = ReadArray(panel, "SingleItemStoreOffers", path, ReadOffer);
        return new StorefrontOfferSection(offers, ReadRemainingSeconds(panel, "SingleItemOffersRemainingDurationInSeconds", path));
    }

    private static StorefrontOfferSection ReadAccessories(JsonElement root)
    {
        const string path = RootPath + ".AccessoryStore";
        if (OptionalProperty(root, "AccessoryStore", RootPath, JsonValueKind.Object) is not { } store)
            return new StorefrontOfferSection([], 0);
        var offers = ReadArray(store, "AccessoryStoreOffers", path, (entry, entryPath) =>
        {
            RequireKind(entry, JsonValueKind.Object, entryPath);
            return ReadOffer(RequireProperty(entry, "Offer", entryPath, JsonValueKind.Object), Join(entryPath, "Offer"));
        });
        return new StorefrontOfferSection(offers, ReadRemainingSeconds(store, "AccessoryStoreRemainingDurationInSeconds", path));
    }

    private static StorefrontBundleSection ReadFeaturedBundles(JsonElement root)
    {
        const string path = RootPath + ".FeaturedBundle";
        var featured = RequireProperty(root, "FeaturedBundle", RootPath, JsonValueKind.Object);
        var bundles = ReadArray(featured, "Bundles", path, ReadBundle);
        return new StorefrontBundleSection(bundles, ReadRemainingSeconds(featured, "BundleRemainingDurationInSeconds", path));
    }

    // Absent or null BonusStore means no night market. A present BonusStore must be well formed.
    private static StorefrontOfferSection? ReadNightMarket(JsonElement root)
    {
        const string path = RootPath + ".BonusStore";
        if (OptionalProperty(root, "BonusStore", RootPath, JsonValueKind.Object) is not { } bonus) return null;
        var offers = ReadArray(bonus, "BonusStoreOffers", path, (entry, entryPath) =>
        {
            RequireKind(entry, JsonValueKind.Object, entryPath);
            var bonusOfferId = RequireUuid(entry, "BonusOfferID", entryPath);
            var offer = ReadOffer(RequireProperty(entry, "Offer", entryPath, JsonValueKind.Object), Join(entryPath, "Offer"));
            return offer with
            {
                DiscountedCost = OptionalCostMap(entry, "DiscountCosts", entryPath),
                DiscountPercent = OptionalPercent(entry, "DiscountPercent", entryPath),
                BonusOfferId = bonusOfferId
            };
        });
        return new StorefrontOfferSection(offers, ReadRemainingSeconds(bonus, "BonusStoreRemainingDurationInSeconds", path));
    }

    private static StorefrontBundle ReadBundle(JsonElement bundle, string path)
    {
        RequireKind(bundle, JsonValueKind.Object, path);
        var bundleId = RequireUuid(bundle, "ID", path);
        var dataAssetId = RequireUuid(bundle, "DataAssetID", path);
        var offerIdsByItem = ReadBundleItemOfferIds(bundle, path);
        var items = ReadArray(bundle, "Items", path, (entry, entryPath) => ReadBundleItem(entry, entryPath, offerIdsByItem));
        return new StorefrontBundle(
            bundleId,
            dataAssetId,
            items,
            OptionalCostMap(bundle, "TotalBaseCost", path),
            OptionalCostMap(bundle, "TotalDiscountedCost", path),
            OptionalPercent(bundle, "TotalDiscountPercent", path),
            ReadRemainingSeconds(bundle, "DurationRemainingInSeconds", path));
    }

    private static StorefrontBundleItem ReadBundleItem(JsonElement entry, string path, IReadOnlyDictionary<string, string> offerIdsByItem)
    {
        RequireKind(entry, JsonValueKind.Object, path);
        var itemPath = Join(path, "Item");
        var item = RequireProperty(entry, "Item", path, JsonValueKind.Object);
        var itemId = RequireUuid(item, "ItemID", itemPath);
        var itemTypeId = RequireUuid(item, "ItemTypeID", itemPath);
        var amount = 1L;
        if (OptionalProperty(item, "Amount", itemPath, JsonValueKind.Number) is { } amountValue)
        {
            amount = ReadInteger(amountValue, Join(itemPath, "Amount"));
            if (amount < 1) throw Malformed(Join(itemPath, "Amount"), "must be at least 1");
        }

        var currencyId = RequireUuid(entry, "CurrencyID", path);
        var basePrice = ReadNonNegativeInteger(RequireProperty(entry, "BasePrice", path, JsonValueKind.Number), Join(path, "BasePrice"));
        IReadOnlyDictionary<string, long>? discountedCost = null;
        if (OptionalProperty(entry, "DiscountedPrice", path, JsonValueKind.Number) is { } discounted)
            discountedCost = SingleCost(currencyId, ReadNonNegativeInteger(discounted, Join(path, "DiscountedPrice")));

        return new StorefrontBundleItem(
            offerIdsByItem.TryGetValue(itemId, out var offerId) ? offerId : null,
            itemId,
            itemTypeId,
            amount,
            SingleCost(currencyId, basePrice),
            discountedCost,
            OptionalPercent(entry, "DiscountPercent", path));
    }

    // ItemOffers may be null or absent; when present each entry links an item (via its first reward) to an offer id.
    private static IReadOnlyDictionary<string, string> ReadBundleItemOfferIds(JsonElement bundle, string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (OptionalProperty(bundle, "ItemOffers", path, JsonValueKind.Array) is not { } itemOffers) return map;
        var arrayPath = Join(path, "ItemOffers");
        var index = 0;
        foreach (var entry in itemOffers.EnumerateArray())
        {
            var entryPath = $"{arrayPath}[{index++}]";
            RequireKind(entry, JsonValueKind.Object, entryPath);
            var bundleItemOfferId = RequireUuid(entry, "BundleItemOfferID", entryPath);
            var offer = ReadOffer(RequireProperty(entry, "Offer", entryPath, JsonValueKind.Object), Join(entryPath, "Offer"));
            map.TryAdd(offer.ItemId, bundleItemOfferId);
        }
        return map;
    }

    // Shared Offer shape: { OfferID, Cost: { currencyId: amount }, Rewards: [ { ItemTypeID, ItemID, ... } ] }.
    private static StorefrontOffer ReadOffer(JsonElement offer, string path)
    {
        RequireKind(offer, JsonValueKind.Object, path);
        var offerId = RequireUuid(offer, "OfferID", path);
        var cost = ReadCostMap(RequireProperty(offer, "Cost", path, JsonValueKind.Object), Join(path, "Cost"));
        var rewardsPath = Join(path, "Rewards");
        var rewards = RequireProperty(offer, "Rewards", path, JsonValueKind.Array);
        if (rewards.GetArrayLength() == 0) throw Malformed(rewardsPath, "must contain at least one reward");
        var rewardPath = rewardsPath + "[0]";
        var reward = rewards[0];
        RequireKind(reward, JsonValueKind.Object, rewardPath);
        return new StorefrontOffer(
            offerId,
            RequireUuid(reward, "ItemID", rewardPath),
            RequireUuid(reward, "ItemTypeID", rewardPath),
            cost);
    }

    private static IReadOnlyList<T> ReadArray<T>(JsonElement parent, string name, string parentPath, Func<JsonElement, string, T> read)
    {
        var arrayPath = Join(parentPath, name);
        if (OptionalProperty(parent, name, parentPath, JsonValueKind.Array) is not { } array) return Array.Empty<T>();
        var result = new List<T>(array.GetArrayLength());
        var index = 0;
        foreach (var entry in array.EnumerateArray()) result.Add(read(entry, $"{arrayPath}[{index++}]"));
        return result.AsReadOnly();
    }

    private static IReadOnlyDictionary<string, long> ReadCostMap(JsonElement value, string path)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            var entryPath = $"{path}[\"{property.Name}\"]";
            if (!Guid.TryParse(property.Name, out _)) throw Malformed(entryPath, "currency key must be a UUID");
            RequireKind(property.Value, JsonValueKind.Number, entryPath);
            if (!map.TryAdd(property.Name, ReadNonNegativeInteger(property.Value, entryPath))) throw Malformed(entryPath, "is duplicated");
        }
        return new ReadOnlyDictionary<string, long>(map);
    }

    private static IReadOnlyDictionary<string, long>? OptionalCostMap(JsonElement parent, string name, string parentPath) =>
        OptionalProperty(parent, name, parentPath, JsonValueKind.Object) is { } value ? ReadCostMap(value, Join(parentPath, name)) : null;

    private static IReadOnlyDictionary<string, long> SingleCost(string currencyId, long amount) =>
        new ReadOnlyDictionary<string, long>(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [currencyId] = amount });

    // Kept as reported: night-market offers use whole percents, bundle entries may use fractions.
    private static double? OptionalPercent(JsonElement parent, string name, string parentPath)
    {
        if (OptionalProperty(parent, name, parentPath, JsonValueKind.Number) is not { } value) return null;
        var path = Join(parentPath, name);
        if (!value.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent < 0) throw Malformed(path, "must be a finite non-negative number");
        return percent;
    }

    // Negative timers are clamped to zero rather than rejected; they only mean "already expired".
    private static long ReadRemainingSeconds(JsonElement parent, string name, string parentPath) =>
        Math.Max(0, ReadInteger(RequireProperty(parent, name, parentPath, JsonValueKind.Number), Join(parentPath, name)));

    private static long ReadNonNegativeInteger(JsonElement value, string path)
    {
        var number = ReadInteger(value, path);
        if (number < 0) throw Malformed(path, "must not be negative");
        return number;
    }

    private static long ReadInteger(JsonElement value, string path)
    {
        if (value.TryGetInt64(out var integer)) return integer;
        // Accept integral values written with a fraction or exponent (e.g. 1775.0) but nothing lossy.
        if (value.TryGetDouble(out var real) && double.IsFinite(real) && Math.Floor(real) == real && real >= long.MinValue && real < long.MaxValue)
            return (long)real;
        throw Malformed(path, "must be an integer");
    }

    private static string RequireUuid(JsonElement parent, string name, string parentPath)
    {
        var path = Join(parentPath, name);
        var text = RequireProperty(parent, name, parentPath, JsonValueKind.String).GetString() ?? throw Malformed(path, "is missing");
        if (!Guid.TryParse(text, out _)) throw Malformed(path, "must be a UUID");
        return text;
    }

    private static JsonElement RequireProperty(JsonElement parent, string name, string parentPath, JsonValueKind kind)
    {
        var path = Join(parentPath, name);
        if (!parent.TryGetProperty(name, out var value)) throw Malformed(path, "is missing");
        RequireKind(value, kind, path);
        return value;
    }

    private static JsonElement? OptionalProperty(JsonElement parent, string name, string parentPath, JsonValueKind kind)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        RequireKind(value, kind, Join(parentPath, name));
        return value;
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind, string path)
    {
        if (value.ValueKind != kind) throw Malformed(path, $"must be {Describe(kind)} but was {Describe(value.ValueKind)}");
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "missing"
    };

    private static string Join(string parentPath, string name) => $"{parentPath}.{name}";

    private static InvalidDataException Malformed(string path, string problem) => new($"Storefront payload: {path} {problem}.");
}
