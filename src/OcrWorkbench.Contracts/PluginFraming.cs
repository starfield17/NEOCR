using System.Buffers.Binary;
using Google.Protobuf;
using OcrWorkbench.Contracts.Plugin.V1;

namespace OcrWorkbench.Contracts;

public static class PluginFraming
{
    public const uint CurrentProtocolVersion = 1;
    public const int MaximumFrameBytes = 16 * 1024 * 1024;

    public static async ValueTask WriteAsync(
        Stream output,
        Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        var payload = envelope.ToByteArray();
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InvalidDataException($"Protocol frame is {payload.Length} bytes; maximum is {MaximumFrameBytes}.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<Envelope?> ReadAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        var headerRead = await ReadAtMostAsync(input, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead != header.Length)
        {
            throw new EndOfStreamException("Worker protocol ended inside a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaximumFrameBytes)
        {
            throw new InvalidDataException($"Invalid worker protocol frame length: {length}.");
        }

        var payload = new byte[length];
        var payloadRead = await ReadAtMostAsync(input, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead != length)
        {
            throw new EndOfStreamException("Worker protocol ended inside a frame payload.");
        }

        return Envelope.Parser.ParseFrom(payload);
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

