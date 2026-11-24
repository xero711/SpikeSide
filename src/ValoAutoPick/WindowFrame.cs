using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ValoAutoPick;

// Keep the standard Windows window styles for keyboard shortcuts and the
// system menu, but use our own client area instead of the native caption.
internal static class WindowFrame
{
    private const int WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084, WmSysCommand = 0x0112;
    private const int HtCaption = 2, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14;
    private const int HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
    private const int ScSize = 0xf000, ScMaximize = 0xf030;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    internal static bool ProcessMessage(Form form, Control? caption, ref Message message, bool resizable = false)
    {
        if (message.Msg == WmNcCalcSize)
        {
            // Without a native frame, Windows would size a maximized window past the monitor edges by the
            // (invisible) border width. Clamp the client area to the monitor's work area instead so nothing is
            // cut off and the taskbar stays visible. rgrc[0] is the first RECT of NCCALCSIZE_PARAMS.
            if (resizable && message.WParam != IntPtr.Zero && message.LParam != IntPtr.Zero && IsZoomed(message.HWnd))
            {
                var proposed = Marshal.PtrToStructure<NativeRect>(message.LParam);
                var monitor = MonitorFromRect(ref proposed, MonitorDefaultToNearest);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                    Marshal.StructureToPtr(info.Work, message.LParam, false);
            }
            message.Result = IntPtr.Zero;
            return true;
        }

        var command = message.WParam.ToInt64() & 0xfff0;
        if (message.Msg == WmSysCommand && (!resizable && (command == ScMaximize || command == ScSize)))
        {
            message.Result = IntPtr.Zero;
            return true;
        }

        if (message.Msg != WmNcHitTest || caption == null) return false;
        long packedPoint = message.LParam.ToInt64();
        var screenPoint = new Point(unchecked((short)(packedPoint & 0xffff)), unchecked((short)((packedPoint >> 16) & 0xffff)));
        var point = form.PointToClient(screenPoint);
        var client = form.ClientRectangle;
        if (!client.Contains(point)) return false;

        // A maximized window cannot be resized from its edges; those pixels belong to the caption and content.
        if (resizable && !IsZoomed(message.HWnd))
        {
            var edge = Math.Max(6, UiMetrics.Px(form, 6));
            var left = point.X < edge;
            var right = point.X >= client.Width - edge;
            var top = point.Y < edge;
            var bottom = point.Y >= client.Height - edge;
            var hit = (top, left, right, bottom) switch
            {
                (true, true, _, _) => HtTopLeft,
                (true, _, true, _) => HtTopRight,
                (_, true, _, true) => HtBottomLeft,
                (_, _, true, true) => HtBottomRight,
                (true, _, _, _) => HtTop,
                (_, true, _, _) => HtLeft,
                (_, _, true, _) => HtRight,
                (_, _, _, true) => HtBottom,
                _ => 0,
            };
            if (hit != 0)
            {
                message.Result = (IntPtr)hit;
                return true;
            }
        }

        if (caption.Bounds.Contains(point))
        {
            message.Result = (IntPtr)HtCaption;
            return true;
        }
        return false;
    }
}

internal sealed class WindowCaption : Panel
{
    private const int WmNcHitTest = 0x0084, HtTransparent = -1;
    private readonly Form owner;
    private readonly CaptionButton minimize, close;
    private readonly CaptionButton? maximize;
    private readonly Font brandFont = new("Segoe UI", UiMetrics.FontSize(9f), FontStyle.Bold);
    private readonly Font detailFont = new("Yu Gothic UI", UiMetrics.FontSize(8.5f), FontStyle.Bold);

    internal WindowCaption(Form owner, bool resizable = false)
    {
        this.owner = owner;
        Dock = DockStyle.Top; Height = 40; Margin = Padding.Empty;
        BackColor = Theme.Surface;
        DoubleBuffered = true;
        minimize = new CaptionButton(CaptionAction.Minimize, owner);
        close = new CaptionButton(CaptionAction.Close, owner);
        // Only resizable windows get a maximize button; fixed dialogs keep their two-button caption.
        if (resizable) maximize = new CaptionButton(CaptionAction.Maximize, owner);
        Controls.Add(minimize);
        if (maximize != null) Controls.Add(maximize);
        Controls.Add(close);
        owner.TextChanged += (_, _) => Invalidate();
        // The caption lives exactly as long as its form, so this handler never outlives either.
        if (maximize != null) owner.SizeChanged += (_, _) => maximize.UpdateState();
    }

