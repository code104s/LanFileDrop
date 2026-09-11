using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LanFileDrop.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

namespace LanFileDrop.Network;

public sealed class WebShareService : IAsyncDisposable
{
    public const int DefaultPort = 49500;
    private readonly HistoryRepository _history;
    private readonly string _storageDirectory;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, AcceptedOffer> _offers = new();
    private readonly ConcurrentDictionary<string, DeviceInfo> _webClients = new();
    private readonly ConcurrentQueue<TextMessage> _messages = new();
    private readonly CancellationTokenSource _stop = new();
    private WebApplication? _app;
    private Task? _cleanupLoop;

    public event Func<IncomingTransferRequest, Task<TransferDecision>>? IncomingUpload;
    public event EventHandler<TransferProgress>? ProgressChanged;
    public event EventHandler<IReadOnlyList<DeviceInfo>>? WebClientsChanged;
    public event EventHandler<TextMessage>? TextMessageReceived;
    public string WebUrl { get; private set; } = "";

    public WebShareService(HistoryRepository history, string? storageDirectory = null, int port = DefaultPort)
    {
        _history = history;
        _port = port;
        _storageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanFileDrop");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) return;
        Directory.CreateDirectory(_storageDirectory);
        CleanupPartials(_storageDirectory);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WebShareService).Assembly.GetName().Name
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_port);
            options.Limits.MaxRequestBodySize = null;
        });
        var app = builder.Build();
        app.MapGet("/", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content(MobilePage, "text/html; charset=utf-8");
        });
        app.MapGet("/api/files", ListFiles);
        app.MapGet("/api/messages", ListMessages);
        app.MapGet("/download/{name}", DownloadFile);
        app.MapPost("/api/register", (Delegate)RegisterAsync);
        app.MapPost("/api/heartbeat", (Delegate)RegisterAsync);
        app.MapPost("/api/offer", (Delegate)OfferAsync);
        app.MapGet("/api/upload/{token}/status", (Delegate)UploadStatus);
        app.MapPut("/api/upload/{token}", (Delegate)UploadAsync);
        await app.StartAsync(cancellationToken);
        _app = app;
        WebUrl = $"http://{FindLanAddress()}:{_port}";
        _cleanupLoop = CleanupLoopAsync(_stop.Token);
    }

    public async Task PublishPathAsync(DeviceInfo device, string path, CancellationToken cancellationToken = default)
    {
        string? temporary = null;
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        try
        {
            if (Directory.Exists(path))
            {
                temporary = Path.Combine(Path.GetTempPath(), $"LanFileDrop-Web-{Guid.NewGuid():N}.zip");
                System.IO.Compression.ZipFile.CreateFromDirectory(path, temporary,
                    System.IO.Compression.CompressionLevel.Fastest, false);
                path = temporary;
                name += ".zip";
            }
            var info = new FileInfo(path);
            var id = Guid.NewGuid();
            var started = DateTimeOffset.UtcNow;
            var destination = UniquePath(_storageDirectory, name);
            var partial = destination + ".partial";
            var watch = Stopwatch.StartNew();
            long copied = 0;
            string? hashText = null;
            try
            {
                if (!FileTransferService.HasEnoughDiskSpace(_storageDirectory, info.Length, out var available))
                    throw new IOException($"Không đủ dung lượng trống (còn {available / 1024d / 1024d / 1024d:0.0} GB).");
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[FileTransferService.ChunkSize];
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    FileTransferService.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    FileTransferService.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer, 0, read);
                    copied += read;
                    var speed = copied / Math.Max(watch.Elapsed.TotalSeconds, 0.001);
                    TimeSpan? eta = speed <= 0 ? null : TimeSpan.FromSeconds((info.Length - copied) / speed);
                    ProgressChanged?.Invoke(this, new TransferProgress(id, name, info.Length, copied, speed, eta,
                        TransferDirection.Send, TransferStatus.Running, device.Name));
                }
                await output.FlushAsync(cancellationToken);
                await output.DisposeAsync();
                File.Move(partial, destination);
                hashText = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                ProgressChanged?.Invoke(this, new TransferProgress(id, name, info.Length, copied,
                    copied / Math.Max(watch.Elapsed.TotalSeconds, 0.001), TimeSpan.Zero, TransferDirection.Send,
                    TransferStatus.Completed, device.Name, hashText));
                await _history.AddAsync(new TransferRecord(0, name, info.Length, device.Name, device.Address.ToString(),
                    TransferDirection.Send, TransferStatus.Completed, hashText, started, DateTimeOffset.UtcNow, null), cancellationToken);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
        }
    }

    public async Task PublishTextAsync(DeviceInfo device, string text, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var message = new TextMessage(Guid.NewGuid(), text, Environment.MachineName, TransferDirection.Send,
            DateTimeOffset.UtcNow, null, NormalizeWebClientId(device.Id));
        AddMessage(message);
        await _history.AddAsync(new TransferRecord(0, "Văn bản", bytes.LongLength, device.Name,
            device.Address.ToString(), TransferDirection.Send, TransferStatus.Completed, hash,
            message.Timestamp, DateTimeOffset.UtcNow, null), cancellationToken);
    }

    private IResult ListFiles()
    {
        var files = new DirectoryInfo(_storageDirectory).EnumerateFiles()
            .Where(x => !x.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.LastWriteTimeUtc).Take(50)
            .Select(x => new { x.Name, x.Length, modified = x.LastWriteTimeUtc }).ToArray();
        return Results.Json(files);
    }

    private IResult ListMessages(string? clientId)
    {
        var normalized = NormalizeWebClientId(clientId);
        var messages = _messages.Where(x =>
                x.TargetDeviceId is null || x.TargetDeviceId == normalized || x.SourceDeviceId == normalized)
            .OrderBy(x => x.Timestamp).TakeLast(100)
            .Select(x => new { x.Id, x.Content, x.SenderName, direction = x.Direction.ToString(), x.Timestamp })
            .ToArray();
        return Results.Json(messages);
    }

    private IResult DownloadFile(string name)
    {
        var safeName = Path.GetFileName(name);
        var path = Path.Combine(_storageDirectory, safeName);
        return File.Exists(path)
            ? Results.File(path, "application/octet-stream", safeName, enableRangeProcessing: true)
            : Results.NotFound();
    }

    private async Task<IResult> OfferAsync(HttpContext context)
    {
        var offer = await context.Request.ReadFromJsonAsync<OfferDto>(context.RequestAborted);
        if (offer is null || offer.Size < 0 || string.IsNullOrWhiteSpace(offer.Name))
            return Results.BadRequest(new { error = "Thông tin file không hợp lệ." });
        if (offer.IsText && offer.Size > 1024 * 1024)
            return Results.BadRequest(new { error = "Văn bản vượt quá giới hạn 1 MB." });
        var safeName = Path.GetFileName(offer.Name);
        var address = context.Connection.RemoteIpAddress ?? IPAddress.None;
        var request = new IncomingTransferRequest(Guid.NewGuid(), safeName, offer.Size,
            offer.DeviceName?.Trim() is { Length: > 0 } value ? value : "Thiết bị di động", address, offer.IsText);
        var handler = IncomingUpload;
        var decision = handler is null ? new TransferDecision(false) : await handler(request);
        if (!decision.Accepted) return Results.Json(new { error = "Yêu cầu đã bị từ chối." }, statusCode: 403);
        var directory = decision.DestinationDirectory ?? _storageDirectory;
        Directory.CreateDirectory(directory);
        if (!FileTransferService.HasEnoughDiskSpace(directory, offer.Size, out var available))
            return Results.Json(new
            {
                error = $"Máy tính không đủ dung lượng trống (còn {available / 1024d / 1024d / 1024d:0.0} GB)."
            }, statusCode: StatusCodes.Status507InsufficientStorage);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var destination = UniquePath(directory, safeName);
        _offers[token] = new AcceptedOffer(request, directory, destination, destination + ".partial",
            DateTimeOffset.UtcNow.AddMinutes(30), NormalizeWebClientId(offer.ClientId));
        CleanupExpiredOffers();
        return Results.Json(new { token });
    }

    private async Task<IResult> RegisterAsync(HttpContext context)
    {
        var registration = await context.Request.ReadFromJsonAsync<RegistrationDto>(context.RequestAborted);
        if (registration is null || string.IsNullOrWhiteSpace(registration.Id)) return Results.BadRequest();
        var id = new string(registration.Id.Where(char.IsLetterOrDigit).Take(64).ToArray());
        if (id.Length < 8) return Results.BadRequest();
        var name = string.IsNullOrWhiteSpace(registration.Name) ? "iPhone (Safari)" : registration.Name.Trim();
        if (name.Length > 40) name = name[..40];
        var address = context.Connection.RemoteIpAddress ?? IPAddress.None;
        _webClients[id] = new DeviceInfo($"web:{id}", name, address, _port, false,
            DateTimeOffset.UtcNow, DeviceKind.WebBrowser);
        RaiseWebClientsChanged();
        return Results.Json(new { ok = true, name });
    }

    private IResult UploadStatus(string token)
    {
        if (!_offers.TryGetValue(token, out var accepted) || accepted.ExpiresAt < DateTimeOffset.UtcNow)
            return Results.Json(new { error = "Yêu cầu đã hết hạn." }, statusCode: 410);
        accepted.Touch();
        var offset = ResumeOffset(accepted.PartialPath, accepted.Request.Size);
        return Results.Json(new { offset, size = accepted.Request.Size });
    }

    private async Task<IResult> UploadAsync(string token, long? offset, HttpContext context)
    {
        if (!_offers.TryGetValue(token, out var accepted) || accepted.ExpiresAt < DateTimeOffset.UtcNow)
            return Results.Json(new { error = "Yêu cầu đã hết hạn." }, statusCode: 410);

        accepted.Touch();
        await accepted.Gate.WaitAsync(context.RequestAborted);
        try
        {
        var resumeOffset = ResumeOffset(accepted.PartialPath, accepted.Request.Size);
        if ((offset ?? 0) != resumeOffset)
            return Results.Json(new { error = "Offset đã thay đổi, hãy đồng bộ lại.", offset = resumeOffset }, statusCode: 409);
        if (context.Request.ContentLength is long length && length > accepted.Request.Size - resumeOffset)
            return Results.BadRequest(new { error = "Dung lượng phần upload không khớp.", offset = resumeOffset });

        Directory.CreateDirectory(accepted.Directory);
        var started = DateTimeOffset.UtcNow;
        var status = TransferStatus.Running;
        string? hashText = null;
        string? error = null;
        long received = resumeOffset;
        var watch = Stopwatch.StartNew();
        var writeHistory = false;
        try
        {
            if (!FileTransferService.HasEnoughDiskSpace(accepted.Directory,
                    accepted.Request.Size - resumeOffset, out var available))
            {
                status = TransferStatus.Failed;
                error = $"Không đủ dung lượng trống (còn {available / 1024d / 1024d / 1024d:0.0} GB).";
                writeHistory = true;
                _offers.TryRemove(token, out _);
                TryDelete(accepted.PartialPath);
                return Results.Json(new
                {
                    error
                }, statusCode: StatusCodes.Status507InsufficientStorage);
            }
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FileTransferService.ChunkSize];
            await using var output = new FileStream(accepted.PartialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                FileTransferService.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await HashPrefixAsync(output, hasher, buffer, resumeOffset, context.RequestAborted);
            output.Position = resumeOffset;
            while (received < accepted.Request.Size)
            {
                var wanted = (int)Math.Min(buffer.Length, accepted.Request.Size - received);
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, wanted), context.RequestAborted);
                if (read == 0) throw new EndOfStreamException("Upload kết thúc sớm.");
                await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                hasher.AppendData(buffer, 0, read);
                received += read;
                Report(accepted.Request, received, watch, TransferStatus.Running);
            }
            if (await context.Request.Body.ReadAsync(buffer.AsMemory(0, 1), context.RequestAborted) != 0)
                throw new InvalidDataException("Upload lớn hơn dung lượng đã khai báo.");
            await output.FlushAsync(context.RequestAborted);
            await output.DisposeAsync();
            File.Move(accepted.PartialPath, accepted.DestinationPath);
            hashText = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            status = TransferStatus.Completed;
            writeHistory = true;
            _offers.TryRemove(token, out _);
            if (accepted.Request.IsText)
            {
                var content = await File.ReadAllTextAsync(accepted.DestinationPath, Encoding.UTF8, context.RequestAborted);
                AddMessage(new TextMessage(accepted.Request.TransferId, content, accepted.Request.SenderName,
                    TransferDirection.Receive, DateTimeOffset.UtcNow, accepted.ClientId));
            }
            Report(accepted.Request, received, watch, status, hashText);
            return Results.Json(new { ok = true, sha256 = hashText });
        }
        catch (OperationCanceledException)
        {
            status = TransferStatus.Retrying;
            return Results.Json(new { error = "Mất kết nối; có thể tiếp tục upload." }, statusCode: 499);
        }
        catch (Exception ex)
        {
            status = ex is IOException ? TransferStatus.Retrying : TransferStatus.Failed;
            error = ex.Message;
            writeHistory = status == TransferStatus.Failed;
            if (writeHistory)
            {
                _offers.TryRemove(token, out _);
                TryDelete(accepted.PartialPath);
            }
            return Results.Json(new { error }, statusCode: 500);
        }
        finally
        {
            if (writeHistory)
                await _history.AddAsync(new TransferRecord(0, accepted.Request.FileName, accepted.Request.Size,
                    accepted.Request.SenderName, accepted.Request.SenderAddress.ToString(), TransferDirection.Receive,
                    status, hashText, started, DateTimeOffset.UtcNow, error));
        }
        }
        finally { accepted.Gate.Release(); }
    }

    private void Report(IncomingTransferRequest request, long received, Stopwatch watch, TransferStatus status, string? hash = null)
    {
        var speed = watch.Elapsed.TotalSeconds <= 0 ? 0 : received / watch.Elapsed.TotalSeconds;
        TimeSpan? eta = speed <= 0 ? null : TimeSpan.FromSeconds((request.Size - received) / speed);
        ProgressChanged?.Invoke(this, new TransferProgress(request.TransferId, request.FileName, request.Size,
            received, speed, eta, TransferDirection.Receive, status, request.SenderName, hash));
    }

    private void CleanupExpiredOffers()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var offer in _offers.Where(x => x.Value.ExpiresAt < now && x.Value.Gate.CurrentCount > 0))
            if (_offers.TryRemove(offer.Key, out var expired)) TryDelete(expired.PartialPath);
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                CleanupExpiredOffers();
                var cutoff = DateTimeOffset.UtcNow.AddSeconds(-16);
                var changed = false;
                foreach (var client in _webClients.Where(x => x.Value.LastSeen < cutoff))
                    changed |= _webClients.TryRemove(client.Key, out _);
                if (changed) RaiseWebClientsChanged();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RaiseWebClientsChanged() => WebClientsChanged?.Invoke(this,
        _webClients.Values.OrderBy(x => x.Name).ToArray());

    private void AddMessage(TextMessage message)
    {
        _messages.Enqueue(message);
        while (_messages.Count > 200) _messages.TryDequeue(out _);
        TextMessageReceived?.Invoke(this, message);
    }

    private static string? NormalizeWebClientId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.StartsWith("web:", StringComparison.Ordinal)) id = id[4..];
        var normalized = new string(id.Where(char.IsLetterOrDigit).Take(64).ToArray());
        return normalized.Length == 0 ? null : normalized;
    }

    private static IPAddress FindLanAddress()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(x => new { Adapter = x, Properties = x.GetIPProperties() })
            .OrderByDescending(x => x.Properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .ThenByDescending(x => x.Adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet);
        foreach (var item in interfaces)
        {
            var address = item.Properties.UnicastAddresses.Select(x => x.Address).FirstOrDefault(x =>
                x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x) && !x.ToString().StartsWith("169.254."));
            if (address is not null) return address;
        }
        return IPAddress.Loopback;
    }

    private static string UniquePath(string directory, string name)
    {
        var safeName = Path.GetFileName(name);
        var path = Path.Combine(directory, safeName);
        if (!File.Exists(path) && !File.Exists(path + ".partial")) return path;
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(path) && !File.Exists(path + ".partial")) return path;
        }
    }

    private static long ResumeOffset(string partialPath, long totalSize)
    {
        if (!File.Exists(partialPath)) return 0;
        var length = new FileInfo(partialPath).Length;
        if (length < 0 || length > totalSize)
        {
            TryDelete(partialPath);
            return 0;
        }
        if (length < totalSize && length % FileTransferService.ChunkSize != 0)
        {
            length -= length % FileTransferService.ChunkSize;
            using var partial = new FileStream(partialPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            partial.SetLength(length);
        }
        return length;
    }

    private static async Task HashPrefixAsync(FileStream file, IncrementalHash hasher, byte[] buffer, long length,
        CancellationToken cancellationToken)
    {
        file.Position = 0;
        long hashed = 0;
        while (hashed < length)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, length - hashed)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("File tạm resume không đầy đủ.");
            hasher.AppendData(buffer, 0, read);
            hashed += read;
        }
    }

    private static void CleanupPartials(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.partial", SearchOption.TopDirectoryOnly))
                TryDelete(path);
        }
        catch { }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_cleanupLoop is not null) try { await _cleanupLoop; } catch { }
        if (_app is not null) await _app.DisposeAsync();
        foreach (var offer in _offers.Values)
        {
            TryDelete(offer.PartialPath);
            offer.Gate.Dispose();
        }
        _stop.Dispose();
    }

    private sealed record OfferDto(string Name, long Size, string? DeviceName, string? ClientId = null, bool IsText = false);
    private sealed record RegistrationDto(string Id, string? Name);
    private sealed class AcceptedOffer(IncomingTransferRequest request, string directory, string destinationPath,
        string partialPath, DateTimeOffset expiresAt, string? clientId)
    {
        public IncomingTransferRequest Request { get; } = request;
        public string Directory { get; } = directory;
        public string DestinationPath { get; } = destinationPath;
        public string PartialPath { get; } = partialPath;
        public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
        public string? ClientId { get; } = clientId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public void Touch() => ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
    }

    private const string MobilePage = """
<!doctype html><html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>LanFileDrop</title><style>
:root{color-scheme:light dark;--bg:#f3f5fa;--card:#fff;--text:#172033;--muted:#6b7280;--line:#dce2ec;--accent:#6966f2}@media(prefers-color-scheme:dark){:root{--bg:#111318;--card:#1b1e25;--text:#f4f6fb;--muted:#9ba5b8;--line:#343a49}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px system-ui,-apple-system,sans-serif}.wrap{max-width:680px;margin:auto;padding:24px 16px}.head{display:flex;align-items:center;gap:12px;margin-bottom:24px}.logo{display:grid;place-items:center;width:46px;height:46px;border-radius:14px;background:linear-gradient(135deg,var(--accent),#8b7cf6);color:#fff;font-size:24px;font-weight:800;box-shadow:0 8px 20px #6966f235}.head h1{font-size:24px;margin:0}.head p{margin:2px 0;color:var(--muted)}.online{margin-left:auto;color:#0e9f75;background:#10b98118;padding:7px 10px;border-radius:999px;font-size:11px;font-weight:700}.card{background:var(--card);border:1px solid var(--line);border-radius:18px;padding:20px;margin-bottom:16px;box-shadow:0 7px 24px #0f172a0a}.card h2{font-size:17px;margin:0 0 12px}.identity{display:flex;align-items:center;gap:12px;margin-bottom:16px}.identity label{color:var(--muted);white-space:nowrap}.identity input,.text-message{display:block;width:100%;border:1px solid var(--line);border-radius:10px;padding:11px 12px;background:var(--bg);color:var(--text);font:inherit}.identity input:focus,.text-message:focus{outline:2px solid #6966f235;border-color:var(--accent)}.text-message{min-height:120px;resize:vertical}.text-actions{display:flex;justify-content:flex-end;align-items:center;gap:12px;margin-top:12px}.text-actions span{color:var(--muted);font-size:12px}.primary{border:0;border-radius:10px;padding:11px 18px;background:var(--accent);color:#fff;font:inherit;font-weight:700}.primary:disabled{opacity:.45}.messages{display:flex;flex-direction:column;gap:10px;max-height:320px;overflow:auto}.message{max-width:88%;padding:11px 13px;border-radius:13px;background:var(--bg);align-self:flex-start}.message.sent{align-self:flex-end;background:#6966f220}.message b{display:block;font-size:12px;color:var(--accent);margin-bottom:4px}.message p{white-space:pre-wrap;overflow-wrap:anywhere;margin:0;line-height:1.45}.message-foot{display:flex;align-items:center;justify-content:space-between;gap:14px;margin-top:7px}.message time{color:var(--muted);font-size:10px}.copy{border:0;background:transparent;color:var(--accent);font:inherit;font-size:11px;font-weight:700;padding:4px 0}.empty{color:var(--muted)}.drop{display:block;text-align:center;border:2px dashed var(--line);border-radius:14px;padding:30px 14px;cursor:pointer;transition:.2s}.drop.drag{border-color:var(--accent);background:#6966f214}.drop b{display:block;font-size:17px;margin:6px}.drop span{color:var(--muted)}input[type=file]{display:none}.status{margin-top:14px;color:var(--muted);min-height:22px;overflow-wrap:anywhere}.bar{height:9px;background:var(--line);border-radius:10px;overflow:hidden;margin-top:10px}.bar i{display:block;height:100%;width:0;background:linear-gradient(90deg,var(--accent),#9b7df8);transition:width .15s}.files{list-style:none;padding:0;margin:0}.files li{display:flex;align-items:center;gap:10px;padding:12px 0;border-bottom:1px solid var(--line)}.files li:last-child{border:0}.file{flex:1;min-width:0}.file b{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.file small{color:var(--muted)}a{color:var(--accent);text-decoration:none;font-weight:650;white-space:nowrap}.hint{text-align:center;color:var(--muted);font-size:12px;margin-top:22px}@media(max-width:480px){.wrap{padding:16px 12px}.card{padding:16px;border-radius:16px}.head{margin:2px 4px 20px}.head h1{font-size:21px}.head p{font-size:12px}.online{font-size:0;padding:6px}.online:first-letter{font-size:12px}.identity{align-items:flex-start;flex-direction:column;gap:7px}.identity input,.text-message{font-size:16px}.drop{padding:25px 10px}.message{max-width:94%}.text-actions{justify-content:space-between}.primary{min-height:44px}}
</style></head><body><main class="wrap"><div class="head"><div class="logo">↗</div><div><h1>LanFileDrop</h1><p>Truyền file trực tiếp trong mạng LAN</p></div><div class="online">● Đã kết nối</div></div>
<section class="card"><div class="identity"><label for="deviceName">Tên thiết bị</label><input id="deviceName" maxlength="40" autocomplete="off"></div><label class="drop" id="drop"><div style="font-size:30px">📎</div><b>Chọn hoặc thả file vào đây</b><span>Windows sẽ hỏi xác nhận trước khi nhận</span><input id="pick" type="file" multiple></label><div class="status" id="status"></div><div class="bar"><i id="progress"></i></div></section>
<section class="card"><h2>Gửi văn bản</h2><textarea class="text-message" id="textMessage" maxlength="250000" placeholder="Nhập hoặc dán nội dung cần gửi…"></textarea><div class="text-actions"><span id="textCount">0 ký tự</span><button class="primary" id="sendText" type="button" disabled>Gửi văn bản</button></div></section>
<section class="card"><h2>Văn bản gần đây</h2><div class="messages" id="messages"><div class="empty">Chưa có văn bản</div></div></section>
<section class="card"><h2 style="font-size:17px;margin:0 0 8px">File có thể tải xuống</h2><ul class="files" id="files"><li>Đang tải…</li></ul></section><p class="hint">Không cần Internet • Không upload lên cloud</p></main>
<script>
const d=document.querySelector('#drop'),p=document.querySelector('#pick'),s=document.querySelector('#status'),bar=document.querySelector('#progress'),n=document.querySelector('#deviceName'),text=document.querySelector('#textMessage'),sendText=document.querySelector('#sendText'),textCount=document.querySelector('#textCount');
const fallback=/iPhone/i.test(navigator.userAgent)?'iPhone (Safari)':/iPad/i.test(navigator.userAgent)?'iPad (Safari)':'Thiết bị web';let clientId=localStorage.getItem('lanFileDropId');if(!clientId){clientId=Date.now().toString(36)+Math.random().toString(36).slice(2);localStorage.setItem('lanFileDropId',clientId)}n.value=localStorage.getItem('lanFileDropName')||fallback;async function register(){localStorage.setItem('lanFileDropName',n.value||fallback);try{await fetch('/api/heartbeat',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({id:clientId,name:n.value||fallback})})}catch{}}n.onchange=register;register();setInterval(register,5000);
p.onchange=()=>{send([...p.files]);p.value=''};for(const e of ['dragenter','dragover'])d.addEventListener(e,x=>{x.preventDefault();d.classList.add('drag')});for(const e of ['dragleave','drop'])d.addEventListener(e,x=>{x.preventDefault();d.classList.remove('drag')});d.addEventListener('drop',e=>send([...e.dataTransfer.files]));
text.oninput=()=>{textCount.textContent=text.value.length.toLocaleString('vi-VN')+' ký tự';sendText.disabled=!text.value.trim()};sendText.onclick=async()=>{const value=text.value;if(!value.trim())return;sendText.disabled=true;const now=new Date(),pad=x=>String(x).padStart(2,'0'),name=`Văn bản-${now.getFullYear()}${pad(now.getMonth()+1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}.txt`;const blob=new Blob([value],{type:'text/plain;charset=utf-8'});if(await sendOne(name,blob,true)){text.value='';text.oninput();loadMessages()}else sendText.disabled=false};
async function send(files){await register();for(const f of files)await sendOne(f.name,f,false)}async function sendOne(name,data,isText){try{await register();s.textContent=`Đang chờ Windows chấp nhận ${name}…`;bar.style.width='0';let r=await fetch('/api/offer',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({name,size:data.size,deviceName:n.value||fallback,clientId,isText})});let j=await r.json();if(!r.ok)throw Error(j.error||'Bị từ chối');await upload(data,j.token,name);s.textContent=`✓ Đã gửi ${name}`;load();return true}catch(e){s.textContent=`Không gửi được: ${e.message}`;return false}}
async function upload(data,token,name){let lastError;for(let attempt=1;attempt<=5;attempt++){try{const state=await fetch('/api/upload/'+token+'/status').then(async r=>{const j=await r.json();if(!r.ok)throw Error(j.error||'Phiên upload đã hết hạn');return j});await uploadPart(data.slice(state.offset),token,name,state.offset,data.size);return}catch(e){lastError=e;if(attempt===5)break;s.textContent=`Mất kết nối • thử lại ${attempt}/4…`;await new Promise(r=>setTimeout(r,Math.min(8000,1000*2**(attempt-1))))}}throw lastError}function uploadPart(part,token,name,offset,total){return new Promise((ok,no)=>{const x=new XMLHttpRequest();x.open('PUT','/api/upload/'+token+'?offset='+offset);x.upload.onprogress=e=>{const loaded=offset+e.loaded,percent=total?Math.round(loaded/total*100):100;bar.style.width=percent+'%';s.textContent=`Đang gửi ${name} • ${percent}%`};x.onload=()=>x.status<300?ok():no(Error(JSON.parse(x.responseText||'{}').error||'Upload lỗi'));x.onerror=()=>no(Error('Mất kết nối'));x.send(part)})}
const size=n=>n>1073741824?(n/1073741824).toFixed(1)+' GB':n>1048576?(n/1048576).toFixed(1)+' MB':n>1024?(n/1024).toFixed(1)+' KB':n+' B';let messageCache=[];async function load(){const a=await(await fetch('/api/files')).json();document.querySelector('#files').innerHTML=a.length?a.map(f=>`<li><div class="file"><b>${esc(f.name)}</b><small>${size(f.length)}</small></div><a href="/download/${encodeURIComponent(f.name)}">Tải xuống</a></li>`).join(''):'<li>Chưa có file</li>'}async function loadMessages(){messageCache=await(await fetch('/api/messages?clientId='+encodeURIComponent(clientId))).json();document.querySelector('#messages').innerHTML=messageCache.length?messageCache.map((m,i)=>`<div class="message ${m.direction==='Send'?'sent':''}"><b>${esc(m.senderName)}</b><p>${esc(m.content)}</p><div class="message-foot"><button class="copy" data-copy="${i}">Sao chép</button><time>${new Date(m.timestamp).toLocaleTimeString('vi-VN',{hour:'2-digit',minute:'2-digit'})}</time></div></div>`).join(''):'<div class="empty">Chưa có văn bản</div>'}document.querySelector('#messages').onclick=e=>{const button=e.target.closest('[data-copy]');if(button)copyMessage(Number(button.dataset.copy),button)};async function copyMessage(index,button){const value=messageCache[index]?.content;if(!value)return;let copied=false;try{if(navigator.clipboard&&window.isSecureContext){await navigator.clipboard.writeText(value);copied=true}}catch{}if(!copied){const area=document.createElement('textarea');area.value=value;area.style.cssText='position:fixed;opacity:0;left:-9999px';document.body.appendChild(area);area.focus();area.select();area.setSelectionRange(0,area.value.length);copied=document.execCommand('copy');area.remove()}button.textContent=copied?'✓ Đã sao chép':'Hãy nhấn giữ để sao chép';setTimeout(()=>button.textContent='Sao chép',1600)}function esc(x){const e=document.createElement('div');e.textContent=x;return e.innerHTML}load();loadMessages();setInterval(()=>{load();loadMessages()},5000);
</script></body></html>
""";
}
