namespace LanFileDrop.Network;

public sealed class TransferControl : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(true);
    private readonly CancellationTokenSource _cancel = new();
    public bool IsPaused { get; private set; }
    public CancellationToken Token => _cancel.Token;

    public void Pause() { IsPaused = true; _gate.Reset(); }
    public void Resume() { IsPaused = false; _gate.Set(); }
    public void Cancel() { _cancel.Cancel(); _gate.Set(); }
    internal void Wait(CancellationToken cancellationToken) => _gate.Wait(cancellationToken);
    public void Dispose() { _cancel.Dispose(); _gate.Dispose(); }
}