    internal void UpdateButtons()
    {
        minimize.Visible = owner.ControlBox && owner.MinimizeBox;
        if (maximize != null) maximize.Visible = owner.ControlBox && owner.MaximizeBox;
        close.Visible = owner.ControlBox;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int S(int value) => UiMetrics.Px(this, value);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var size = S(23);
        AppIcon.Draw(g, new Rectangle(S(8), (Height - size) / 2, size, size));
        TextRenderer.DrawText(g, "SpikeSide", brandFont, new Rectangle(S(35), 0, S(160), Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        using (var divider = new Pen(Theme.Border)) g.DrawLine(divider, S(196), S(12), S(196), Height - S(12));
        var detail = owner.Text.Equals("SpikeSide", StringComparison.OrdinalIgnoreCase) ? "AUTO PICK" : owner.Text;
        var available = Math.Max(0, Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Left).DefaultIfEmpty(Width).Min() - S(216));
        TextRenderer.DrawText(g, detail, detailFont, new Rectangle(S(209), 0, available, Height), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        using var bottom = new Pen(Theme.Border);
        g.DrawLine(bottom, 0, Height - 1, Width, Height - 1);
    }

    protected override void WndProc(ref Message m)
    {
        // Forward empty caption space to the form's native hit test so Windows
        // owns dragging and the system menu.
        if (m.Msg == WmNcHitTest) { m.Result = (IntPtr)HtTransparent; return; }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { brandFont.Dispose(); detailFont.Dispose(); }
        base.Dispose(disposing);
    }
}

internal enum CaptionAction { Minimize, Maximize, Close }

internal sealed class CaptionButton : Control
{
    private readonly CaptionAction action;
    private readonly Form owner;
    private readonly InteractionMotion motion;
    private bool spacePressed;

    internal CaptionButton(CaptionAction action, Form owner)
    {
        this.action = action; this.owner = owner;
        Dock = DockStyle.Right; Width = 46; TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        UpdateState();
        motion = new InteractionMotion(this);
    }

    internal void UpdateState()
    {
        AccessibleName = action switch
        {
            CaptionAction.Minimize => "最小化",
            CaptionAction.Maximize => owner.WindowState == FormWindowState.Maximized ? "元のサイズに戻す" : "最大化",
            _ => "閉じる"
        };
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int value) => UiMetrics.Px(this, value);
        var normal = Parent?.BackColor ?? Theme.Surface;
        var background = InteractionMotion.Blend(normal, action == CaptionAction.Close ? Theme.Red : Theme.Hover, motion.Hover);
        background = InteractionMotion.Blend(background, action == CaptionAction.Close ? Theme.AccentPressed : Theme.Background, motion.Press * .85f);
        e.Graphics.Clear(background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(action == CaptionAction.Close && motion.Hover > .6f ? Theme.OnAccent : Theme.Text, Math.Max(1f, S(1))) { StartCap = LineCap.Square, EndCap = LineCap.Square };
        var x = Width / 2; var y = Height / 2 + (int)Math.Round(S(2) * motion.Press);
        if (action == CaptionAction.Minimize)
            e.Graphics.DrawLine(pen, x - S(5), y + S(3), x + S(5), y + S(3));
        else if (action == CaptionAction.Maximize)
        {
            if (owner.WindowState == FormWindowState.Maximized)
            {
                // Restore glyph: a front square with a second one offset behind it.
                e.Graphics.DrawRectangle(pen, x - S(5), y - S(3), S(8), S(8));
                e.Graphics.DrawLines(pen, [new Point(x - S(3), y - S(3)), new Point(x - S(3), y - S(5)), new Point(x + S(5), y - S(5)), new Point(x + S(5), y + S(3)), new Point(x + S(3), y + S(3))]);
            }
            else e.Graphics.DrawRectangle(pen, x - S(5), y - S(5), S(10), S(10));
        }
        else if (action == CaptionAction.Close)
        {
            e.Graphics.DrawLine(pen, x - S(5), y - S(5), x + S(5), y + S(5));
            e.Graphics.DrawLine(pen, x + S(5), y - S(5), x - S(5), y + S(5));
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -S(4), -S(4)));
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled) return;
        switch (action)
        {
            case CaptionAction.Minimize: owner.WindowState = FormWindowState.Minimized; break;
            case CaptionAction.Maximize:
                owner.WindowState = owner.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
            case CaptionAction.Close: owner.Close(); break;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Focus(); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space) { spacePressed = true; e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.SuppressKeyPress = true; }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space && spacePressed) { spacePressed = false; OnClick(EventArgs.Empty); e.Handled = true; }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); spacePressed = false; Invalidate(); }
    protected override AccessibleObject CreateAccessibilityInstance() => new CaptionButtonAccessibleObject(this);

    private sealed class CaptionButtonAccessibleObject(CaptionButton owner) : ControlAccessibleObject(owner)
    {
        public override string? DefaultAction => "押す";
        public override void DoDefaultAction() { if (owner.CanSelect) owner.OnClick(EventArgs.Empty); }
    }
}
