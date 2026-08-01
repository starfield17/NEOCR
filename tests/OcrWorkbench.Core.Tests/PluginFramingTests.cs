using OcrWorkbench.Contracts;
using OcrWorkbench.Contracts.Plugin.V1;

namespace OcrWorkbench.Core.Tests;

public sealed class PluginFramingTests
{
    [Fact]
    public async Task RoundTripsEnvelope()
    {
        var stream = new MemoryStream();
        var expected = new Envelope
        {
            ProtocolVersion = PluginFraming.CurrentProtocolVersion,
            CorrelationId = "abc",
            HelloRequest = new HelloRequest { HostVersion = "test" },
        };

        await PluginFraming.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await PluginFraming.ReadAsync(stream);

        Assert.NotNull(actual);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal(Envelope.PayloadOneofCase.HelloRequest, actual.PayloadCase);
    }
}

