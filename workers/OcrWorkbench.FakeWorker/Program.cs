using OcrWorkbench.Contracts;
using OcrWorkbench.Contracts.Plugin.V1;
using WireSpatialTextBlock = OcrWorkbench.Contracts.Plugin.V1.SpatialTextBlock;

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();

while (await PluginFraming.ReadAsync(input) is { } request)
{
    var response = new Envelope
    {
        ProtocolVersion = PluginFraming.CurrentProtocolVersion,
        CorrelationId = request.CorrelationId,
    };

    switch (request.PayloadCase)
    {
        case Envelope.PayloadOneofCase.HelloRequest:
            response.HelloResponse = new HelloResponse
            {
                PluginId = "org.ocrworkbench.fake",
                PluginVersion = "0.1.0",
                Kind = PluginKind.Recognizer,
                NegotiatedProtocolVersion = PluginFraming.CurrentProtocolVersion,
                MaximumConcurrency = 1,
            };
            response.HelloResponse.Capabilities.Add("recognize.image");
            response.HelloResponse.Capabilities.Add("output.spatial-text");
            break;

        case Envelope.PayloadOneofCase.ProcessPageRequest:
            if (!File.Exists(request.ProcessPageRequest.ArtifactPath))
            {
                response.ErrorResponse = new ErrorResponse
                {
                    ErrorCode = "Input.NotFound",
                    Message = "Artifact path does not exist.",
                    Retryable = false,
                };
                break;
            }

            var block = new WireSpatialTextBlock
            {
                Text = $"FAKE OCR: {Path.GetFileName(request.ProcessPageRequest.ArtifactPath)}",
                Confidence = 1,
                Source = "fake",
            };
            block.Polygon.Add(new Point { X = 0, Y = 0 });
            block.Polygon.Add(new Point { X = 100, Y = 0 });
            block.Polygon.Add(new Point { X = 100, Y = 24 });
            block.Polygon.Add(new Point { X = 0, Y = 24 });
            response.ProcessPageResponse = new ProcessPageResponse();
            response.ProcessPageResponse.Blocks.Add(block);
            break;

        case Envelope.PayloadOneofCase.CancelRequest:
            response.Ack = new Ack();
            break;

        case Envelope.PayloadOneofCase.ShutdownRequest:
            response.Ack = new Ack();
            await PluginFraming.WriteAsync(output, response);
            return;

        default:
            response.ErrorResponse = new ErrorResponse
            {
                ErrorCode = "Protocol.UnsupportedRequest",
                Message = $"Unsupported request: {request.PayloadCase}.",
                Retryable = false,
            };
            break;
    }

    await PluginFraming.WriteAsync(output, response);
}
