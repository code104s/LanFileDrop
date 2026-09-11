using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanFileDrop.Core;

namespace LanFileDrop.Network;

public sealed class FileTransferService : IAsyncDisposable
{
    public const int DefaultPort = 49495;
    public const int ChunkSize = 1024 * 1024;
    private const int ProtocolVersion = 2;
    private const int MaxAttempts = 5;
    private const byte Declined = 0;
    private const byte Accepted = 1;
    private const byte AlreadyCompleted = 2;
    private const byte InsufficientSpace = 3;
    private static readonly TimeSpan PartialLifetime = TimeSpan.FromHours(24);

    private readonly HistoryRepository _history;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, IncomingState> _incoming = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _incomingLocks = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _completed = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _activeTransfers;

    public event EventHandler<TransferProgress>? ProgressChanged;
    public event Func<IncomingTransferRequest, Task<TransferDecision>>? IncomingTransfer;
    public event EventHandler<TextMessage>? TextReceived;
    public int ActiveTransfers => Volatile.Read(ref _activeTransfers);

    public FileTransferService(HistoryRepository history) => _history = history;

    public void Start(int port = DefaultPort)
    {
        if (_acceptLoop is not null) return;
        CleanupStalePartials(DefaultDownloadDirectory());
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    public async Task SendPathAsync(DeviceInfo device, string path, TransferControl control, Guid? transferId = null)
    {
        string? temporary = null;
        var displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        try
        {
            if (Directory.Exists(path))
            {
                temporary = Path.Combine(Path.GetTempPath(), $"LanFileDrop-{Guid.NewGuid():N}.zip");
                ZipFile.CreateFromDirectory(path, temporary, CompressionLevel.Fastest, false);
                path = temporary;
                displayName += ".zip";
            }
            await SendFileCoreAsync(device, path, displayName, false, control, transferId);
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
        }
    }

    public async Task SendTextAsync(DeviceInfo device, string text, TransferControl control)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"Text-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(temporary, text, Encoding.UTF8, control.Token);
        try { await SendFileCoreAsync(device, temporary, Path.GetFileName(temporary), true, control, null); }
        finally { TryDelete(temporary); }
    }

