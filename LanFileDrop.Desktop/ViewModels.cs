using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LanFileDrop.Core;
using LanFileDrop.Network;
using Microsoft.Win32;
using QRCoder;

namespace LanFileDrop.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "LanFileDrop", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class TransferItemViewModel : ObservableObject
{
    private TransferProgress _progress;
    public TransferControl? Control { get; init; }
    public TransferItemViewModel(TransferProgress progress) => _progress = progress;
    public Guid Id => _progress.TransferId;
    public string FileName => _progress.FileName;
    public string Peer => _progress.PeerName;
    public double Percentage => _progress.Percentage;
    public string PercentText => $"{Percentage:0}%";
    public string SpeedText => _progress.Status == TransferStatus.Completed ? "Đã hoàn tất" : $"{_progress.BytesPerSecond / 1024d / 1024d:0.0} MB/s";
    public string EtaText => _progress.Eta is null ? "ETA: --" : $"ETA: {FormatDuration(_progress.Eta.Value)}";
    public string DirectionText => _progress.Direction == TransferDirection.Send ? "GỬI" : "NHẬN";
    public TransferDirection Direction => _progress.Direction;
    public TransferStatus Status => _progress.Status;
    public string StatusText => _progress.Status switch
    {
        TransferStatus.Completed => "✓ Hoàn tất",
        TransferStatus.Cancelled => "Đã hủy",
        TransferStatus.Failed => "Có lỗi",
        TransferStatus.Paused => "Tạm dừng",
        TransferStatus.Retrying => "Đang kết nối lại…",
        _ => Direction == TransferDirection.Send ? "Đang gửi" : "Đang nhận"
    };
    public string HashText => string.IsNullOrWhiteSpace(_progress.Sha256) ? "" : $"SHA-256: {_progress.Sha256[..Math.Min(16, _progress.Sha256.Length)]}…";
    public bool CanControl => Control is not null && Status is TransferStatus.Running or TransferStatus.Retrying;
    public string PauseText => Control?.IsPaused == true ? "▶ Tiếp tục" : "⏸ Tạm dừng";
    public ICommand PauseCommand => new RelayCommand(() => { if (Control?.IsPaused == true) Control.Resume(); else Control?.Pause(); Raise(nameof(PauseText)); });
    public ICommand CancelCommand => new RelayCommand(() => Control?.Cancel());

    public void Update(TransferProgress progress)
    {
        _progress = progress;
        Raise(string.Empty);
    }

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
}

