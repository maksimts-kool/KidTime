namespace KidTime.ControlService.Server;

public sealed class SyncTrigger
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await _signal.WaitAsync(timeout, cancellationToken);
    }
}
