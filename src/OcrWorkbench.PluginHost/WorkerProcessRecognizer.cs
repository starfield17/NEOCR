using System.Diagnostics;
using OcrWorkbench.Contracts;
using OcrWorkbench.Contracts.Plugin.V1;
using OcrWorkbench.Core;
using DomainPoint = OcrWorkbench.Contracts.SpatialPoint;
using DomainBlock = OcrWorkbench.Contracts.SpatialTextBlock;
using WireBlock = OcrWorkbench.Contracts.Plugin.V1.SpatialTextBlock;

namespace OcrWorkbench.PluginHost;

public sealed class WorkerProcessRecognizer : IRecognizer
{
    private readonly Process _process;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _stderrPump;
    private bool _disposed;

    private WorkerProcessRecognizer(Process process, Action<string>? log)
    {
        _process = process;
        _stderrPump = PumpStandardErrorAsync(process.StandardError, log, _lifetime.Token);
    }

    public string PluginId { get; private set; } = string.Empty;
    public string PluginVersion { get; private set; } = string.Empty;
    public IReadOnlyList<string> Capabilities { get; private set; } = [];

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        var response = await ExchangeAsync(
            new Envelope
            {
                ProtocolVersion = PluginFraming.CurrentProtocolVersion,
                CorrelationId = Guid.NewGuid().ToString("N"),
                ProcessPageRequest = new ProcessPageRequest
                {
                    ArtifactPath = page.SourcePath,
                    MimeType = page.MimeType,
                    Language = options.Language,
                },
            },
            cancellationToken).ConfigureAwait(false);

        return response.PayloadCase switch
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
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await ExchangeAsync(
                        new Envelope
                        {
                            ProtocolVersion = PluginFraming.CurrentProtocolVersion,
                            CorrelationId = Guid.NewGuid().ToString("N"),
                            ShutdownRequest = new ShutdownRequest(),
                        },
                        timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) when (timeout.IsCancellationRequested || _process.HasExited)
                {
                }
            }
        }
        finally
        {
            _lifetime.Cancel();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await _process.WaitForExitAsync().ConfigureAwait(false);
            try
            {
                await _stderrPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _process.Dispose();
            _requestLock.Dispose();
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
        if (hello.Kind != PluginKind.Recognizer || hello.NegotiatedProtocolVersion != PluginFraming.CurrentProtocolVersion)
        {
            throw new InvalidDataException("Worker is not a compatible recognizer.");
        }

        PluginId = hello.PluginId;
        PluginVersion = hello.PluginVersion;
        Capabilities = hello.Capabilities.ToArray();
    }

    private async Task<Envelope> ExchangeAsync(Envelope request, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"Worker exited with code {_process.ExitCode}.");
            }

            await PluginFraming.WriteAsync(
                _process.StandardInput.BaseStream,
                request,
                cancellationToken).ConfigureAwait(false);
            var response = await PluginFraming.ReadAsync(
                _process.StandardOutput.BaseStream,
                cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Worker exited without a response.");
            if (!string.Equals(response.CorrelationId, request.CorrelationId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Worker response correlation ID does not match the request.");
            }

            return response;
        }
        finally
        {
            _requestLock.Release();
        }
    }

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
}
