using System.Reflection;

namespace ValoAutoPick;

internal static class RankImages
{
    private static readonly Dictionary<int, Image> Cache = [];

    internal static Image? Get(int? tier)
    {
        if (tier is 1 or 2) tier = 0;
        if (tier is not (0 or >= 3 and <= 27)) return null;
        var value = tier.Value;
        if (Cache.TryGetValue(value, out var cached)) return cached;
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"ValoAutoPick.Assets.Ranks.{value}.png");
            if (stream is null) return null;
            using var source = Image.FromStream(stream);
            var image = new Bitmap(source);
            Cache.Add(value, image);
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or IOException)
        {
            return null;
        }
    }
}