    private async Task SendFileCoreAsync(DeviceInfo device, string path, string displayName, bool isText,
        TransferControl control, Guid? transferId)
    {
        var info = new FileInfo(path);
        var id = transferId ?? Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var status = TransferStatus.Running;
        string? hashText = null;
        string? error = null;
        Interlocked.Increment(ref _activeTransfers);
        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    var result = await SendAttemptAsync(device, path, displayName, isText, id, info.Length, control);
                    status = result.Status;
                    hashText = result.Hash;
                    error = null;
                    break;
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts && !control.Token.IsCancellationRequested)
                {
                    error = ex.Message;
                    ProgressChanged?.Invoke(this, new TransferProgress(id, displayName, info.Length, 0, 0, null,
                        TransferDirection.Send, TransferStatus.Retrying, device.Name, null,
                        $"Mất kết nối. Đang thử lại {attempt}/{MaxAttempts - 1}…"));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 << (attempt - 1))), control.Token);
                }
            }
        }
        catch (OperationCanceledException) { status = TransferStatus.Cancelled; error = "Đã hủy truyền file."; }
        catch (Exception ex) { status = TransferStatus.Failed; error = ex.Message; }
        finally
        {
            Interlocked.Decrement(ref _activeTransfers);
            await _history.AddAsync(new TransferRecord(0, displayName, info.Length, device.Name,
                device.Address.ToString(), TransferDirection.Send, status, hashText, started, DateTimeOffset.UtcNow, error));
            if (status is TransferStatus.Failed or TransferStatus.Cancelled or TransferStatus.Declined)
                ProgressChanged?.Invoke(this, new TransferProgress(id, displayName, info.Length, 0, 0, null,
                    TransferDirection.Send, status, device.Name, null, error));
        }
    }

    private async Task<SendResult> SendAttemptAsync(DeviceInfo device, string path, string displayName, bool isText,
        Guid id, long length, TransferControl control)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(device.Address, device.Port, control.Token);
        await using var network = client.GetStream();
        await WriteHeaderAsync(network,
            new TransferHeader(id, displayName, length, Environment.MachineName, isText, ProtocolVersion), control.Token);

        var response = new byte[9];
        await ReadExactlyAsync(network, response, control.Token);
        var offset = BitConverter.ToInt64(response, 1);
        if (response[0] == Declined) return new SendResult(TransferStatus.Declined, null);
        if (response[0] == InsufficientSpace)
            throw new RemoteTransferException("Thiết bị nhận không đủ dung lượng trống.");
        if (response[0] == AlreadyCompleted)
        {
            var completedHash = await ComputeHashAsync(path, control.Token);
            Report(id, displayName, length, length, 0, Stopwatch.StartNew(), TransferDirection.Send,
                TransferStatus.Completed, device.Name, completedHash);
            return new SendResult(TransferStatus.Completed, completedHash);
        }
        if (response[0] != Accepted || offset < 0 || offset > length)
            throw new InvalidDataException("Phản hồi resume của thiết bị nhận không hợp lệ.");

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        await HashPrefixAsync(file, hasher, buffer, offset, control.Token);
        var sent = offset;
        var watch = Stopwatch.StartNew();
        while (sent < length)
        {
            control.Wait(control.Token);
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), control.Token);
            if (read == 0) throw new EndOfStreamException("File nguồn đã thay đổi trong khi truyền.");
            await network.WriteAsync(buffer.AsMemory(0, read), control.Token);
            hasher.AppendData(buffer, 0, read);
            sent += read;
            Report(id, displayName, length, sent, Math.Max(0, sent - offset), watch,
                TransferDirection.Send, TransferStatus.Running, device.Name);
        }
        var hash = hasher.GetHashAndReset();
        await network.WriteAsync(hash, control.Token);
        var verification = new byte[1];
        await ReadExactlyAsync(network, verification, control.Token);
        if (verification[0] != 1) throw new InvalidDataException("Thiết bị nhận báo checksum SHA-256 không khớp.");
        var hashText = Convert.ToHexString(hash).ToLowerInvariant();
        Report(id, displayName, length, length, Math.Max(0, length - offset), watch,
            TransferDirection.Send, TransferStatus.Completed, device.Name, hashText);
        return new SendResult(TransferStatus.Completed, hashText);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        TransferHeader? header = null;
        IncomingState? state = null;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        Interlocked.Increment(ref _activeTransfers);
        using (client)
        try
        {
            await using var network = client.GetStream();
            header = await ReadHeaderAsync(network, cancellationToken);
            if (header.Version != ProtocolVersion)
            {
                await WriteResponseAsync(network, Declined, 0, cancellationToken);
                return;
            }

            CleanupCompletedTransfers();
            if (_completed.ContainsKey(header.Id))
            {
                await WriteResponseAsync(network, AlreadyCompleted, header.Length, cancellationToken);
                return;
            }

            var gate = _incomingLocks.GetOrAdd(header.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (_completed.ContainsKey(header.Id))
                {
                    await WriteResponseAsync(network, AlreadyCompleted, header.Length, cancellationToken);
                    return;
                }

                if (_incoming.TryGetValue(header.Id, out state))
                {
                    if (state.Header.Length != header.Length || state.Header.Name != header.Name)
                        throw new InvalidDataException("Thông tin file resume không khớp.");
                }
                else
                {
                    var request = new IncomingTransferRequest(header.Id, header.Name, header.Length, header.Sender,
                        remote.Address, header.IsText);
                    var handler = IncomingTransfer;
                    var decision = handler is null ? new TransferDecision(false) : await handler(request);
                    if (!decision.Accepted)
                    {
                        await WriteResponseAsync(network, Declined, 0, cancellationToken);
                        await AddReceiveHistoryAsync(header, remote.Address, TransferStatus.Declined, null,
                            DateTimeOffset.UtcNow, null);
                        return;
                    }

                    var directory = decision.DestinationDirectory ?? DefaultDownloadDirectory();
                    Directory.CreateDirectory(directory);
                    CleanupStalePartials(directory);
                    var destination = UniquePath(directory, Path.GetFileName(header.Name));
                    state = new IncomingState(header, destination, destination + ".partial", DateTimeOffset.UtcNow,
                        remote.Address);
                    _incoming[header.Id] = state;
                }

                var offset = File.Exists(state.PartialPath) ? new FileInfo(state.PartialPath).Length : 0;
                if (offset < 0 || offset > header.Length)
                {
                    TryDelete(state.PartialPath);
                    offset = 0;
                }
                else if (offset < header.Length && offset % ChunkSize != 0)
                {
                    offset -= offset % ChunkSize;
                    using var partial = new FileStream(state.PartialPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                    partial.SetLength(offset);
                }
                if (!HasEnoughDiskSpace(Path.GetDirectoryName(state.PartialPath)!, header.Length - offset, out _))
                {
                    await WriteResponseAsync(network, InsufficientSpace, offset, cancellationToken);
                    RemoveIncoming(state, deletePartial: true);
                    await AddReceiveHistoryAsync(header, remote.Address, TransferStatus.Failed, null, state.StartedAt,
                        "Không đủ dung lượng trống để nhận file.");
                    return;
                }

                await WriteResponseAsync(network, Accepted, offset, cancellationToken);
                await ReceiveContentAsync(network, state, offset, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (state is not null) RemoveIncoming(state, deletePartial: true);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            if (header is not null)
            {
                var received = state is not null && File.Exists(state.PartialPath) ? new FileInfo(state.PartialPath).Length : 0;
                ProgressChanged?.Invoke(this, new TransferProgress(header.Id, header.Name, header.Length, received, 0,
                    null, TransferDirection.Receive, TransferStatus.Retrying, header.Sender, null,
                    "Kết nối gián đoạn, đang chờ thiết bị gửi nối lại…"));
            }
        }
        catch (Exception ex)
        {
            if (state is not null) RemoveIncoming(state, deletePartial: true);
            if (header is not null)
            {
                await AddReceiveHistoryAsync(header, remote.Address, TransferStatus.Failed, null,
                    state?.StartedAt ?? DateTimeOffset.UtcNow, ex.Message);
                ProgressChanged?.Invoke(this, new TransferProgress(header.Id, header.Name, header.Length, 0, 0, null,
                    TransferDirection.Receive, TransferStatus.Failed, header.Sender, null, ex.Message));
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeTransfers);
        }
    }

    private async Task ReceiveContentAsync(Stream network, IncomingState state, long offset,
        CancellationToken cancellationToken)
    {
        var header = state.Header;
        var buffer = new byte[ChunkSize];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(state.PartialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.Read, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await HashPrefixAsync(file, hasher, buffer, offset, cancellationToken);
        file.Position = offset;
        var received = offset;
        var watch = Stopwatch.StartNew();
        while (received < header.Length)
        {
            var wanted = (int)Math.Min(buffer.Length, header.Length - received);
            var read = await network.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Kết nối bị đóng trước khi nhận đủ dữ liệu.");
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hasher.AppendData(buffer, 0, read);
            received += read;
            Report(header.Id, header.Name, header.Length, received, Math.Max(0, received - offset), watch,
                TransferDirection.Receive, TransferStatus.Running, header.Sender);
        }
        await file.FlushAsync(cancellationToken);
        var expected = new byte[32];
        await ReadExactlyAsync(network, expected, cancellationToken);
        var actual = hasher.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            await network.WriteAsync(new byte[] { 0 }, cancellationToken);
            RemoveIncoming(state, deletePartial: true);
            throw new TerminalTransferException("SHA-256 không khớp; file tạm đã được xóa.");
        }

        await file.DisposeAsync();
        File.Move(state.PartialPath, state.DestinationPath);
        var hashText = Convert.ToHexString(actual).ToLowerInvariant();
        _incoming.TryRemove(header.Id, out _);
        _completed[header.Id] = DateTimeOffset.UtcNow;
        if (header.IsText && header.Length <= 1024 * 1024)
        {
            var content = await File.ReadAllTextAsync(state.DestinationPath, Encoding.UTF8, cancellationToken);
            TextReceived?.Invoke(this, new TextMessage(header.Id, content, header.Sender,
                TransferDirection.Receive, DateTimeOffset.UtcNow));
        }
        Report(header.Id, header.Name, header.Length, header.Length, Math.Max(0, header.Length - offset), watch,
            TransferDirection.Receive, TransferStatus.Completed, header.Sender, hashText);
        await AddReceiveHistoryAsync(header, state.RemoteAddress, TransferStatus.Completed, hashText, state.StartedAt, null);
        await network.WriteAsync(new byte[] { 1 }, cancellationToken);
    }

    private async Task AddReceiveHistoryAsync(TransferHeader header, IPAddress address, TransferStatus status,
        string? hash, DateTimeOffset started, string? error) =>
        await _history.AddAsync(new TransferRecord(0, header.Name, header.Length, header.Sender, address.ToString(),
            TransferDirection.Receive, status, hash, started, DateTimeOffset.UtcNow, error));

    private void RemoveIncoming(IncomingState state, bool deletePartial)
    {
        _incoming.TryRemove(state.Header.Id, out _);
        if (deletePartial) TryDelete(state.PartialPath);
    }

    private void Report(Guid id, string name, long total, long done, long bytesThisAttempt, Stopwatch watch,
        TransferDirection direction, TransferStatus status, string peer, string? hash = null)
    {
        var speed = watch.Elapsed.TotalSeconds <= 0 ? 0 : bytesThisAttempt / watch.Elapsed.TotalSeconds;
        TimeSpan? eta = speed <= 0 ? null : TimeSpan.FromSeconds((total - done) / speed);
        ProgressChanged?.Invoke(this,
            new TransferProgress(id, name, total, done, speed, eta, direction, status, peer, hash));
    }

    private static async Task HashPrefixAsync(FileStream file, IncrementalHash hasher, byte[] buffer, long length,
        CancellationToken token)
    {
        file.Position = 0;
        long hashed = 0;
        while (hashed < length)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - hashed)), token);
            if (read == 0) throw new EndOfStreamException("File tạm resume không đầy đủ.");
            hasher.AppendData(buffer, 0, read);
            hashed += read;
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(file, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteHeaderAsync(Stream stream, TransferHeader header, CancellationToken token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header);
        await stream.WriteAsync(BitConverter.GetBytes(json.Length), token);
        await stream.WriteAsync(json, token);
    }

    private static async Task<TransferHeader> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var size = new byte[4];
        await ReadExactlyAsync(stream, size, token);
        var length = BitConverter.ToInt32(size);
        if (length is <= 0 or > 64 * 1024) throw new InvalidDataException("Header không hợp lệ.");
        var json = new byte[length];
        await ReadExactlyAsync(stream, json, token);
        return JsonSerializer.Deserialize<TransferHeader>(json) ?? throw new InvalidDataException("Header không hợp lệ.");
    }

    private static async Task WriteResponseAsync(Stream stream, byte status, long offset, CancellationToken token)
    {
        var response = new byte[9];
        response[0] = status;
        BitConverter.GetBytes(offset).CopyTo(response, 1);
        await stream.WriteAsync(response, token);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    public static bool HasEnoughDiskSpace(string directory, long requiredBytes, out long availableBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrWhiteSpace(root)) { availableBytes = 0; return false; }
            availableBytes = new DriveInfo(root).AvailableFreeSpace;
            var reserve = Math.Max(64L * 1024 * 1024, Math.Max(0, requiredBytes) / 50);
            return availableBytes >= reserve && availableBytes - reserve >= Math.Max(0, requiredBytes);
        }
        catch
        {
            availableBytes = 0;
            return false;
        }
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

    private static string DefaultDownloadDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanFileDrop");

    private static void CleanupStalePartials(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var cutoff = DateTime.UtcNow - PartialLifetime;
            foreach (var path in Directory.EnumerateFiles(directory, "*.partial", SearchOption.TopDirectoryOnly))
                if (File.GetLastWriteTimeUtc(path) < cutoff) TryDelete(path);
        }
        catch { }
    }

    private void CleanupCompletedTransfers()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        foreach (var item in _completed.Where(x => x.Value < cutoff)) _completed.TryRemove(item.Key, out _);
    }

    private static bool IsTransient(Exception ex) => ex is SocketException or IOException;

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null) try { await _acceptLoop; } catch { }
        foreach (var state in _incoming.Values) TryDelete(state.PartialPath);
        foreach (var gate in _incomingLocks.Values) gate.Dispose();
        _stop.Dispose();
    }

    private sealed record TransferHeader(Guid Id, string Name, long Length, string Sender, bool IsText, int Version);
    private sealed record IncomingState(TransferHeader Header, string DestinationPath, string PartialPath,
        DateTimeOffset StartedAt, IPAddress RemoteAddress);
    private sealed record SendResult(TransferStatus Status, string? Hash);
    private sealed class RemoteTransferException(string message) : Exception(message);
    private sealed class TerminalTransferException(string message) : Exception(message);
}
