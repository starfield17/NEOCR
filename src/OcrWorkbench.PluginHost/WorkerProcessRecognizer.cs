using System.Collections.Concurrent;
using System.Diagnostics;
using OcrWorkbench.Contracts;
using OcrWorkbench.Contracts.Plugin.V1;
using OcrWorkbench.Core;
using DomainBlock = OcrWorkbench.Contracts.SpatialTextBlock;
using DomainPoint = OcrWorkbench.Contracts.SpatialPoint;
using WireBlock = OcrWorkbench.Contracts.Plugin.V1.SpatialTextBlock;

namespace OcrWorkbench.PluginHost;

public sealed class WorkerProcessRecognizer : IRecognizer
{
    private static readonly TimeSpan CancellationGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Envelope>> _pending = [];
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private int _faulted;
    private int _disposed;

    private WorkerProcessRecognizer(Process process, Action<string>? log)
    {
        _process = process;
        _stdoutPump = PumpStandardOutputAsync();
        _stderrPump = PumpStandardErrorAsync(process.StandardError, log, _lifetime.Token);
    }

    public string PluginId { get; private set; } = string.Empty;
    public string PluginVersion { get; private set; } = string.Empty;
    public IReadOnlyList<string> Capabilities { get; private set; } = [];
    public uint MaximumConcurrency { get; private set; }
    public RecognizerIdentity Identity => new(PluginId, PluginVersion);

    internal bool IsHealthy => Volatile.Read(ref _faulted) == 0
        && Volatile.Read(ref _disposed) == 0
        && !_process.HasExited;

