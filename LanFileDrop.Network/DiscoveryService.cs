using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanFileDrop.Core;

namespace LanFileDrop.Network;

public sealed class DiscoveryService : IAsyncDisposable
{
    public const int DiscoveryPort = 49494;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _udp;
    private Task? _loop;
    private bool _busy;

    public event EventHandler<IReadOnlyList<DeviceInfo>>? DevicesChanged;
    public IReadOnlyList<DeviceInfo> Devices => _devices.Values.OrderBy(x => x.Name).ToArray();

    public void Start(int transferPort)
    {
        if (_loop is not null) return;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.EnableBroadcast = true;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _loop = RunAsync(transferPort, _stop.Token);
    }

    public void SetBusy(bool busy) => _busy = busy;

    private async Task RunAsync(int transferPort, CancellationToken cancellationToken)
    {
        var receive = ReceiveAsync(transferPort, cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await BroadcastAsync(transferPort, cancellationToken);
                RemoveExpired();
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        await receive;
    }

    private async Task ReceiveAsync(int transferPort, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(cancellationToken);
                var message = JsonSerializer.Deserialize<DiscoveryMessage>(result.Buffer);
                if (message is null || message.Id == _instanceId || message.Magic != "LANFILEDROP/1") continue;
                _devices[message.Id] = new DeviceInfo(message.Id, message.Name, result.RemoteEndPoint.Address,
                    message.Port, message.Busy, DateTimeOffset.UtcNow);
                RaiseChanged();
                if (message.Kind == "query") await SendAsync("announce", transferPort, result.RemoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { /* malformed datagrams are ignored */ }
        }
    }

    private Task BroadcastAsync(int transferPort, CancellationToken cancellationToken) =>
        SendAsync("query", transferPort, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), cancellationToken);

    private async Task SendAsync(string kind, int transferPort, IPEndPoint target, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage(
            "LANFILEDROP/1", kind, _instanceId, Environment.MachineName, transferPort, _busy));
        await _udp!.SendAsync(data, target, cancellationToken);
    }

    private void RemoveExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-16);
        var changed = false;
        foreach (var item in _devices.Where(x => x.Value.LastSeen < cutoff))
            changed |= _devices.TryRemove(item.Key, out _);
        if (changed) RaiseChanged();
    }

    private void RaiseChanged() => DevicesChanged?.Invoke(this, Devices);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp?.Dispose();
        if (_loop is not null) try { await _loop; } catch { }
        _stop.Dispose();
    }

    private sealed record DiscoveryMessage(string Magic, string Kind, string Id, string Name, int Port, bool Busy);
}
