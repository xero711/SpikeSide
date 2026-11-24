using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ValoAutoPick;

// The notification-area menu. Every row paints itself inside its own bounds so
// text is centred on the row instead of on the native menu's fixed text slot
// (which ignores vertical Padding and pushed the labels to the top of the row).
// Rows remain ToolStripMenuItems, so keyboard navigation and screen-reader
// semantics are those of a native menu.
internal static class TrayMenuStyle
{
    internal const int MinWidth = 228;
    internal const int Inset = 6;      // hover/card inset from the menu border
    internal const int TextX = 20;     // shared left edge of header and action text
    internal const TextFormatFlags TextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;

    internal static readonly Font Brand = SharedFonts.Get("Segoe UI", 11, FontStyle.Bold);
    internal static readonly Font Badge = SharedFonts.Get("Yu Gothic UI", 8, FontStyle.Bold);
    internal static readonly Font Status = SharedFonts.Get("Yu Gothic UI", 9, FontStyle.Bold);
    internal static readonly Font Action = SharedFonts.Get("Yu Gothic UI", 10, FontStyle.Bold);

    internal static float Scale(ToolStripItem item) => (item.Owner?.DeviceDpi ?? item.GetCurrentParent()?.DeviceDpi ?? 96) / 96f;
    internal static int Px(ToolStripItem item, float value) => (int)Math.Round(value * Scale(item));

    // The drop-down gives each menu item its own preferred width, so every row
    // reports the widest row's width and they all line up on the right.
    internal static int RowWidth(ToolStripItem item)
    {
        var width = Px(item, MinWidth);
        if (item.Owner == null) return item is ITrayRow row ? Math.Max(width, row.ContentWidth()) : width;
        foreach (var sibling in item.Owner.Items.OfType<ITrayRow>()) width = Math.Max(width, sibling.ContentWidth());
        return width;
    }

    // Header and status rows are informational. They are menu items (not
    // labels) only so the drop-down stretches them across the full width like
    // the action rows; disabling them keeps keyboard focus off them.
    internal static void MakeStatic(ToolStripMenuItem item)
    {
        item.Enabled = false;
        item.AccessibleRole = AccessibleRole.StaticText;
        item.Margin = Padding.Empty;
        item.Padding = Padding.Empty;
    }

    // Fills the row and returns the drawable area inside the 1px menu border.
    // Translates to menu x = 1 and clamps the width to the menu interior so the
    // right side of cards, switches, and separators never clips against the border.
    internal static Size BeginRow(ToolStripItem item, Graphics g)
    {
        using (var background = new SolidBrush(Theme.Surface)) g.FillRectangle(background, new Rectangle(Point.Empty, item.Size));
        var dx = 1 - item.Bounds.X;
        g.TranslateTransform(dx, 0);
        var innerWidth = item.Owner is { ClientSize.Width: > 2 } owner
            ? owner.ClientSize.Width - 2
            : item.Width - Math.Max(0, dx);
        return new Size(Math.Max(0, innerWidth), item.Height);
    }

    // Yu Gothic UI's ink sits about 1px (at 96 DPI) above the centre of its
    // line box; Japanese text gets a small downward nudge to look centred.
    internal static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, int nudge = 0)
    {
        bounds.Offset(0, nudge);
        TextRenderer.DrawText(g, text, font, bounds, color, TextFlags);
    }

    internal static Size Measure(string text, Font font) => TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFlags);

    internal static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d < 2) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal interface ITrayRow { int ContentWidth(); }

internal sealed class TrayContextMenu : ContextMenuStrip
{
    private bool keepOpenOnNextItemClick;

    public TrayContextMenu()
    {
        Renderer = new TrayMenuRenderer();
        ShowImageMargin = false;
        ShowCheckMargin = false;
        Padding = new Padding(1, 2, 1, 2);
        Font = TrayMenuStyle.Action;
    }