    public static async Task<WorkerProcessRecognizer> StartAsync(
        string workerPath,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(workerPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Worker entrypoint does not exist.", fullPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : fullPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(fullPath);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start worker: {fullPath}");
        var recognizer = new WorkerProcessRecognizer(process, log);
        try
        {
            await recognizer.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            return recognizer;
        }
        catch
        {
            await recognizer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
        PageArtifact page,
        RecognitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var correlationId = Guid.NewGuid().ToString("N");
            var responseTask = await DispatchAsync(
                new Envelope
                {
                    ProtocolVersion = PluginFraming.CurrentProtocolVersion,
                    CorrelationId = correlationId,
                    ProcessPageRequest = new ProcessPageRequest
                    {
                        ArtifactPath = page.SourcePath,
                        MimeType = page.MimeType,
                        Language = options.Language,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            Envelope response;
            try
            {
                response = await responseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CancelAndDrainAsync(correlationId, responseTask).ConfigureAwait(false);
                throw;
            }

            return MapOutcome(response);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _faulted) == 0 && !_process.HasExited)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(ShutdownGracePeriod);
                    var response = await ExchangeAsync(
                        new Envelope
                        {
                            ProtocolVersion = PluginFraming.CurrentProtocolVersion,
                            CorrelationId = Guid.NewGuid().ToString("N"),
                            ShutdownRequest = new ShutdownRequest(),
                        },
                        timeout.Token).ConfigureAwait(false);
                    if (response.PayloadCase != Envelope.PayloadOneofCase.Ack)
                    {
                        throw new InvalidDataException("Worker did not acknowledge shutdown.");
                    }
                }
                catch (Exception) when (_process.HasExited)
                {
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            StopProcess(new ObjectDisposedException(nameof(WorkerProcessRecognizer)));
            await WaitForProcessAndPumpsAsync().ConfigureAwait(false);
            _process.Dispose();
            _operationGate.Dispose();
            _writeLock.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(
            new Envelope
            {
                ProtocolVersion = PluginFraming.CurrentProtocolVersion,
                CorrelationId = Guid.NewGuid().ToString("N"),
                HelloRequest = new HelloRequest
                {
                    HostVersion = typeof(WorkerProcessRecognizer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    MinimumProtocolVersion = PluginFraming.CurrentProtocolVersion,
                    MaximumProtocolVersion = PluginFraming.CurrentProtocolVersion,
                },
            },
            cancellationToken).ConfigureAwait(false);

        if (response.PayloadCase != Envelope.PayloadOneofCase.HelloResponse)
        {
            throw new InvalidDataException($"Worker rejected handshake with {response.PayloadCase}.");
        }

        var hello = response.HelloResponse;
        if (hello.Kind != PluginKind.Recognizer
            || hello.NegotiatedProtocolVersion != PluginFraming.CurrentProtocolVersion
            || hello.MaximumConcurrency == 0)
        {
            throw new InvalidDataException("Worker is not a compatible recognizer.");
        }

        PluginId = hello.PluginId;
        PluginVersion = hello.PluginVersion;
        Capabilities = hello.Capabilities.ToArray();
        MaximumConcurrency = hello.MaximumConcurrency;
    }

    private async Task CancelAndDrainAsync(string targetCorrelationId, Task<Envelope> targetResponse)
    {
        try
        {
            var cancelResponseTask = await DispatchAsync(
                new Envelope
                {
                    ProtocolVersion = PluginFraming.CurrentProtocolVersion,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    CancelRequest = new CancelRequest { TargetCorrelationId = targetCorrelationId },
                },
                _lifetime.Token).ConfigureAwait(false);

            await Task.WhenAll(targetResponse, cancelResponseTask)
                .WaitAsync(CancellationGracePeriod, _lifetime.Token)
                .ConfigureAwait(false);
            if (cancelResponseTask.Result.PayloadCase != Envelope.PayloadOneofCase.Ack)
            {
                throw new InvalidDataException("Worker did not acknowledge cancellation.");
            }
        }
        catch (Exception exception)
        {
            StopProcess(new InvalidOperationException(
                $"Worker did not complete cooperative cancellation within {CancellationGracePeriod.TotalSeconds:0} seconds.",
                exception));
        }
    }

    private async Task<Envelope> ExchangeAsync(Envelope request, CancellationToken cancellationToken)
    {
        var responseTask = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        return await responseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Task<Envelope>> DispatchAsync(
        Envelope request,
        CancellationToken dispatchCancellation)
    {
        ThrowIfUnavailable(allowDisposing: request.PayloadCase == Envelope.PayloadOneofCase.ShutdownRequest);
        await _writeLock.WaitAsync(dispatchCancellation).ConfigureAwait(false);
        TaskCompletionSource<Envelope>? completion = null;
        try
        {
            ThrowIfUnavailable(allowDisposing: request.PayloadCase == Envelope.PayloadOneofCase.ShutdownRequest);
            dispatchCancellation.ThrowIfCancellationRequested();
            completion = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(request.CorrelationId, completion))
            {
                throw new InvalidOperationException($"Duplicate worker correlation ID: {request.CorrelationId}.");
            }

            await PluginFraming.WriteAsync(
                _process.StandardInput.BaseStream,
                request,
                _lifetime.Token).ConfigureAwait(false);
            return completion.Task;
        }
        catch (Exception exception)
        {
            if (completion is not null)
            {
                _pending.TryRemove(request.CorrelationId, out _);
                completion.TrySetException(exception);
            }

            if (exception is not OperationCanceledException || !dispatchCancellation.IsCancellationRequested)
            {
                StopProcess(exception);
            }

            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PumpStandardOutputAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var response = await PluginFraming.ReadAsync(
                    _process.StandardOutput.BaseStream,
                    _lifetime.Token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Worker exited without closing the protocol cleanly.");
                if (string.IsNullOrWhiteSpace(response.CorrelationId)
                    || !_pending.TryRemove(response.CorrelationId, out var completion))
                {
                    throw new InvalidDataException(
                        $"Worker returned an unsolicited correlation ID: '{response.CorrelationId}'.");
                }

                completion.TrySetResult(response);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StopProcess(exception);
        }
    }

    private void StopProcess(Exception exception)
    {
        Interlocked.Exchange(ref _faulted, 1);
        _lifetime.Cancel();
        foreach (var pending in _pending.ToArray())
        {
            if (_pending.TryRemove(pending.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception processException) when (processException is InvalidOperationException
                                                 or System.ComponentModel.Win32Exception)
        {
        }
    }

    private async Task WaitForProcessAndPumpsAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        await IgnorePumpFailureAsync(_stdoutPump).ConfigureAwait(false);
        await IgnorePumpFailureAsync(_stderrPump).ConfigureAwait(false);
    }

    private void ThrowIfUnavailable(bool allowDisposing = false)
    {
        if (!allowDisposing)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }

        if (Volatile.Read(ref _faulted) != 0 || _process.HasExited)
        {
            throw new InvalidOperationException("Worker process is unavailable.");
        }
    }

    private static PluginOutcome<RecognitionResult> MapOutcome(Envelope response) => response.PayloadCase switch
    {
        Envelope.PayloadOneofCase.ProcessPageResponse => new PluginOutcome<RecognitionResult>.Succeeded(
            new RecognitionResult(
                response.ProcessPageResponse.Blocks.Select(MapBlock).ToArray(),
                NullIfEmpty(response.ProcessPageResponse.SemanticMarkdown))),
        Envelope.PayloadOneofCase.DeclinedResponse => new PluginOutcome<RecognitionResult>.Declined(
            response.DeclinedResponse.ReasonCode,
            response.DeclinedResponse.Message,
            response.DeclinedResponse.Retryable,
            response.DeclinedResponse.AllowedFallbackClasses.ToArray()),
        Envelope.PayloadOneofCase.ErrorResponse => new PluginOutcome<RecognitionResult>.Failed(
            response.ErrorResponse.ErrorCode,
            response.ErrorResponse.Message,
            response.ErrorResponse.Retryable),
        _ => new PluginOutcome<RecognitionResult>.Failed(
            "Protocol.UnexpectedResponse",
            $"Unexpected worker response: {response.PayloadCase}.",
            false),
    };

    private static DomainBlock MapBlock(WireBlock block) => new(
        block.Text,
        block.Polygon.Select(point => new DomainPoint(point.X, point.Y)).ToArray(),
        block.Confidence,
        block.Source);

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task PumpStandardErrorAsync(
        StreamReader error,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await error.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            log?.Invoke(line);
        }
    }

    private static async Task IgnorePumpFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
