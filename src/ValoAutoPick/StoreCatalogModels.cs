using System.Text.Json.Serialization;

namespace ValoAutoPick;

internal sealed class ApiEnvelope<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

internal sealed class WeaponData
{
    [JsonPropertyName("displayIcon")]
    public string? DisplayIcon { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("skins")]
    public List<SkinData> Skins { get; set; } = [];
}

internal sealed class SkinData
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("displayIcon")]
    public string? DisplayIcon { get; set; }

    [JsonPropertyName("contentTierUuid")]
    public string? ContentTierUuid { get; set; }

    [JsonPropertyName("chromas")]
    public List<ChromaData> Chromas { get; set; } = [];

    [JsonPropertyName("levels")]
    public List<LevelData> Levels { get; set; } = [];
}

internal sealed class ChromaData
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("displayIcon")]
    public string? DisplayIcon { get; set; }

    [JsonPropertyName("fullRender")]
    public string? FullRender { get; set; }

    [JsonPropertyName("swatch")]
    public string? Swatch { get; set; }

    [JsonPropertyName("streamedVideo")]
    public string? StreamedVideo { get; set; }
}

internal sealed class LevelData
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("levelItem")]
    public string? LevelItem { get; set; }

    [JsonPropertyName("displayIcon")]
    public string? DisplayIcon { get; set; }

    [JsonPropertyName("streamedVideo")]
    public string? StreamedVideo { get; set; }
}

internal sealed class ContentTierData
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("highlightColor")]
    public string? HighlightColor { get; set; }
}

internal sealed class BundleData
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("displayIcon")]
    public string? DisplayIcon { get; set; }

    [JsonPropertyName("verticalPromoImage")]
    public string? VerticalPromoImage { get; set; }
}

internal class AccessoryData
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("displayIcon")]
    public string? DisplayIcon { get; set; }
}

internal sealed class BuddyData : AccessoryData
{
    [JsonPropertyName("levels")]
    public List<BuddyLevelData> Levels { get; set; } = [];
}

internal sealed class BuddyLevelData
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("charmLevel")]
    public int CharmLevel { get; set; }

    [JsonPropertyName("displayIcon")]
    public string? DisplayIcon { get; set; }
}

internal sealed record CatalogCachePayload(
    DateTimeOffset UpdatedAt,
    List<WeaponData> Weapons,
    List<ContentTierData> Tiers,
    List<BundleData> Bundles,
    List<BuddyData> Buddies,
    List<AccessoryData> PlayerCards,
    List<AccessoryData> Sprays);

internal sealed record CatalogSnapshot(
    DateTimeOffset UpdatedAt,
    List<WeaponCard> Weapons,
    IReadOnlyDictionary<string, BundleCard> Bundles,
    IReadOnlyDictionary<string, StoreItemCard> Accessories,
    int SkinCount,
    int ChromaCount,
    int VideoCount,
    bool FromCache)
{
    public bool HasWeaponImages { get; init; }
}

internal sealed record BundleCard(string Name, IReadOnlyList<string> Images);

internal sealed record StoreItemCard(string Name, string Type, IReadOnlyList<string> Images);

internal sealed record WeaponCard(
    string Name,
    string Category,
    List<SkinCard> Skins);

internal sealed record SkinCard(
    string Id,
    string Name,
    string Weapon,
    string Category,
    string? Image,
    string? WeaponImage,
    string Tier,
    string TierColor,
    List<ChromaCard> Chromas,
    List<LevelCard> Levels);

internal sealed record ChromaCard(
    string Id,
    string Name,
    string? Icon,
    string? FullRender,
    string? Swatch,
    string? Video);

internal sealed record LevelCard(
    string Id,
    string Name,
    string? Icon,
    string? Video);