    protected override void OnItemClicked(ToolStripItemClickedEventArgs e)
    {
        keepOpenOnNextItemClick = e.ClickedItem is TrayToggleItem;
        base.OnItemClicked(e);
    }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        var baseSize = base.GetPreferredSize(constrainingSize);
        var width = 0;
        var height = Padding.Vertical;
        foreach (ToolStripItem item in Items)
        {
            if (!item.Available) continue;
            var pref = item.GetPreferredSize(Size.Empty);
            width = Math.Max(width, pref.Width);
            height += pref.Height + item.Margin.Vertical;
        }
        return new Size(Math.Max(baseSize.Width, width + 2), Math.Max(baseSize.Height, height));
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        keepOpenOnNextItemClick = false;
        base.OnOpening(e);
        TrayMenuRenderer.ApplyMetrics(this);
    }

    protected override void OnClosing(ToolStripDropDownClosingEventArgs e)
    {
        if (keepOpenOnNextItemClick && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
        {
            keepOpenOnNextItemClick = false;
            e.Cancel = true;
            return;
        }
        keepOpenOnNextItemClick = false;
        base.OnClosing(e);
    }
}

internal sealed class TrayHeaderItem : ToolStripMenuItem, ITrayRow
{
    private const string BadgeText = "体験版";
    private readonly bool trial;

    public TrayHeaderItem(bool trial) : base("SPIKESIDE")
    {
        this.trial = trial;
        AccessibleName = trial ? "SpikeSide 体験版" : "SpikeSide";
        Font = TrayMenuStyle.Brand;
        TrayMenuStyle.MakeStatic(this);
    }

    public int ContentWidth()
    {
        var width = TrayMenuStyle.Px(this, TrayMenuStyle.TextX + 16) + TrayMenuStyle.Measure(Text ?? "", Font).Width;
        if (trial) width += TrayMenuStyle.Px(this, 8 + 14) + TrayMenuStyle.Measure(BadgeText, TrayMenuStyle.Badge).Width;
        return width;
    }

    public override Size GetPreferredSize(Size constrainingSize) => new(TrayMenuStyle.RowWidth(this), TrayMenuStyle.Px(this, 42));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var size = TrayMenuStyle.BeginRow(this, g);

        var x = TrayMenuStyle.Px(this, TrayMenuStyle.TextX);
        var markHeight = TrayMenuStyle.Px(this, 14);
        var markWidth = Math.Max(2, TrayMenuStyle.Px(this, 3));
        using (var accent = new SolidBrush(Theme.Red))
            g.FillRectangle(accent, x - TrayMenuStyle.Px(this, 9), (size.Height - markHeight) / 2, markWidth, markHeight);

        var brand = TrayMenuStyle.Measure(Text ?? "", Font);
        TrayMenuStyle.DrawText(g, Text ?? "", Font, new Rectangle(x, 0, size.Width - x, size.Height), Theme.Text);
        if (!trial) return;

        var label = TrayMenuStyle.Measure(BadgeText, TrayMenuStyle.Badge);
        var padX = TrayMenuStyle.Px(this, 7);
        var badgeHeight = TrayMenuStyle.Px(this, 18);
        var badge = new Rectangle(x + brand.Width + TrayMenuStyle.Px(this, 8), (size.Height - badgeHeight) / 2, label.Width + padX * 2, badgeHeight);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = TrayMenuStyle.Rounded(new RectangleF(badge.X + .5f, badge.Y + .5f, badge.Width - 1, badge.Height - 1), badge.Height / 2f))
        using (var fill = new SolidBrush(Color.FromArgb(40, Theme.Red)))
        using (var outline = new Pen(Color.FromArgb(150, Theme.Red)))
        {
            g.FillPath(fill, path);
            g.DrawPath(outline, path);
        }
        g.SmoothingMode = SmoothingMode.Default;
        TrayMenuStyle.DrawText(g, BadgeText, TrayMenuStyle.Badge, badge with { X = badge.X + padX, Width = badge.Width - padX }, Theme.Red, TrayMenuStyle.Px(this, 1));
    }
}

