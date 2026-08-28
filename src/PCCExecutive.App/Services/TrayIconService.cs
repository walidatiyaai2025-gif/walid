using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using PCCExecutive.App.ViewModels;
using Forms = System.Windows.Forms;

namespace PCCExecutive.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _brandIcon;

    public TrayIconService(MainWindow window, MainViewModel viewModel)
    {
        _window = window;
        _brandIcon = CreateBrandIcon();
        _icon = new Forms.NotifyIcon
        {
            Text = "PCC Executive",
            Visible = true,
            Icon = _brandIcon
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open PCC Executive", null, (_, _) => ShowWindow());
        menu.Items.Add("Attention Center", null, (_, _) =>
        {
            viewModel.Navigate(PCCExecutive.App.Presentation.ScreenId.AttentionCenter);
            ShowWindow();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit PCC Executive", null, (_, _) => _window.AllowCloseAndClose());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static Icon CreateBrandIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.FromArgb(5, 10, 19));

        using var purple = new SolidBrush(Color.FromArgb(139, 92, 246));
        using var purpleDeep = new Pen(Color.FromArgb(109, 40, 217), 2.2f);
        var diamond = new[] { new PointF(16, 3), new PointF(28, 10), new PointF(16, 17), new PointF(4, 10) };
        graphics.FillPolygon(purple, diamond);
        graphics.DrawPolygon(purpleDeep, diamond);
        graphics.DrawLine(Pens.White, 10, 11, 16, 14);
        graphics.DrawLine(Pens.White, 16, 14, 22, 11);
        graphics.DrawLine(Pens.White, 16, 14, 16, 27);
        graphics.DrawLine(purpleDeep, 6, 14, 6, 22);
        graphics.DrawLine(purpleDeep, 26, 14, 26, 22);
        graphics.DrawLine(purpleDeep, 6, 22, 16, 28);
        graphics.DrawLine(purpleDeep, 26, 22, 16, 28);

        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { NativeMethods.DestroyIcon(handle); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _brandIcon.Dispose();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
