namespace VideoEdit.Services;

public sealed class MediaGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string? _operation;
    private bool _exporting;

    public bool IsBusy => _operation is not null;

    public bool IsExporting => _exporting;

    public object Status() => new
    {
        busy = IsBusy,
        operation = _operation,
        exporting = _exporting,
    };

    public void Begin(string operation, bool export)
    {
        if (!_semaphore.Wait(0))
        {
            throw VideoEditException.Conflict($"A media operation is already running ({_operation}).");
        }

        _operation = operation;
        _exporting = export;
    }

    public void End()
    {
        _exporting = false;
        _operation = null;
        _semaphore.Release();
    }

    public async Task<T> RunAsync<T>(string operation, bool export, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        Begin(operation, export);
        try
        {
            return await work(cancellationToken);
        }
        finally
        {
            End();
        }
    }
}
