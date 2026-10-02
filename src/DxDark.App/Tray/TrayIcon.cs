using System.Runtime.InteropServices;
using System.Windows.Threading;
using DxDark.App.ViewModels;
using DxDark.Core.Settings;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DxDark.App.Tray;

/// <summary>The notification-area icon and its menu (Windows Forms, which has the best tray support).</summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly (string Label, double Value)[] BrightnessSteps =
        [("100 %", 1.0), ("75 %", 0.75), ("50 %", 0.5), ("25 %", 0.25), ("10 %", 0.1)];

    private readonly MainViewModel _vm;
    private readonly Dispatcher _ui;
    private readonly Action _showWindow;
    private readonly Action _openCalibration;
    private readonly Action _exit;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly TrayIcons _icons;
    private readonly Drawing.Font _boldFont;
    private readonly Dictionary<string, Drawing.Bitmap> _swatches = [];

    public TrayIcon(MainViewModel vm, Dispatcher ui, Action showWindow, Action openCalibration, Action exit)
    {
        _vm = vm;
        _ui = ui;
        _showWindow = showWindow;
        _openCalibration = openCalibration;
        _exit = exit;
        _icons = new TrayIcons();

        _menu = new Forms.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = true };
        _boldFont = new Drawing.Font(_menu.Font, Drawing.FontStyle.Bold);
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            // Windows Forms cancels the opening of a menu that had no items when the click came in,
            // which made the very first right-click do nothing.
            e.Cancel = false;
        };
        BuildMenu();

        _icon = new Forms.NotifyIcon
        {
            Icon = _icons.Active,
            Text = "DX Dark",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                _showWindow();
            }
        };
        _icon.BalloonTipClicked += (_, _) => _showWindow();

        vm.Controller.StateChanged += () => _ui.BeginInvoke(UpdateIcon);
        UpdateIcon();
    }

    public void ShowNotice(string message) =>
        _icon.ShowBalloonTip(6000, "DX Dark", message, Forms.ToolTipIcon.Info);

    private void UpdateIcon()
    {
        var c = _vm.Controller;
        bool active = c.IsConnected && !c.IsSuspended && c.Settings.Lighting.Mode is not (LightMode.Off or LightMode.Controller);
        _icon.Icon = !c.IsConnected ? _icons.Disconnected : active ? _icons.Active : _icons.Dim;

        // Tooltips are limited to 63 characters.
        string text = c.IsConnected ? $"DX Dark — {_vm.ModeName}" : "DX Dark — strip not connected";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void BuildMenu()
    {
        var c = _vm.Controller;
        LightingSettings lighting = c.Settings.Lighting;
        foreach (Forms.ToolStripItem old in _menu.Items.Cast<Forms.ToolStripItem>().ToList())
        {
            Release(old);
        }

        _menu.Items.Clear();

        var header = Item(c.IsConnected ? $"DX Dark · {c.StripInfo?.LedCount} LEDs" : "DX Dark · strip not connected", null);
        header.Enabled = false;
        header.Font = _boldFont;
        _menu.Items.Add(header);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var screen = Item("Screen", () => Do(() => c.SetMode(LightMode.ScreenSync)));
        screen.Checked = lighting.Mode == LightMode.ScreenSync;
        _menu.Items.Add(screen);

        var effects = Item("Effect", null);
        foreach (EffectTile tile in _vm.Effects.Tiles)
        {
            var item = Item(tile.Name, () => Do(() => c.SetEffect(tile.Id)));
            item.Checked = lighting.Mode == LightMode.Effect && lighting.EffectId == tile.Id;
            if (tile.Id == Core.Effects.BuiltInEffects.Solid)
            {
                item.Image = Swatch(lighting.SolidColor);
            }

            effects.DropDownItems.Add(item);
        }

        effects.Checked = lighting.Mode == LightMode.Effect;
        _menu.Items.Add(effects);

        var off = Item("Off", () => Do(() => c.SetMode(LightMode.Off)));
        off.Checked = lighting.Mode == LightMode.Off;
        _menu.Items.Add(off);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var presets = Item("Preset", null);
        foreach (string name in c.Settings.Profiles.Select(p => p.Name).ToList())
        {
            var item = Item(name, () => Do(() => c.ActivateProfile(name)));
            item.Checked = c.Settings.ActiveProfile == name;
            presets.DropDownItems.Add(item);
        }

        _menu.Items.Add(presets);

        var brightness = Item("Brightness", null);
        double current = c.Settings.GetActiveProfile().Brightness;
        foreach ((string label, double value) in BrightnessSteps)
        {
            var item = Item(label, () => Do(() => _vm.Presets.SetBrightness(value)));
            item.Checked = Math.Abs(current - value) < 0.02;
            brightness.DropDownItems.Add(item);
        }

        _menu.Items.Add(brightness);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(Item("Calibration", _openCalibration));
        var panel = Item("Control panel", _showWindow);
        panel.Font = _boldFont;
        _menu.Items.Add(panel);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(Item("Quit DX Dark", _exit));
    }

    /// <summary>Disposes a menu item from the previous opening (the shared swatch images and font are kept).</summary>
    private static void Release(Forms.ToolStripItem item)
    {
        if (item is Forms.ToolStripMenuItem menuItem)
        {
            foreach (Forms.ToolStripItem child in menuItem.DropDownItems.Cast<Forms.ToolStripItem>().ToList())
            {
                Release(child);
            }
        }

        item.Image = null;
        item.Dispose();
    }

    /// <summary>Runs a menu action, then refreshes the control panel to match.</summary>
    private void Do(Action action)
    {
        action();
        _vm.RefreshState();
    }

    private static Forms.ToolStripMenuItem Item(string text, Action? onClick)
    {
        var item = new Forms.ToolStripMenuItem(text) { ForeColor = DarkMenuRenderer.Text };
        if (onClick is not null)
        {
            item.Click += (_, _) => onClick();
        }

        return item;
    }

    private Drawing.Bitmap Swatch(string hex)
    {
        if (!_swatches.TryGetValue(hex, out Drawing.Bitmap? bitmap))
        {
            bitmap = new Drawing.Bitmap(16, 16);
            using var g = Drawing.Graphics.FromImage(bitmap);
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(hex));
            g.FillEllipse(brush, 1, 1, 14, 14);
            _swatches[hex] = bitmap;
        }

        return bitmap;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _boldFont.Dispose();
        _icons.Dispose();
        foreach (Drawing.Bitmap bitmap in _swatches.Values)
        {
            bitmap.Dispose();
        }
    }
}

