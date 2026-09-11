using System.Drawing;
using System.Runtime.InteropServices;
using LanFileDrop.Core;
using Forms = System.Windows.Forms;

namespace LanFileDrop.Desktop;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _baseIcon;
    private Icon? _dynamicIcon;

    public TrayService()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mở LanFileDrop", null, (_, _) => ShowWindow());
        menu.Items.Add("Thoát", null, (_, _) => System.Windows.Application.Current.Shutdown());
        _baseIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        _icon = new Forms.NotifyIcon { Text = "LanFileDrop", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ShowWindow();
        SetActiveCount(0);
    }

    public void SetActiveCount(int count)
    {
        if (count <= 0)
        {
            var previousIcon = _dynamicIcon;
            _dynamicIcon = null;
            _icon.Icon = _baseIcon;
            previousIcon?.Dispose();
            return;
        }
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(Color.FromArgb(99, 102, 241)))
        using (var text = new SolidBrush(Color.White))
        using (var font = new Font("Segoe UI", count > 9 ? 8 : 10, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.DrawIcon(_baseIcon, new Rectangle(0, 0, 32, 32));
            graphics.FillEllipse(background, 17, 17, 15, 15);
            var label = Math.Min(count, 99).ToString();
            var size = graphics.MeasureString(label, font);
            graphics.DrawString(label, font, text, 24.5f - size.Width / 2, 24.5f - size.Height / 2);
        }
        var handle = bitmap.GetHicon();
        using var iconFromHandle = Icon.FromHandle(handle);
        var next = (Icon)iconFromHandle.Clone();
        DestroyIcon(handle);
        var previousDynamicIcon = _dynamicIcon;
        _dynamicIcon = next;
        _icon.Icon = next;
        previousDynamicIcon?.Dispose();
    }

    public void ShowIncoming(IncomingTransferRequest request)
    {
        _icon.BalloonTipTitle = $"{request.SenderName} muốn gửi file";
        _icon.BalloonTipText = $"{request.FileName} • {request.Size / 1024d / 1024d:0.0} MB";
        _icon.ShowBalloonTip(5000);
    }

    private static void ShowWindow()
    {
        var window = System.Windows.Application.Current.MainWindow;
        window?.Show();
        if (window is not null) { window.WindowState = System.Windows.WindowState.Normal; window.Activate(); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _dynamicIcon?.Dispose();
        if (!ReferenceEquals(_baseIcon, SystemIcons.Application)) _baseIcon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
