using System.Reflection;
using System.Drawing.Drawing2D;

namespace ValoAutoPick;

internal static class AppIcon
{
    private static readonly byte[] Data = Load();
    private static readonly Bitmap? Artwork = LoadArtwork();
    private static byte[] Load()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ValoAutoPick.Assets.SpikeSide.ico");
        if (resource == null) return [];
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        return copy.ToArray();
    }
    public static Icon Create()
    {
        if (Data.Length == 0) return (Icon)SystemIcons.Application.Clone();
        using var stream = new MemoryStream(Data, writable: false);
        using var source = new Icon(stream);
        return new Icon(source, source.Size);
    }

    private static Bitmap? LoadArtwork()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ValoAutoPick.Assets.SpikeSide.png");
        if (resource == null) return null;
        using var image = Image.FromStream(resource);
        return new Bitmap(image);
    }

    public static void Draw(Graphics graphics, Rectangle bounds)
    {
        if (Artwork == null || bounds.Width <= 0 || bounds.Height <= 0) return;
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.DrawImage(Artwork, bounds, 0, 0, Artwork.Width, Artwork.Height, GraphicsUnit.Pixel);
        }
        finally { graphics.Restore(state); }
    }
}
