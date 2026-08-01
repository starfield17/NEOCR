using System.Collections.Concurrent;
using OcrWorkbench.Contracts;
using OcrWorkbench.Contracts.Plugin.V1;
using WireSpatialTextBlock = OcrWorkbench.Contracts.Plugin.V1.SpatialTextBlock;

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var writeLock = new SemaphoreSlim(1, 1);
var active = new ConcurrentDictionary<string, CancellationTokenSource>();

while (await PluginFraming.ReadAsync(input) is { } request)
{
    switch (request.PayloadCase)
    {
        case Envelope.PayloadOneofCase.HelloRequest:
            await WriteAsync(new Envelope
            {
                ProtocolVersion = PluginFraming.CurrentProtocolVersion,
                CorrelationId = request.CorrelationId,
                HelloResponse = new HelloResponse
                {
                    PluginId = "org.ocrworkbench.fake",
                    PluginVersion = "0.1.0",
                    Kind = PluginKind.Recognizer,
                    NegotiatedProtocolVersion = PluginFraming.CurrentProtocolVersion,
                    MaximumConcurrency = 1,
                    Capabilities =
                    {
                        "recognize.image",
                        "output.spatial-text",
                    },
                },
            });
            break;

        case Envelope.PayloadOneofCase.ProcessPageRequest:
            var operationCancellation = new CancellationTokenSource();
            if (!active.TryAdd(request.CorrelationId, operationCancellation))
            {
                await WriteErrorAsync(request.CorrelationId, "Protocol.DuplicateCorrelation", "Correlation ID is already active.");
                operationCancellation.Dispose();
                break;
            }

            _ = ProcessPageAsync(request, operationCancellation);
            break;

        case Envelope.PayloadOneofCase.CancelRequest:
            if (active.TryGetValue(request.CancelRequest.TargetCorrelationId, out var target))
            {
                target.Cancel();
            }

            await WriteAsync(CreateAck(request.CorrelationId));
            break;

        case Envelope.PayloadOneofCase.ShutdownRequest:
            foreach (var operation in active.Values)
            {
                operation.Cancel();
            }

            await WriteAsync(CreateAck(request.CorrelationId));
            return;

        default:
            await WriteErrorAsync(
                request.CorrelationId,
                "Protocol.UnsupportedRequest",
                $"Unsupported request: {request.PayloadCase}.");
            break;
    }
}

async Task ProcessPageAsync(Envelope request, CancellationTokenSource cancellation)
{
    try
    {
        var page = request.ProcessPageRequest;
        switch (page.Language)
        {
            case "x-test-wait-for-cancel":
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                break;
            case "x-test-ignore-cancel":
                await Task.Delay(Timeout.InfiniteTimeSpan);
                break;
            case "x-test-crash":
                Environment.Exit(86);
                return;
        }

        if (!File.Exists(page.ArtifactPath))
        {
            await WriteErrorAsync(request.CorrelationId, "Input.NotFound", "Artifact path does not exist.");
            return;
        }

        var block = new WireSpatialTextBlock
        {
            Text = $"FAKE OCR: {Path.GetFileName(page.ArtifactPath)}",
            Confidence = 1,
            Source = $"fake:{Environment.ProcessId}",
        };
        block.Polygon.Add(new Point { X = 0, Y = 0 });
        block.Polygon.Add(new Point { X = 100, Y = 0 });
        block.Polygon.Add(new Point { X = 100, Y = 24 });
        block.Polygon.Add(new Point { X = 0, Y = 24 });
        var response = new Envelope
        {
            ProtocolVersion = PluginFraming.CurrentProtocolVersion,
            CorrelationId = page.Language == "x-test-wrong-correlation"
                ? Guid.NewGuid().ToString("N")
                : request.CorrelationId,
            ProcessPageResponse = new ProcessPageResponse(),
        };
        response.ProcessPageResponse.Blocks.Add(block);
        await WriteAsync(response);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        await WriteErrorAsync(request.CorrelationId, "Request.Cancelled", "Request was cancelled.");
    }
    finally
    {
        active.TryRemove(request.CorrelationId, out _);
        cancellation.Dispose();
    }
}

async Task WriteErrorAsync(string correlationId, string code, string message) => await WriteAsync(new Envelope
{
    ProtocolVersion = PluginFraming.CurrentProtocolVersion,
    CorrelationId = correlationId,
    ErrorResponse = new ErrorResponse
    {
        ErrorCode = code,
        Message = message,
        Retryable = false,
    },
});

async Task WriteAsync(Envelope response)
{
    await writeLock.WaitAsync();
    try
    {
        await PluginFraming.WriteAsync(output, response);
    }
    finally
    {
        writeLock.Release();
    }
}

static Envelope CreateAck(string correlationId) => new()
{
    ProtocolVersion = PluginFraming.CurrentProtocolVersion,
    CorrelationId = correlationId,
    Ack = new Ack(),
};