internal sealed class TrayStatusItem : ToolStripMenuItem, ITrayRow
{
    private readonly string[] states;
    private bool active;
    private bool warning;

    // states: every text the row can show. The row reserves room for the
    // longest one so the menu keeps one width whatever the monitoring state.
    public TrayStatusItem(params string[] states)
    {
        this.states = states;
        Font = TrayMenuStyle.Status;
        TrayMenuStyle.MakeStatic(this);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => active;
        set { if (active == value) return; active = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Warning
    {
        get => warning;
        set { if (warning == value) return; warning = value; Invalidate(); }
    }

    public int ContentWidth() => TrayMenuStyle.Px(this, TrayMenuStyle.Inset * 2 + 31 + 14) + states.Append(Text ?? "").Max(state => TrayMenuStyle.Measure(state, Font).Width);

    public override Size GetPreferredSize(Size constrainingSize) => new(TrayMenuStyle.RowWidth(this), TrayMenuStyle.Px(this, 38));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var size = TrayMenuStyle.BeginRow(this, g);

        var inset = TrayMenuStyle.Px(this, TrayMenuStyle.Inset);
        var card = new RectangleF(inset + .5f, TrayMenuStyle.Px(this, 1) + .5f, Math.Max(4, size.Width - inset * 2 - 1), size.Height - TrayMenuStyle.Px(this, 7) - 1);
        var tint = warning ? Theme.Red : active ? Theme.Green : Theme.Muted;
        var emphasized = active || warning;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = TrayMenuStyle.Rounded(card, TrayMenuStyle.Px(this, 6)))
        using (var fill = new SolidBrush(Color.FromArgb(emphasized ? 28 : 14, tint)))
        using (var outline = new Pen(Color.FromArgb(emphasized ? 78 : 45, tint)))
        {
            g.FillPath(fill, path);
            g.DrawPath(outline, path);
        }

        var centerY = card.Y + card.Height / 2f;
        var dotX = inset + TrayMenuStyle.Px(this, 14);
        var dot = TrayMenuStyle.Px(this, 8);
        if (emphasized)
        {
            var halo = dot + TrayMenuStyle.Px(this, 6);
            using var glow = new SolidBrush(Color.FromArgb(55, tint));
            g.FillEllipse(glow, dotX + dot / 2f - halo / 2f, centerY - halo / 2f, halo, halo);
        }
        using (var dotBrush = new SolidBrush(tint)) g.FillEllipse(dotBrush, dotX, centerY - dot / 2f, dot, dot);
        g.SmoothingMode = SmoothingMode.Default;

        var textX = dotX + dot + TrayMenuStyle.Px(this, 9);
        var textBounds = new Rectangle(textX, (int)card.Y, Math.Max(0, (int)card.Right - textX - TrayMenuStyle.Px(this, 8)), (int)Math.Round(card.Height));
        TrayMenuStyle.DrawText(g, Text ?? "", Font, textBounds, emphasized ? Theme.Text : Theme.Muted, TrayMenuStyle.Px(this, 1));
    }
}

internal sealed class TrayToggleItem : ToolStripMenuItem, ITrayRow
{
    private bool isOn;

