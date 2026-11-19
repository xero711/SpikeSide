using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace ValoAutoPick;

internal static class NativePageStyle
{
    private static readonly object ErrorLogGate = new();
    internal static readonly Font Eyebrow = new("Segoe UI", UiMetrics.FontSize(8.5f), FontStyle.Bold);
    internal static readonly Font Body = new("Yu Gothic UI", 10f);
    internal static readonly Font BodyBold = new("Yu Gothic UI", 10f, FontStyle.Bold);
    internal static readonly Font Reading = new("Yu Gothic UI", 11f);
    internal static readonly Font ReadingBold = new("Yu Gothic UI", 11f, FontStyle.Bold);
    internal static readonly Font Small = new("Yu Gothic UI", UiMetrics.FontSize(8.5f));
    internal static readonly Font SmallBold = new("Yu Gothic UI", UiMetrics.FontSize(8.5f), FontStyle.Bold);
    internal static readonly Font Heading = new("Yu Gothic UI", 18f, FontStyle.Bold);
    internal static readonly Font Subheading = new("Yu Gothic UI", 12f, FontStyle.Bold);
    internal static readonly Font Metric = new("Segoe UI", 17f, FontStyle.Bold);

    internal static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        Font = font ?? Body,
        ForeColor = color ?? Theme.Text,
        BackColor = Color.Transparent,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    internal static int TextHeight(Control control, Font font, int lines = 1, int verticalPadding = 4)
    {
        var lineHeight = TextRenderer.MeasureText("国Ag", font, Size.Empty, TextFormatFlags.NoPadding).Height;
        return Math.Max(1, lineHeight) * Math.Max(1, lines) + UiMetrics.Px(control, verticalPadding);
    }

    internal static int TextBlockHeight(Control control, string text, Font font, int width, int verticalPadding = 4)
    {
        var measured = TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), 100_000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return Math.Max(1, measured.Height) + UiMetrics.Px(control, verticalPadding);
    }

    internal static RoundedCard Card() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Theme.Background,
        Padding = new Padding(12),
        Margin = new Padding(5)
    };

    internal static void LogPageError(string page, Exception error)
    {
        try
        {
            lock (ErrorLogGate)
            {
                Directory.CreateDirectory(Storage.Folder);
                File.AppendAllText(
                    Path.Combine(Storage.Folder, "page-errors.log"),
                    $"{DateTimeOffset.Now:O}\t{page}\t{error.GetType().FullName}{Environment.NewLine}");
            }
        }
        catch { }
    }

}

internal enum ImageFit { Contain, Cover }

/// <summary>
/// Delays placeholders until a wait has lasted long enough to justify them, then
/// uses one restrained shimmer that respects the Windows animation setting.
/// </summary>
internal abstract class SkeletonSurface : Control
{
    private const int RevealDelay = 360;
    private const int ShimmerCycle = 1500;
    private readonly System.Windows.Forms.Timer revealTimer = new() { Interval = RevealDelay };
    private readonly System.Windows.Forms.Timer shimmerTimer = new() { Interval = 32 };
    private bool loading;
    private bool skeletonVisible;
    private long shimmerStartedAt;

    protected bool SkeletonVisible => skeletonVisible;
    protected float AnimationPhase => (Environment.TickCount64 - shimmerStartedAt) % ShimmerCycle / (float)ShimmerCycle;

