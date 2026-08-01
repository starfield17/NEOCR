using OcrWorkbench.Contracts;
using OcrWorkbench.Core;

namespace OcrWorkbench.PluginHost;

public sealed class WorkerRecognizerSession : IRecognizer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PluginPackage _package;
    private readonly Action<string>? _log;
    private WorkerProcessRecognizer? _worker;
    private int _disposed;

    internal WorkerRecognizerSession(PluginPackage package, Action<string>? log)
    {
        _package = package;
        _log = log;
    }

    public async ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
        PageArtifact page,
        RecognitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var worker = await GetOrStartAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await worker.RecognizeAsync(page, options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (!worker.IsHealthy)
                {
                    await EvictAsync(worker).ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_worker is not null)
            {
                await _worker.DisposeAsync().ConfigureAwait(false);
                _worker = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<WorkerProcessRecognizer> GetOrStartAsync(CancellationToken cancellationToken)
    {
        if (_worker is { IsHealthy: true })
        {
            return _worker;
        }

        if (_worker is not null)
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
        }

        _worker = await _package.StartRecognizerAsync(_log, cancellationToken).ConfigureAwait(false);
        return _worker;
    }

    private async Task EvictAsync(WorkerProcessRecognizer worker)
    {
        if (!ReferenceEquals(_worker, worker))
        {
            return;
        }

        _worker = null;
        await worker.DisposeAsync().ConfigureAwait(false);
    }
}
