using System.Net;

namespace LanFileDrop.Core;

public enum TransferDirection { Send, Receive }
public enum TransferStatus { Pending, Running, Paused, Completed, Declined, Cancelled, Failed, Retrying }
public enum DeviceKind { NativeApp, WebBrowser, ManualIp }

public sealed record DeviceInfo(
    string Id,
    string Name,
    IPAddress Address,
    int Port,
    bool IsBusy,
    DateTimeOffset LastSeen,
    DeviceKind Kind = DeviceKind.NativeApp);

public sealed record TransferRecord(
    long Id,
    string FileName,
    long Size,
    string PeerName,
    string PeerAddress,
    TransferDirection Direction,
    TransferStatus Status,
    string? Sha256,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public sealed record TransferProgress(
    Guid TransferId,
    string FileName,
    long TotalBytes,
    long TransferredBytes,
    double BytesPerSecond,
    TimeSpan? Eta,
    TransferDirection Direction,
    TransferStatus Status,
    string PeerName,
    string? Sha256 = null,
    string? Error = null)
{
    public double Percentage => TotalBytes == 0 ? 100 : Math.Clamp(TransferredBytes * 100d / TotalBytes, 0, 100);
}

public sealed record IncomingTransferRequest(
    Guid TransferId,
    string FileName,
    long Size,
    string SenderName,
    IPAddress SenderAddress,
    bool IsText);

public sealed record TransferDecision(bool Accepted, string? DestinationDirectory = null);

public sealed record TextMessage(
    Guid Id,
    string Content,
    string SenderName,
    TransferDirection Direction,
    DateTimeOffset Timestamp,
    string? SourceDeviceId = null,
    string? TargetDeviceId = null);
