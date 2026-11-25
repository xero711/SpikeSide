using System.Drawing.Drawing2D;

namespace ValoAutoPick;

internal sealed class MatchTrendChart : Control
{
    private readonly ToolTip tips = new() { ShowAlways = true };
    private IReadOnlyList<TrendPoint> points = [];
    private PointF[] positions = [];
    private int hovered = -1;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, string> MapName { get; set; } = id => id;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<int?, string> RankName { get; set; } = _ => "ランク不明";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Metric { get; set; } = "ACS";
    public MatchTrendChart() { DoubleBuffered = true; AccessibleName = "試合成績の推移"; }
    public void SetPoints(IReadOnlyList<TrendPoint> value) { points = value; hovered = -1; tips.SetToolTip(this, ""); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Theme.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (points.Count < 2)
        {
            TextRenderer.DrawText(e.Graphics, "グラフを表示するには、データのある試合が2件以上必要です。", Font, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            positions = [];
            return;
        }
        var min = points.Min(p => p.Value); var max = points.Max(p => p.Value);
        var isRr = Metric == "RR推移";
        if (isRr)
        {
            min = Math.Min(0, Math.Floor(min / 100) * 100);
            max = Math.Max(100, Math.Ceiling(max / 100) * 100);
        }
        else
        {
            var span = Math.Max(max - min, 1); min -= span * .1; max += span * .1;
        }
        string AxisLabel(int index) => (max - (max - min) * index / 2).ToString(isRr ? "0" : "0.#");
        var pad = UiMetrics.Px(this, 40);
        var textHeight = TextRenderer.MeasureText(e.Graphics, "100", Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
        var labelWidth = Enumerable.Range(0, 3).Select(i => TextRenderer.MeasureText(e.Graphics, AxisLabel(i), Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width).Max();
        var leftPad = Math.Max(pad, labelWidth + UiMetrics.Px(this, 12));
        var topPad = isRr ? Math.Max(textHeight + UiMetrics.Px(this, 8), (textHeight + 1) / 2) : Math.Max(pad / 2, (textHeight + 1) / 2);
        var bottomPad = Math.Max(pad, textHeight + UiMetrics.Px(this, 8));
        var rect = RectangleF.FromLTRB(leftPad, topPad, Math.Max(leftPad + 1, Width - pad / 2f), Math.Max(topPad + 1, Height - bottomPad));
        if (isRr)
            TextRenderer.DrawText(e.Graphics, "試合後のRR · ランク変更時は線を区切ります", Font,
                new Rectangle(leftPad, 0, Math.Max(0, Width - leftPad), topPad), Theme.Muted, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        using var grid = new Pen(Theme.Muted) { DashStyle = DashStyle.Dot };
        using var line = new Pen(Theme.Text, Math.Max(1, UiMetrics.Px(this, 2)));
        for (var i = 0; i <= 2; i++)
        {
            var y = rect.Top + rect.Height * i / 2;
            e.Graphics.DrawLine(grid, rect.Left, y, rect.Right, y);
            TextRenderer.DrawText(e.Graphics, AxisLabel(i), Font, new Rectangle(0, (int)y - textHeight / 2, leftPad - UiMetrics.Px(this, 4), textHeight), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        positions = points.Select((p, i) => new PointF(rect.Left + rect.Width * i / (points.Count - 1), rect.Bottom - (float)((p.Value - min) / (max - min)) * rect.Height)).ToArray();
        for (var i = 1; i < positions.Length; i++)
        {
            var previousTier = points[i - 1].TierAfterUpdate;
            var currentTier = points[i].TierAfterUpdate;
            if (isRr && previousTier.HasValue && currentTier.HasValue && previousTier != currentTier) continue;
            e.Graphics.DrawLine(line, positions[i - 1], positions[i]);
        }
        var radius = Math.Max(3, UiMetrics.Px(this, 5));
        for (var i = 0; i < points.Count; i++)
        {
            using var brush = new SolidBrush(points[i].Outcome == MatchOutcome.Win ? Theme.Green : points[i].Outcome == MatchOutcome.Loss ? Theme.Red : Theme.Muted);
            e.Graphics.FillEllipse(brush, positions[i].X - radius, positions[i].Y - radius, radius * 2, radius * 2);
        }
        var dateBounds = new Rectangle((int)rect.Left, (int)rect.Bottom + UiMetrics.Px(this, 4), (int)rect.Width, textHeight);
        TextRenderer.DrawText(e.Graphics, $"{points[0].Date.ToLocalTime():M/d}", Font, dateBounds, Theme.Muted, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, $"{points[^1].Date.ToLocalTime():M/d}", Font, dateBounds, Theme.Muted, TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var nearest = -1; var distance = UiMetrics.Px(this, 16) * UiMetrics.Px(this, 16);
        for (var i = 0; i < positions.Length; i++)
        {
            var dx = positions[i].X - e.X; var dy = positions[i].Y - e.Y; var d = dx * dx + dy * dy;
            if (d < distance) { distance = (int)d; nearest = i; }
        }
        if (nearest == hovered) return;
        hovered = nearest;
        if (nearest < 0) { tips.SetToolTip(this, ""); return; }
        var point = points[nearest];
        var value = Metric == "RR推移"
            ? $"{RankName(point.TierAfterUpdate)} · 試合後 {point.Value:0} RR" + (point.RankedRatingEarned is { } earned ? $"（{earned:+0;-0;0} RR）" : "")
            : $"{Metric}: {point.Value:0.##}";
        tips.SetToolTip(this, $"{point.Date.ToLocalTime():yyyy/M/d HH:mm} · {MapName(point.MapId)}\n{value}");
    }
}