    protected SkeletonSurface()
    {
        AccessibleRole = AccessibleRole.Default;
        AccessibleName = string.Empty;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        revealTimer.Tick += Reveal;
        shimmerTimer.Tick += (_, _) => Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Loading
    {
        get => loading;
        set
        {
            if (loading == value) return;
            loading = value;
            revealTimer.Stop();
            shimmerTimer.Stop();
            skeletonVisible = false;
            if (loading)
            {
                AccessibleName = "コンテンツを読み込み中";
                revealTimer.Start();
            }
            else AccessibleName = string.Empty;
            Invalidate();
        }
    }

    private void Reveal(object? sender, EventArgs e)
    {
        revealTimer.Stop();
        if (!loading || IsDisposed) return;
        skeletonVisible = true;
        shimmerStartedAt = Environment.TickCount64;
        SyncShimmer();
        Invalidate();
    }

    private void SyncShimmer()
    {
        if (loading && skeletonVisible && Visible && System.Windows.SystemParameters.ClientAreaAnimation)
        {
            if (!shimmerTimer.Enabled)
            {
                shimmerStartedAt = Environment.TickCount64;
                shimmerTimer.Start();
            }
        }
        else shimmerTimer.Stop();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        SyncShimmer();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(brush, e.ClipRectangle);
    }

    protected void DrawSkeletonBlock(Graphics graphics, Rectangle bounds, int radius = 7)
    {
        if (!skeletonVisible || bounds.Width < 2 || bounds.Height < 2) return;
        bounds = Rectangle.Intersect(ClientRectangle, bounds);
        if (bounds.Width < 2 || bounds.Height < 2) return;
        using var path = ThemedButton.Rounded(new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1),
            Math.Min(UiMetrics.Px(this, radius), Math.Min(bounds.Width, bounds.Height) / 2));
        using (var fill = new SolidBrush(Theme.Elevated)) graphics.FillPath(fill, path);

        if (!shimmerTimer.Enabled) return;
        var phase = AnimationPhase;
        var bandWidth = Math.Clamp(bounds.Width / 3, UiMetrics.Px(this, 24), UiMetrics.Px(this, 120));
        var bandX = bounds.Left - bandWidth + (int)Math.Round((bounds.Width + bandWidth) * phase);
        var band = new Rectangle(bandX, bounds.Top, bandWidth, bounds.Height);
        var state = graphics.Save();
        graphics.SetClip(path);
        using var shine = new LinearGradientBrush(band, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal);
        shine.InterpolationColors = new ColorBlend
        {
            Colors = [Color.Transparent, Color.FromArgb(35, Theme.Muted), Color.Transparent],
            Positions = [0f, .5f, 1f]
        };
        graphics.FillRectangle(shine, band);
        graphics.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            revealTimer.Stop();
            shimmerTimer.Stop();
            revealTimer.Dispose();
            shimmerTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal enum SkeletonLayout { MatchHistory, StoreOffers, CatalogCards, MediaPreview }

/// <summary>Paints layout-specific placeholders for the existing data-heavy pages.</summary>
internal sealed class SkeletonScreen : SkeletonSurface
{
    private readonly SkeletonLayout layout;
    private string loadingMessage = "読み込み中…";

    internal SkeletonScreen(SkeletonLayout layout)
    {
        this.layout = layout;
        if (layout == SkeletonLayout.MediaPreview) AccessibleRole = AccessibleRole.ProgressBar;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal new bool Loading
    {
        get => base.Loading;
        set
        {
            base.Loading = value;
            Visible = value;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string LoadingMessage
    {
        get => loadingMessage;
        set
        {
            loadingMessage = string.IsNullOrWhiteSpace(value) ? "読み込み中…" : value;
            AccessibleName = loadingMessage;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!SkeletonVisible || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        switch (layout)
        {
            case SkeletonLayout.MatchHistory: PaintMatchHistory(e.Graphics); break;
            case SkeletonLayout.StoreOffers: PaintOfferGrid(e.Graphics); break;
            case SkeletonLayout.CatalogCards: PaintCatalogGrid(e.Graphics); break;
            case SkeletonLayout.MediaPreview: PaintMediaPreview(e.Graphics); break;
        }
    }

    private void PaintMatchHistory(Graphics graphics)
    {
        var bounds = Rectangle.Inflate(ClientRectangle, -UiMetrics.Px(this, 5), -UiMetrics.Px(this, 5));
        var gap = UiMetrics.Px(this, 8);
        var leftWidth = Math.Max(UiMetrics.Px(this, 190), (bounds.Width - gap) * 36 / 100);
        var left = new Rectangle(bounds.X, bounds.Y, leftWidth, bounds.Height);
        var right = new Rectangle(left.Right + gap, bounds.Y, Math.Max(0, bounds.Right - left.Right - gap), bounds.Height);
        DrawSurface(graphics, left);
        DrawSurface(graphics, right);

        var inset = UiMetrics.Px(this, 12);
        var line = UiMetrics.Px(this, 14);
        var rowGap = UiMetrics.Px(this, 7);
        var header = UiMetrics.Px(this, 24);
        DrawSkeletonBlock(graphics, new Rectangle(left.X + inset, left.Y + inset, left.Width / 3, line), 5);
        var rowHeight = Math.Max(UiMetrics.Px(this, 62), (left.Height - inset * 2 - header - rowGap * 4) / 4);
        for (var i = 0; i < 4; i++)
        {
            var y = left.Y + inset + header + rowGap + i * (rowHeight + rowGap);
            var row = new Rectangle(left.X + inset, y, left.Width - inset * 2, rowHeight);
            DrawSurface(graphics, row);
            DrawSkeletonBlock(graphics, new Rectangle(row.X + inset, row.Y + inset, row.Width * 2 / 5, line), 5);
            DrawSkeletonBlock(graphics, new Rectangle(row.Right - inset - row.Width / 4, row.Y + inset, row.Width / 4, line), 5);
            DrawSkeletonBlock(graphics, new Rectangle(row.X + inset, row.Y + inset + line + rowGap, row.Width / 3, line), 5);
            DrawSkeletonBlock(graphics, new Rectangle(row.X + inset, row.Bottom - inset - line, row.Width / 2, line), 5);
        }

        DrawSkeletonBlock(graphics, new Rectangle(right.X + inset, right.Y + inset, right.Width / 3, line + 2), 5);
        DrawSkeletonBlock(graphics, new Rectangle(right.X + inset, right.Y + inset + line + rowGap, right.Width / 2, line), 5);
        var tabY = right.Y + inset + line * 2 + rowGap * 2 + UiMetrics.Px(this, 8);
        for (var i = 0; i < 3; i++)
            DrawSkeletonBlock(graphics, new Rectangle(right.X + inset + i * (UiMetrics.Px(this, 84) + rowGap), tabY, UiMetrics.Px(this, 84), UiMetrics.Px(this, 25)), 6);
        var tableY = tabY + UiMetrics.Px(this, 36);
        for (var i = 0; i < 7; i++)
        {
            var y = tableY + i * (line + rowGap);
            if (y + line > right.Bottom - inset) break;
            DrawSkeletonBlock(graphics, new Rectangle(right.X + inset, y, right.Width * 2 / 5, line), 5);
            DrawSkeletonBlock(graphics, new Rectangle(right.X + inset + right.Width * 3 / 5, y, right.Width * 2 / 5 - inset * 2, line), 5);
        }
    }

    private void PaintOfferGrid(Graphics graphics) =>
        PaintGrid(graphics, preferredCardWidth: 190, preferredCardHeight: 250, maxColumns: 4, maxRows: 2, imageRatio: .55f);

    private void PaintCatalogGrid(Graphics graphics) =>
        PaintGrid(graphics, preferredCardWidth: 160, preferredCardHeight: 210, maxColumns: 4, maxRows: 3, imageRatio: .62f);

    private void PaintMediaPreview(Graphics graphics)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var diameter = Math.Min(UiMetrics.Px(this, 58), Math.Max(UiMetrics.Px(this, 38), ClientSize.Width / 4));
        var ring = new Rectangle(
            (ClientSize.Width - diameter) / 2,
            Math.Max(UiMetrics.Px(this, 14), (ClientSize.Height - diameter - UiMetrics.Px(this, 42)) / 2),
            diameter,
            diameter);
        var stroke = Math.Max(3, UiMetrics.Px(this, 5));
        using (var track = new Pen(Color.FromArgb(75, Theme.Muted), stroke))
            graphics.DrawArc(track, ring, -90, 360);
        using (var arc = new Pen(Theme.Red, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            graphics.DrawArc(arc, ring, -90 + (int)Math.Round(AnimationPhase * 360), 265);

        var labelBounds = new Rectangle(0, ring.Bottom + UiMetrics.Px(this, 10), ClientSize.Width, UiMetrics.Px(this, 28));
        TextRenderer.DrawText(graphics, loadingMessage, Font, labelBounds, Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void PaintGrid(Graphics graphics, int preferredCardWidth, int preferredCardHeight, int maxColumns, int maxRows, float imageRatio)
    {
        var inset = UiMetrics.Px(this, 8);
        var gap = UiMetrics.Px(this, 8);
        var areaWidth = Math.Max(0, ClientSize.Width - inset * 2);
        var areaHeight = Math.Max(0, ClientSize.Height - inset * 2);
        if (areaWidth <= 0 || areaHeight <= 0) return;
        var preferredWidth = UiMetrics.Px(this, preferredCardWidth);
        var preferredHeight = UiMetrics.Px(this, preferredCardHeight);
        var columns = Math.Clamp((areaWidth + gap) / Math.Max(1, preferredWidth + gap), 1, maxColumns);
        var rows = Math.Clamp((areaHeight + gap) / Math.Max(1, preferredHeight + gap), 1, maxRows);
        var cardWidth = Math.Max(1, (areaWidth - gap * (columns - 1)) / columns);
        var cardHeight = Math.Max(1, Math.Min(preferredHeight, (areaHeight - gap * (rows - 1)) / rows));
        var line = UiMetrics.Px(this, 12);
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var x = inset + column * (cardWidth + gap);
            var y = inset + row * (cardHeight + gap);
            var card = new Rectangle(x, y, cardWidth, cardHeight);
            DrawSurface(graphics, card);
            var cardInset = UiMetrics.Px(this, 9);
            DrawSkeletonBlock(graphics, new Rectangle(card.X + cardInset, card.Y + cardInset, card.Width * 3 / 5, line), 5);
            var imageTop = card.Y + cardInset + line + gap;
            var imageHeight = Math.Max(line * 3, (int)((card.Height - cardInset * 2) * imageRatio));
            DrawSkeletonBlock(graphics, new Rectangle(card.X + cardInset, imageTop, card.Width - cardInset * 2, Math.Min(imageHeight, Math.Max(line, card.Bottom - cardInset - imageTop))), 7);
            var titleY = card.Bottom - cardInset - line * 2 - gap;
            DrawSkeletonBlock(graphics, new Rectangle(card.X + cardInset, titleY, card.Width * 4 / 5, line), 5);
            DrawSkeletonBlock(graphics, new Rectangle(card.X + cardInset, titleY + line + UiMetrics.Px(this, 3), card.Width / 2, line), 5);
        }
    }

    private void DrawSurface(Graphics graphics, Rectangle bounds)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        using var path = ThemedButton.Rounded(new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), UiMetrics.Px(this, 8));
        using var fill = new SolidBrush(Theme.Surface);
        using var border = new Pen(Theme.Border);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }
}

/// <summary>
/// Shows a full-resolution source image scaled once, with high-quality filtering, to the control's
/// current size while preserving aspect ratio. The scaled frame is re-rendered from the original on
/// resize, so images stay sharp at any size. The control owns (and disposes) the assigned image.
/// </summary>
internal sealed class SharpImageBox : SkeletonSurface
{
    private Image? image;
    private Bitmap? frame;
    private Rectangle frameSource;
    private ImageFit fit = ImageFit.Contain;
    private string placeholderText = "";

    public SharpImageBox()
    {
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = string.Empty;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Surface;
        Margin = Padding.Empty;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ImageFit Fit
    {
        get => fit;
        set { if (fit == value) return; fit = value; ResetFrame(); Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText
    {
        get => placeholderText;
        set { placeholderText = value ?? ""; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? Image
    {
        get => image;
        set
        {
            if (ReferenceEquals(image, value)) return;
            var previous = image;
            image = value;
            ResetFrame();
            previous?.Dispose();
            if (image is not null) Loading = false;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (image is null)
        {
            if (SkeletonVisible)
                DrawSkeletonBlock(e.Graphics, ClientRectangle, radius: 7);
            else if (!Loading && !string.IsNullOrWhiteSpace(placeholderText))
            {
                var inset = UiMetrics.Px(this, 8);
                var bounds = Rectangle.Inflate(ClientRectangle, -inset, -inset);
                TextRenderer.DrawText(e.Graphics, placeholderText, Font, bounds, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            return;
        }
        var (source, destination) = Placement(image.Size, ClientSize, fit);
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0) return;
        if (frame is null || frame.Size != destination.Size || frameSource != source)
        {
            ResetFrame();
            try { frame = Render(image, source, destination.Size); }
            catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException) { return; }
            frameSource = source;
        }
        var state = e.Graphics.Save();
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.CompositingMode = CompositingMode.SourceOver;
        e.Graphics.DrawImage(frame, destination, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
        e.Graphics.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ResetFrame();
            image?.Dispose();
            image = null;
        }
        base.Dispose(disposing);
    }

    private void ResetFrame()
    {
        frame?.Dispose();
        frame = null;
        frameSource = Rectangle.Empty;
    }

    private static (Rectangle Source, Rectangle Destination) Placement(Size image, Size bounds, ImageFit fit)
    {
        if (image.Width <= 0 || image.Height <= 0) return (Rectangle.Empty, Rectangle.Empty);
        if (fit == ImageFit.Cover)
        {
            // Fill the whole control and crop the overflowing axis evenly.
            var scale = Math.Max((double)bounds.Width / image.Width, (double)bounds.Height / image.Height);
            var sourceWidth = Math.Clamp((int)Math.Round(bounds.Width / scale), 1, image.Width);
            var sourceHeight = Math.Clamp((int)Math.Round(bounds.Height / scale), 1, image.Height);
            return (new Rectangle((image.Width - sourceWidth) / 2, (image.Height - sourceHeight) / 2, sourceWidth, sourceHeight),
                new Rectangle(Point.Empty, bounds));
        }

        var fitScale = Math.Min((double)bounds.Width / image.Width, (double)bounds.Height / image.Height);
        var width = Math.Clamp((int)Math.Round(image.Width * fitScale), 1, bounds.Width);
        var height = Math.Clamp((int)Math.Round(image.Height * fitScale), 1, bounds.Height);
        return (new Rectangle(Point.Empty, image),
            new Rectangle((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height));
    }

    private static Bitmap Render(Image source, Rectangle sourceRect, Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        using var attributes = new ImageAttributes();
        // Prevents the bicubic filter from blending in transparent pixels at the image edges.
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        graphics.DrawImage(source, new Rectangle(Point.Empty, size), sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height, GraphicsUnit.Pixel, attributes);
        return bitmap;
    }
}

/// <summary>
/// Lays out its children as an evenly distributed grid that fills the client area.
/// The row/column split is chosen per item count and available size so each cell stays close to
/// <see cref="PreferredAspect"/>; a shorter final row stays the same card width and is centered.
/// </summary>
internal sealed class ResponsiveCardGrid : Panel
{
    public ResponsiveCardGrid()
    {
        DoubleBuffered = true;
        AutoScroll = false;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
    }

    /// <summary>Preferred cell width / height ratio used to pick the row count.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double PreferredAspect { get; set; } = 1.6;

    /// <summary>Gap between cells, in logical (96 DPI, pre-compact) pixels.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Gap { get; set; } = 10;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) PerformLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
    }

    private void Arrange()
    {
        var items = Controls.Cast<Control>().ToArray();
        var area = new Rectangle(Padding.Left, Padding.Top, ClientSize.Width - Padding.Horizontal, ClientSize.Height - Padding.Vertical);
        if (items.Length == 0 || area.Width <= 0 || area.Height <= 0) return;

        var gap = UiMetrics.Px(this, Gap);
        var rows = ChooseRows(items.Length, area.Size, gap);
        var baseCount = items.Length / rows;
        var extra = items.Length % rows;
        var maximumColumns = (items.Length + rows - 1) / rows;
        var cellWidth = Math.Max(1, (area.Width - gap * (maximumColumns - 1)) / maximumColumns);
        var cellHeight = Math.Max(1, (area.Height - gap * (rows - 1)) / rows);
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            var count = baseCount + (row < extra ? 1 : 0);
            var rowWidth = count * cellWidth + (count - 1) * gap;
            var rowLeft = area.Left + (area.Width - rowWidth) / 2;
            var top = area.Top + row * (cellHeight + gap);
            for (var column = 0; column < count; column++)
            {
                var left = rowLeft + column * (cellWidth + gap);
                var bounds = new Rectangle(left, top, cellWidth, cellHeight);
                var item = items[index++];
                if (item.Dock != DockStyle.None) item.Dock = DockStyle.None;
                if (item.Bounds != bounds) item.Bounds = bounds;
            }
        }
    }

    private int ChooseRows(int count, Size area, int gap)
    {
        var aspect = PreferredAspect > 0 && double.IsFinite(PreferredAspect) ? PreferredAspect : 1.6;
        var bestRows = 1;
        var bestScore = double.MinValue;
        for (var rows = 1; rows <= count; rows++)
        {
            var columns = (count + rows - 1) / rows;
            var cellWidth = (area.Width - gap * (columns - 1)) / (double)columns;
            var cellHeight = (area.Height - gap * (rows - 1)) / (double)rows;
            if (cellWidth <= 0 || cellHeight <= 0) continue;
            // Size of the largest preferred-aspect card that fits a cell; uneven rows are penalised
            // slightly so an exact split (e.g. 3 x 1, 2 x 2, 3 x 2) wins when sizes are close.
            var fit = Math.Min(cellWidth, cellHeight * aspect);
            var score = count % rows == 0 ? fit : fit * 0.85;
            if (score > bestScore) { bestScore = score; bestRows = rows; }
        }
        return bestRows;
    }
}

internal static class NativeImageCache
{
    private const int MaxCachedBytes = 32 * 1024 * 1024;
    // valorant-api.com returns this valid PNG (a white X) for several Standard skin
    // image paths. Treat it as missing art so callers can continue to a real fallback.
    private const string MissingArtSha256 = "6E236924D463ED12ABA89C6B5C3DEC87F35AEB57AAC8B9068FE96377D113DE8F";
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Downloads = new(4, 4);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (byte[] Bytes, long Used)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static long cachedBytes;
    private static long usageClock;

    /// <summary>
    /// Decodes the image at its native resolution without padding or pre-scaling, so the
    /// display control can resample it exactly once to whatever size it is shown at.
    /// </summary>
    internal static async Task<Image?> LoadOriginalAsync(string? source, CancellationToken cancellationToken = default)
    {
        var bytes = await GetBytesAsync(source, cancellationToken);
        if (bytes is null) return null;
        try
        {
            return await Task.Run<Image?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsMissingArtPlaceholder(bytes)) return null;
                using var stream = new MemoryStream(bytes, writable: false);
                using var decoded = Image.FromStream(stream);
                if (decoded.Width <= 0 || decoded.Height <= 0) return null;
                var bitmap = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);
                bitmap.SetResolution(96, 96);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(decoded, new Rectangle(0, 0, decoded.Width, decoded.Height), 0, 0, decoded.Width, decoded.Height, GraphicsUnit.Pixel);
                return bitmap;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            return null;
        }
    }

    private static bool IsMissingArtPlaceholder(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) == MissingArtSha256;

    private static async Task<byte[]?> GetBytesAsync(string? source, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("media.valorant-api.com" or "valorant.dyn.riotcdn.net")) return null;

        byte[]? bytes;
        lock (Gate)
            bytes = Cache.TryGetValue(uri.AbsoluteUri, out var cached) ? Touch(uri.AbsoluteUri, cached).Bytes : null;

        if (bytes is null)
        {
            await Downloads.WaitAsync(cancellationToken);
            try
            {
                lock (Gate)
                    bytes = Cache.TryGetValue(uri.AbsoluteUri, out var cached) ? Touch(uri.AbsoluteUri, cached).Bytes : null;
                if (bytes is null)
                {
                    bytes = await Http.GetByteArrayAsync(uri, cancellationToken);
                    if (bytes.Length is 0 or > 8 * 1024 * 1024) return null;
                    CacheBytes(uri.AbsoluteUri, bytes);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return null;
            }
            finally { Downloads.Release(); }
        }
        return bytes;
    }

    private static (byte[] Bytes, long Used) Touch(string key, (byte[] Bytes, long Used) entry)
    {
        var updated = (entry.Bytes, ++usageClock);
        Cache[key] = updated;
        return updated;
    }

    private static void CacheBytes(string key, byte[] bytes)
    {
        if (bytes.Length > MaxCachedBytes) return;
        lock (Gate)
        {
            while (cachedBytes + bytes.Length > MaxCachedBytes && Cache.Count > 0)
            {
                var oldest = Cache.MinBy(pair => pair.Value.Used);
                Cache.Remove(oldest.Key);
                cachedBytes -= oldest.Value.Bytes.Length;
            }
            if (Cache.TryGetValue(key, out var prior)) cachedBytes -= prior.Bytes.Length;
            Cache[key] = (bytes, ++usageClock);
            cachedBytes += bytes.Length;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpikeSide", typeof(NativeImageCache).Assembly.GetName().Version!.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        return client;
    }
}

internal static class NativeVideoCache
{
    private const long MaxVideoBytes = 120L * 1024 * 1024;
    private const long MaxCacheBytes = 384L * 1024 * 1024;
    private const int MaxCacheFiles = 8;
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Downloads = new(1, 1);
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickPickerPro", "VideoCache");

    internal static async Task<string?> GetLocalFileAsync(string? source, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not "valorant.dyn.riotcdn.net" || !uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return null;

        var fileName = Path.GetFileName(uri.AbsolutePath);
        if (fileName.Length == 0 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var destination = Path.Combine(CacheDirectory, fileName);
        if (IsCached(destination)) return destination;

        await Downloads.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            if (IsCached(destination)) return destination;
            Directory.CreateDirectory(CacheDirectory);
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxVideoBytes) return null;

            temporaryPath = Path.Combine(CacheDirectory, $"{fileName}.{Guid.NewGuid():N}.tmp");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > MaxVideoBytes) return null;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            if (new FileInfo(temporaryPath).Length == 0) return null;
            File.Move(temporaryPath, destination, overwrite: true);
            temporaryPath = null;
            PruneCache(destination);
            return destination;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException) { return null; }
        finally
        {
            if (temporaryPath is not null) { try { File.Delete(temporaryPath); } catch (IOException) { } }
            Downloads.Release();
        }
    }

    private static bool IsCached(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxVideoBytes) return false;
            info.LastAccessTimeUtc = DateTime.UtcNow;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void PruneCache(string keepPath)
    {
        try
        {
            var files = new DirectoryInfo(CacheDirectory).GetFiles("*.mp4")
                .OrderBy(file => file.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(file => file.Length);
            var count = files.Count;
            foreach (var file in files)
            {
                if (count <= MaxCacheFiles && total <= MaxCacheBytes) break;
                if (file.FullName.Equals(keepPath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                    count--;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpikeSide", typeof(NativeVideoCache).Assembly.GetName().Version!.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("video/mp4"));
        return client;
    }
}