public sealed class HistoryItemViewModel
{
    public TransferRecord Record { get; }
    public HistoryItemViewModel(TransferRecord record) => Record = record;
    public string FileName => Record.FileName;
    public long Size => Record.Size;
    public TransferDirection Direction => Record.Direction;
    public TransferStatus Status => Record.Status;
    public string PeerName => Record.PeerName;
    public string PeerDetails => string.IsNullOrWhiteSpace(Record.PeerAddress)
        ? Record.PeerName : $"{Record.PeerName}  •  {Record.PeerAddress}";
    public string DirectionSymbol => Direction == TransferDirection.Send ? "↑" : "↓";
    public string DirectionText => Direction == TransferDirection.Send ? "ĐÃ GỬI" : "ĐÃ NHẬN";
    public string StatusText => Status switch
    {
        TransferStatus.Completed => "Hoàn tất",
        TransferStatus.Running => "Đang truyền",
        TransferStatus.Retrying => "Kết nối lại",
        TransferStatus.Paused => "Tạm dừng",
        TransferStatus.Declined => "Đã từ chối",
        TransferStatus.Cancelled => "Đã hủy",
        TransferStatus.Failed => "Có lỗi",
        _ => "Đang chờ"
    };
    public string TimeText
    {
        get
        {
            var local = Record.StartedAt.ToLocalTime();
            var prefix = local.Date == DateTimeOffset.Now.Date ? "Hôm nay"
                : local.Date == DateTimeOffset.Now.Date.AddDays(-1) ? "Hôm qua"
                : local.ToString("dd/MM/yyyy");
            return $"{prefix}  •  {local:HH:mm}";
        }
    }
    public string HashText => string.IsNullOrWhiteSpace(Record.Sha256) ? ""
        : $"SHA-256  {Record.Sha256[..Math.Min(20, Record.Sha256.Length)]}…";
    public bool HasHash => !string.IsNullOrWhiteSpace(Record.Sha256);
    public bool HasError => !string.IsNullOrWhiteSpace(Record.Error);
    public string ErrorText => Record.Error ?? "";
}

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DiscoveryService _discovery;
    private readonly FileTransferService _transfers;
    private readonly HistoryRepository _history;
    private readonly TrayService _tray;
    private readonly WebShareService _webShare;
    private readonly Dictionary<Guid, TransferControl> _controls = new();
    private readonly Queue<PendingIncoming> _incomingQueue = new();
    private IReadOnlyList<DeviceInfo> _nativeDevices = Array.Empty<DeviceInfo>();
    private IReadOnlyList<DeviceInfo> _webDevices = Array.Empty<DeviceInfo>();
    private readonly Dictionary<string, DeviceInfo> _manualDevices = new(StringComparer.OrdinalIgnoreCase);
    private DeviceInfo? _selectedDevice;
    private IncomingTransferRequest? _incoming;
    private PendingIncoming? _activeIncoming;
    private bool _historyOpen;
    private bool _settingsOpen;
    private bool _webOpen;
    private bool _isDark;
    private DateTime? _historyFrom;
    private int _historyDirection;
    private string _minimumSizeMb = "";
    private string _statusText = "Đang tìm thiết bị trong mạng LAN…";
    private string _webUrl = "Đang khởi động web LAN…";
    private BitmapImage? _webQrImage;
    private string _manualAddress = "";
    private string _manualDeviceName = "";
    private string _manualConnectionMessage = "Nhập IPv4 của máy bên kia, ví dụ 26.10.20.30 hoặc 26.10.20.30:49495.";

    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    public ObservableCollection<TransferItemViewModel> TransferItems { get; } = new();
    public ObservableCollection<HistoryItemViewModel> HistoryItems { get; } = new();
    public ObservableCollection<TextMessage> TextMessages { get; } = new();
    public DeviceInfo? SelectedDevice { get => _selectedDevice; set { if (Set(ref _selectedDevice, value)) RaiseCommandStates(); } }
    public IncomingTransferRequest? Incoming { get => _incoming; private set { Set(ref _incoming, value); Raise(nameof(IsToastVisible)); } }
    public bool IsToastVisible => Incoming is not null;
    public bool IsHistoryOpen { get => _historyOpen; set => Set(ref _historyOpen, value); }
    public bool IsSettingsOpen { get => _settingsOpen; set => Set(ref _settingsOpen, value); }
    public bool IsWebOpen { get => _webOpen; set => Set(ref _webOpen, value); }
    public bool IsDark { get => _isDark; set { if (Set(ref _isDark, value)) { ThemeService.Apply(value); ThemeService.SaveDarkMode(value); } } }
    public DateTime? HistoryFrom { get => _historyFrom; set => Set(ref _historyFrom, value); }
    public int HistoryDirection { get => _historyDirection; set => Set(ref _historyDirection, value); }
    public string MinimumSizeMb { get => _minimumSizeMb; set => Set(ref _minimumSizeMb, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string LocalDeviceName => Environment.MachineName;
    public string WebUrl { get => _webUrl; private set => Set(ref _webUrl, value); }
    public BitmapImage? WebQrImage { get => _webQrImage; private set => Set(ref _webQrImage, value); }
    public string ManualAddress { get => _manualAddress; set => Set(ref _manualAddress, value); }
    public string ManualDeviceName { get => _manualDeviceName; set => Set(ref _manualDeviceName, value); }
    public string ManualConnectionMessage { get => _manualConnectionMessage; private set => Set(ref _manualConnectionMessage, value); }
    public bool HasTransfers => TransferItems.Count > 0;
    public bool HasTextMessages => TextMessages.Count > 0;
    public bool HasHistoryItems => HistoryItems.Count > 0;
    public string HistorySummaryText { get; private set; } = "Chưa có hoạt động phù hợp";
    public ICommand BrowseCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand SendTextCommand { get; }
    public ICommand AcceptCommand { get; }
    public ICommand DeclineCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand CloseHistoryCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public ICommand ApplyHistoryFilterCommand { get; }
    public ICommand ClearHistoryFilterCommand { get; }
    public ICommand OpenWebCommand { get; }
    public ICommand CloseWebCommand { get; }
    public ICommand AddManualDeviceCommand { get; }

    public MainViewModel(DiscoveryService discovery, FileTransferService transfers, HistoryRepository history,
        TrayService tray, WebShareService webShare)
    {
        _discovery = discovery; _transfers = transfers; _history = history; _tray = tray; _webShare = webShare;
        BrowseCommand = new AsyncRelayCommand(BrowseAsync, () => SelectedDevice is not null);
        BrowseFolderCommand = new AsyncRelayCommand(BrowseFolderAsync, () => SelectedDevice is not null);
        SendTextCommand = new AsyncRelayCommand(SendTextAsync, () => SelectedDevice is not null);
        AcceptCommand = new RelayCommand(AcceptIncoming);
        DeclineCommand = new RelayCommand(DeclineIncoming);
        OpenHistoryCommand = new AsyncRelayCommand(async () => { IsHistoryOpen = true; await LoadHistoryAsync(); });
        CloseHistoryCommand = new RelayCommand(() => IsHistoryOpen = false);
        OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
        ApplyHistoryFilterCommand = new AsyncRelayCommand(LoadHistoryAsync);
        ClearHistoryFilterCommand = new AsyncRelayCommand(ClearHistoryFilterAsync);
        OpenWebCommand = new RelayCommand(() => IsWebOpen = true);
        CloseWebCommand = new RelayCommand(() => IsWebOpen = false);
        AddManualDeviceCommand = new RelayCommand(AddManualDevice);
        _isDark = ThemeService.GetInitialDarkMode();
        ThemeService.Apply(_isDark);
        discovery.DevicesChanged += OnDevicesChanged;
        transfers.ProgressChanged += OnProgressChanged;
        transfers.IncomingTransfer += OnIncomingTransferAsync;
        transfers.TextReceived += OnTextMessage;
        webShare.ProgressChanged += OnProgressChanged;
        webShare.IncomingUpload += OnIncomingTransferAsync;
        webShare.WebClientsChanged += OnWebClientsChanged;
        webShare.TextMessageReceived += OnTextMessage;
    }

    public async Task StartAsync()
    {
        _transfers.Start();
        _discovery.Start(FileTransferService.DefaultPort);
        await _webShare.StartAsync();
        WebUrl = _webShare.WebUrl;
        WebQrImage = CreateQrImage(WebUrl);
    }

    public async Task SendPathsAsync(IEnumerable<string> paths)
    {
        var device = SelectedDevice ?? throw new InvalidOperationException("Hãy chọn một thiết bị trước khi gửi.");
        foreach (var path in paths)
        {
            if (device.Kind == DeviceKind.WebBrowser) await _webShare.PublishPathAsync(device, path);
            else await StartSendAsync(device, path);
        }
    }

    private async Task StartSendAsync(DeviceInfo device, string path)
    {
        var id = Guid.NewGuid();
        var control = new TransferControl();
        _controls[id] = control;
        var displayName = Directory.Exists(path)
            ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + ".zip"
            : Path.GetFileName(path);
        var size = File.Exists(path) ? new FileInfo(path).Length : 0;
        OnProgressChanged(this, new TransferProgress(id, displayName, size, 0, 0, null,
            TransferDirection.Send, TransferStatus.Running, device.Name));
        try { await _transfers.SendPathAsync(device, path, control, id); }
        finally { _controls.Remove(id); control.Dispose(); }
    }

    private Task BrowseAsync()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Chọn file để gửi" };
        return dialog.ShowDialog() == true ? SendPathsAsync(dialog.FileNames) : Task.CompletedTask;
    }

    private Task BrowseFolderAsync()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Chọn thư mục để gửi" };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? SendPathsAsync(new[] { dialog.SelectedPath }) : Task.CompletedTask;
    }

    private async Task SendTextAsync()
    {
        var dialog = new TextSendWindow { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.MessageText)) return;
        var device = SelectedDevice!;
        if (device.Kind == DeviceKind.WebBrowser)
        {
            await _webShare.PublishTextAsync(device, dialog.MessageText);
            return;
        }
        using var control = new TransferControl();
        await _transfers.SendTextAsync(device, dialog.MessageText, control);
        AddTextMessage(new TextMessage(Guid.NewGuid(), dialog.MessageText, $"Bạn → {device.Name}",
            TransferDirection.Send, DateTimeOffset.Now, null, device.Id));
    }

    private Task<TransferDecision> OnIncomingTransferAsync(IncomingTransferRequest request)
    {
        var tcs = new TaskCompletionSource<TransferDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Current.Dispatcher.Invoke(() =>
        {
            _incomingQueue.Enqueue(new PendingIncoming(request, tcs));
            ShowNextIncoming();
        });
        return tcs.Task;
    }

    private void ShowNextIncoming()
    {
        if (_activeIncoming is not null || !_incomingQueue.TryDequeue(out var pending)) return;
        _activeIncoming = pending;
        Incoming = pending.Request;
        _tray.ShowIncoming(pending.Request);
        var window = Application.Current.MainWindow;
        if (window is not null)
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
        _ = AutoDeclineAsync(pending);
    }

    private async Task AutoDeclineAsync(PendingIncoming pending)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        Application.Current.Dispatcher.Invoke(() => CompleteIncoming(pending, false));
    }

    private void AcceptIncoming()
    {
        if (_activeIncoming is not null) CompleteIncoming(_activeIncoming, true);
    }

    private void DeclineIncoming()
    {
        if (_activeIncoming is not null) CompleteIncoming(_activeIncoming, false);
    }

    private void CompleteIncoming(PendingIncoming pending, bool accepted)
    {
        if (!ReferenceEquals(_activeIncoming, pending)) return;
        pending.Decision.TrySetResult(new TransferDecision(accepted));
        _activeIncoming = null;
        Incoming = null;
        ShowNextIncoming();
    }

    private void OnDevicesChanged(object? sender, IReadOnlyList<DeviceInfo> devices) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            _nativeDevices = devices;
            RefreshDevices();
        });

    private void OnWebClientsChanged(object? sender, IReadOnlyList<DeviceInfo> devices) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            _webDevices = devices;
            RefreshDevices();
        });

    private void RefreshDevices()
    {
        var selectedId = SelectedDevice?.Id;
        Devices.Clear();
        foreach (var device in _nativeDevices.Concat(_webDevices).Concat(_manualDevices.Values)
                     .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Name))
            Devices.Add(device);
        SelectedDevice = Devices.FirstOrDefault(x => x.Id == selectedId) ?? Devices.FirstOrDefault();
        StatusText = Devices.Count == 0 ? "Đang tìm thiết bị trong mạng LAN…" : $"Đã tìm thấy {Devices.Count} thiết bị";
    }

    private void AddManualDevice()
    {
        var input = ManualAddress.Trim();
        var port = FileTransferService.DefaultPort;
        var addressText = input;
        var separator = input.LastIndexOf(':');
        if (separator > 0)
        {
            addressText = input[..separator].Trim();
            if (!int.TryParse(input[(separator + 1)..], out port) || port is < 1 or > 65535)
            {
                ManualConnectionMessage = "Port không hợp lệ. Hãy dùng giá trị từ 1 đến 65535.";
                return;
            }
        }
        if (!IPAddress.TryParse(addressText, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            ManualConnectionMessage = "IPv4 không hợp lệ. Ví dụ: 26.10.20.30 hoặc 192.168.1.15.";
            return;
        }

        var id = $"manual:{address}:{port}";
        var name = string.IsNullOrWhiteSpace(ManualDeviceName) ? $"Máy {address}" : ManualDeviceName.Trim();
        if (name.Length > 40) name = name[..40];
        _manualDevices[id] = new DeviceInfo(id, name, address, port, false, DateTimeOffset.UtcNow, DeviceKind.ManualIp);
        RefreshDevices();
        SelectedDevice = Devices.First(x => x.Id == id);
        ManualConnectionMessage = $"✓ Đã thêm {name} ({address}:{port}). Chọn file để kiểm tra kết nối.";
    }

    private void OnProgressChanged(object? sender, TransferProgress progress) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            var item = TransferItems.FirstOrDefault(x => x.Id == progress.TransferId);
            if (item is null)
            {
                _controls.TryGetValue(progress.TransferId, out var control);
                item = new TransferItemViewModel(progress) { Control = control };
                TransferItems.Insert(0, item);
                Raise(nameof(HasTransfers));
            }
            else item.Update(progress);
            var activeCount = TransferItems.Count(x => x.Status is TransferStatus.Running or TransferStatus.Retrying);
            _tray.SetActiveCount(activeCount);
            _discovery.SetBusy(activeCount > 0);
        });

    private void OnTextMessage(object? sender, TextMessage message) =>
        Application.Current.Dispatcher.Invoke(() => AddTextMessage(message));

    private void AddTextMessage(TextMessage message)
    {
        TextMessages.Add(message);
        while (TextMessages.Count > 100) TextMessages.RemoveAt(0);
        Raise(nameof(HasTextMessages));
    }

    private async Task LoadHistoryAsync()
    {
        TransferDirection? direction = HistoryDirection switch { 1 => TransferDirection.Send, 2 => TransferDirection.Receive, _ => null };
        long? minimum = double.TryParse(MinimumSizeMb, out var mb) ? (long)(mb * 1024 * 1024) : null;
        var records = await _history.QueryAsync(HistoryFrom, direction, minimum);
        HistoryItems.Clear();
        foreach (var record in records) HistoryItems.Add(new HistoryItemViewModel(record));
        var sent = records.Count(x => x.Direction == TransferDirection.Send);
        var received = records.Count - sent;
        HistorySummaryText = records.Count == 0
            ? "Không có hoạt động phù hợp với bộ lọc"
            : $"{records.Count} hoạt động  •  {sent} gửi  •  {received} nhận";
        Raise(nameof(HistorySummaryText));
        Raise(nameof(HasHistoryItems));
    }

    private async Task ClearHistoryFilterAsync()
    {
        HistoryFrom = null;
        HistoryDirection = 0;
        MinimumSizeMb = "";
        await LoadHistoryAsync();
    }

    private void RaiseCommandStates()
    {
        (BrowseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (BrowseFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SendTextCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static BitmapImage CreateQrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(12, drawQuietZones: true);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public async ValueTask DisposeAsync()
    {
        _activeIncoming?.Decision.TrySetResult(new TransferDecision(false));
        while (_incomingQueue.TryDequeue(out var pending)) pending.Decision.TrySetResult(new TransferDecision(false));
        await _discovery.DisposeAsync();
        await _transfers.DisposeAsync();
        await _webShare.DisposeAsync();
        _tray.Dispose();
    }

    private sealed record PendingIncoming(IncomingTransferRequest Request, TaskCompletionSource<TransferDecision> Decision);
}