    public TrayToggleItem(string text, EventHandler onClick) : base(text, null, onClick)
    {
        Font = TrayMenuStyle.Action;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsOn
    {
        get => isOn;
        set
        {
            if (isOn == value) return;
            isOn = value;
            Checked = value;
            Invalidate();
        }
    }

    public int ContentWidth() =>
        TrayMenuStyle.Px(this, TrayMenuStyle.TextX + 18 + 38 + 12 + TrayMenuStyle.Inset + 8)
        + TrayMenuStyle.Measure(Text ?? "", Font).Width
        + TrayMenuStyle.Measure("OFF", TrayMenuStyle.Badge).Width;

    public override Size GetPreferredSize(Size constrainingSize) => new(TrayMenuStyle.RowWidth(this), TrayMenuStyle.Px(this, 38));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var size = TrayMenuStyle.BeginRow(this, g);
        var inset = TrayMenuStyle.Px(this, TrayMenuStyle.Inset);

        var hot = Enabled && (Selected || Pressed);
        if (hot)
        {
            var vertical = TrayMenuStyle.Px(this, 2);
            var row = new RectangleF(inset, vertical, Math.Max(4, size.Width - inset * 2), size.Height - vertical * 2);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = TrayMenuStyle.Rounded(row, TrayMenuStyle.Px(this, 6)))
            using (var fill = new SolidBrush(Theme.Elevated))
                g.FillPath(fill, path);
            g.SmoothingMode = SmoothingMode.Default;
            var barHeight = Math.Max(1, (int)row.Height - TrayMenuStyle.Px(this, 16));
            using var accent = new SolidBrush(Theme.Red);
            g.FillRectangle(accent, inset + TrayMenuStyle.Px(this, 4), (size.Height - barHeight) / 2, Math.Max(2, TrayMenuStyle.Px(this, 3)), barHeight);
        }

        var trackW = TrayMenuStyle.Px(this, 36);
        var trackH = TrayMenuStyle.Px(this, 20);
        var trackX = size.Width - inset - TrayMenuStyle.Px(this, 10) - trackW;
        var trackY = (size.Height - trackH) / 2f;
        var trackRect = new RectangleF(trackX, trackY, trackW, trackH);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var trackPath = TrayMenuStyle.Rounded(trackRect, trackH / 2f))
        {
            var trackColor = !Enabled ? Theme.Border : isOn ? Theme.Red : Theme.Border;
            using (var trackBrush = new SolidBrush(trackColor))
                g.FillPath(trackBrush, trackPath);
            if (!isOn)
            {
                using var trackBorder = new Pen(Theme.Border);
                g.DrawPath(trackBorder, trackPath);
            }
        }

        var knobSize = trackH - TrayMenuStyle.Px(this, 6);
        var knobPad = TrayMenuStyle.Px(this, 3);
        var knobX = isOn ? trackRect.Right - knobSize - knobPad : trackRect.X + knobPad;
        var knobY = trackRect.Y + (trackH - knobSize) / 2f;
        using (var knobBrush = new SolidBrush(Enabled ? Color.White : Theme.Muted))
            g.FillEllipse(knobBrush, knobX, knobY, knobSize, knobSize);
        g.SmoothingMode = SmoothingMode.Default;

        var stateText = isOn ? "ON" : "OFF";
        var stateSize = TrayMenuStyle.Measure(stateText, TrayMenuStyle.Badge);
        var stateX = trackX - stateSize.Width - TrayMenuStyle.Px(this, 7);
        var stateColor = !Enabled ? Theme.Muted : isOn ? Theme.Green : Theme.Muted;
        TrayMenuStyle.DrawText(g, stateText, TrayMenuStyle.Badge, new Rectangle(stateX, 0, stateSize.Width + 4, size.Height), stateColor);

        var textColor = !Enabled ? Theme.Muted : Theme.Text;
        var x = TrayMenuStyle.Px(this, TrayMenuStyle.TextX);
        var maxTextWidth = Math.Max(0, stateX - x - TrayMenuStyle.Px(this, 8));
        TrayMenuStyle.DrawText(g, Text ?? "", Font, new Rectangle(x, 0, maxTextWidth, size.Height), textColor, TrayMenuStyle.Px(this, 1));
    }
}

internal sealed class TrayActionItem : ToolStripMenuItem, ITrayRow
{
    private readonly bool destructive;