/// <summary>Tray icons drawn from the app icon: full color, dimmed (lights off) and gray (no strip).</summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly List<IntPtr> _handles = [];

    public TrayIcons()
    {
        int size = Forms.SystemInformation.SmallIconSize.Width;
        using Drawing.Bitmap source = LoadAppIcon(size);
        Active = Make(source, 1f, saturation: 1f);
        Dim = Make(source, 0.55f, saturation: 1f);
        Disconnected = Make(source, 0.6f, saturation: 0f);
    }

    public Drawing.Icon Active { get; }

    public Drawing.Icon Dim { get; }

    public Drawing.Icon Disconnected { get; }

    private static Drawing.Bitmap LoadAppIcon(int size)
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/DxDark.ico"));
        using var stream = info.Stream;
        using var icon = new Drawing.Icon(stream, new Drawing.Size(size, size));
        return icon.ToBitmap();
    }

    private Drawing.Icon Make(Drawing.Bitmap source, float opacity, float saturation)
    {
        using var bitmap = new Drawing.Bitmap(source.Width, source.Height);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            float s = saturation, r = 0.299f * (1 - s), gr = 0.587f * (1 - s), b = 0.114f * (1 - s);
            var matrix = new Drawing.Imaging.ColorMatrix(
            [
                [r + s, r, r, 0, 0],
                [gr, gr + s, gr, 0, 0],
                [b, b, b + s, 0, 0],
                [0, 0, 0, opacity, 0],
                [0, 0, 0, 0, 1],
            ]);
            using var attributes = new Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(matrix);
            g.DrawImage(source, new Drawing.Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height, Drawing.GraphicsUnit.Pixel, attributes);
        }

        IntPtr handle = bitmap.GetHicon();
        _handles.Add(handle);
        return Drawing.Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        Active.Dispose();
        Dim.Dispose();
        Disconnected.Dispose();
        foreach (IntPtr handle in _handles)
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>Dark colors for the tray menu, matching the control panel.</summary>
internal sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    public static readonly Drawing.Color Text = Drawing.Color.FromArgb(233, 236, 242);
    private static readonly Drawing.Color Background = Drawing.Color.FromArgb(26, 30, 38);
    private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(42, 47, 60);
    private static readonly Drawing.Color Border = Drawing.Color.FromArgb(48, 54, 68);
    private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(139, 124, 255);

    public DarkMenuRenderer()
        : base(new Colors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Drawing.Color.FromArgb(156, 165, 181);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        // A small accent dot instead of the default black tick, which is invisible on dark menus.
        Drawing.Rectangle r = e.ImageRectangle;
        int d = Math.Min(r.Width, r.Height) / 2;
        e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new Drawing.SolidBrush(Accent);
        e.Graphics.FillEllipse(brush, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
    }

    private sealed class Colors : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Background;

        public override Drawing.Color ImageMarginGradientBegin => Background;

        public override Drawing.Color ImageMarginGradientMiddle => Background;

        public override Drawing.Color ImageMarginGradientEnd => Background;

        public override Drawing.Color MenuBorder => Border;

        public override Drawing.Color MenuItemBorder => Hover;

        public override Drawing.Color MenuItemSelected => Hover;

        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;

        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;

        public override Drawing.Color MenuItemPressedGradientBegin => Hover;

        public override Drawing.Color MenuItemPressedGradientEnd => Hover;

        public override Drawing.Color SeparatorDark => Border;

        public override Drawing.Color SeparatorLight => Background;

        public override Drawing.Color CheckBackground => Background;

        public override Drawing.Color CheckSelectedBackground => Hover;

        public override Drawing.Color CheckPressedBackground => Hover;
    }
}
