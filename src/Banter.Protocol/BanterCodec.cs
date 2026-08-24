using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using MessagePack.Resolvers;

namespace Banter.Protocol;

/// <summary>Wire encodings. MessagePack is the protocol; JSON exists purely for debugging
/// (readable frames in logs and network captures), switchable per codec instance.</summary>
public enum BanterWireFormat
{
    MessagePack,
    Json,
}

/// <summary>
/// Encodes payload objects into envelopes and envelopes into bytes, and back. Stateless and
/// thread-safe; create one per connection or share one per process.
/// </summary>
public sealed class BanterCodec(BanterWireFormat format = BanterWireFormat.MessagePack)
{
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        MessagePackSerializerOptions.Standard
            .WithResolver(StandardResolver.Instance)
            .WithSecurity(MessagePackSecurity.UntrustedData);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public BanterWireFormat Format { get; } = format;

    /// <summary>Wraps a payload in a v1 envelope, serializing it in this codec's format.</summary>
    public BanterEnvelope CreateEnvelope<TPayload>(TPayload payload, string? replyTo = null)
        where TPayload : notnull
    {
        var type = PayloadRegistry.MessageTypeFor(payload.GetType());
        var payloadBytes = Format == BanterWireFormat.MessagePack
            ? MessagePackSerializer.Serialize(payload.GetType(), payload, MsgPackOptions)
            : JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), JsonOptions);
        return new BanterEnvelope(BanterEnvelope.CurrentVersion, type, BanterEnvelope.NewMsgId(), replyTo, payloadBytes);
    }

    public byte[] EncodeEnvelope(BanterEnvelope envelope) =>
        Format == BanterWireFormat.MessagePack
            ? MessagePackSerializer.Serialize(envelope, MsgPackOptions)
            : JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

    public BanterEnvelope DecodeEnvelope(ReadOnlyMemory<byte> bytes) =>
        Format == BanterWireFormat.MessagePack
            ? MessagePackSerializer.Deserialize<BanterEnvelope>(bytes, MsgPackOptions)
            : JsonSerializer.Deserialize<BanterEnvelope>(bytes.Span, JsonOptions)
                ?? throw new InvalidDataException("Envelope decoded to null.");

    /// <summary>
    /// Deserializes the envelope's payload via the registry. Returns null for message types this
    /// peer has no contract for — callers treat that as "known envelope, unknown payload" rather
    /// than an error, which is what keeps mixed-version fleets talking.
    /// </summary>
    public object? DecodePayload(BanterEnvelope envelope)
    {
        var payloadType = PayloadRegistry.PayloadTypeFor(envelope.Type);
        if (payloadType is null)
        {
            return null;
        }

        return Format == BanterWireFormat.MessagePack
            ? MessagePackSerializer.Deserialize(payloadType, envelope.Payload, MsgPackOptions)
            : JsonSerializer.Deserialize(envelope.Payload, payloadType, JsonOptions);
    }

    public TPayload DecodePayload<TPayload>(BanterEnvelope envelope) where TPayload : class =>
        DecodePayload(envelope) as TPayload
            ?? throw new InvalidDataException(
                $"Envelope {envelope.Type} did not decode to {typeof(TPayload).Name}.");
}