    public TrayActionItem(string text, EventHandler onClick, bool destructive = false) : base(text, null, onClick)
    {
        this.destructive = destructive;
        Font = TrayMenuStyle.Action;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
    }

    public int ContentWidth() => TrayMenuStyle.Px(this, TrayMenuStyle.TextX + 24) + TrayMenuStyle.Measure(Text ?? "", Font).Width;

    public override Size GetPreferredSize(Size constrainingSize) => new(TrayMenuStyle.RowWidth(this), TrayMenuStyle.Px(this, 36));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var size = TrayMenuStyle.BeginRow(this, g);

        var hot = Enabled && (Selected || Pressed);
        if (hot)
        {
            var inset = TrayMenuStyle.Px(this, TrayMenuStyle.Inset);
            var vertical = TrayMenuStyle.Px(this, 2);
            var row = new RectangleF(inset, vertical, Math.Max(4, size.Width - inset * 2), size.Height - vertical * 2);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = TrayMenuStyle.Rounded(row, TrayMenuStyle.Px(this, 6)))
            using (var fill = new SolidBrush(destructive ? Color.FromArgb(38, Theme.Red) : Theme.Elevated))
                g.FillPath(fill, path);
            g.SmoothingMode = SmoothingMode.Default;
            var barHeight = Math.Max(1, (int)row.Height - TrayMenuStyle.Px(this, 16));
            using var accent = new SolidBrush(Theme.Red);
            g.FillRectangle(accent, inset + TrayMenuStyle.Px(this, 4), (size.Height - barHeight) / 2, Math.Max(2, TrayMenuStyle.Px(this, 3)), barHeight);
        }

        var color = !Enabled ? Theme.Muted : hot && destructive ? Theme.Red : Theme.Text;
        var x = TrayMenuStyle.Px(this, TrayMenuStyle.TextX);
        TrayMenuStyle.DrawText(g, Text ?? "", Font, new Rectangle(x, 0, Math.Max(0, size.Width - x - TrayMenuStyle.Px(this, 12)), size.Height), color, TrayMenuStyle.Px(this, 1));
    }
}

internal sealed class TraySeparator : ToolStripSeparator
{
    private readonly int height;
    private readonly bool line;

    // line: false gives a blank spacer (e.g. breathing room under the last row).
    public TraySeparator(bool line = true, int height = 11) { this.line = line; this.height = height; Margin = Padding.Empty; }

    public override Size GetPreferredSize(Size constrainingSize) => new(TrayMenuStyle.RowWidth(this), TrayMenuStyle.Px(this, height));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var size = TrayMenuStyle.BeginRow(this, g);
        if (!line) return;
        var inset = TrayMenuStyle.Px(this, TrayMenuStyle.Inset + 6);
        using var pen = new Pen(Theme.Border);
        var y = size.Height / 2;
        e.Graphics.DrawLine(pen, inset, y, Math.Max(inset, size.Width - inset), y);
    }
}

internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    public TrayMenuRenderer() : base(new ThemeMenuColorTable()) { RoundedEdges = false; }

    // Rows paint themselves; the renderer only draws the menu surface and border.
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) { }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var background = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(background, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    // The drop-down sizes menu items from their text alone, then stretches
    // every menu item across the menu. Give the menu the width the custom rows
    // need (status dot, trial badge, toggles, padding) before it opens.
    internal static void ApplyMetrics(ContextMenuStrip menu)
    {
        var width = 0;
        var height = menu.Padding.Vertical;
        foreach (ToolStripItem item in menu.Items)
        {
            if (!item.Available) continue;
            var pref = item.GetPreferredSize(Size.Empty);
            width = Math.Max(width, pref.Width);
            height += pref.Height + item.Margin.Vertical;
        }
        var target = new Size(width + 2, height);
        menu.MinimumSize = new Size(target.Width, 0);
        menu.Size = target;
        menu.PerformLayout();
    }
}
