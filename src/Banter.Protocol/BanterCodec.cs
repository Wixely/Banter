using System.Diagnostics.CodeAnalysis;
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
    /// <summary>
    /// What every codec instance serializes with, and deliberately not <c>StandardResolver</c>.
    ///
    /// <para>StandardResolver ends in the four <c>Dynamic*</c> resolvers, which build formatters at
    /// run time with Reflection.Emit. A browser has no Reflection.Emit, so on the wasm head that
    /// tail is not a fallback, it is a crash on the first frame decoded — and merely referencing it
    /// is what failed a trimmed publish (IL2104, "assembly 'MessagePack' produced trim warnings").
    /// </para>
    ///
    /// <para>None of it was ever needed. MessagePack's source generator has already emitted a
    /// formatter for every <c>[MessagePackObject]</c> in this assembly, at compile time; composing
    /// that over the builtin primitives is the same behaviour with no code generation at all.
    /// <c>EveryRegisteredPayloadHasACompileTimeFormatter</c> is what keeps it honest: with the
    /// dynamic tail gone, a payload the generator missed throws instead of quietly working here and
    /// failing only in a browser.</para>
    /// </summary>
    public static readonly IFormatterResolver Resolver = CompositeResolver.Create(
        BuiltinResolver.Instance,
        AttributeFormatterResolver.Instance,
        GeneratedMessagePackResolver.Instance);

    private static readonly MessagePackSerializerOptions MsgPackOptions =
        MessagePackSerializerOptions.Standard
            .WithResolver(Resolver)
            .WithSecurity(MessagePackSecurity.UntrustedData);

    private static JsonSerializerOptions? _jsonOptions;

    /// <summary>
    /// Built on demand behind the same guard as the helpers below, and deliberately not a static
    /// readonly field. As a field its initializer runs whatever the feature switch says, and the
    /// non-generic JsonStringEnumConverter it constructs needs runtime code generation - which an
    /// ahead-of-time compile refuses outright (IL3050), even though nothing on that head will ever
    /// serialize a frame with it.
    /// </summary>
    private static JsonSerializerOptions JsonOptions => _jsonOptions ??= CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        if (!JsonWireFormatSupported) throw new PlatformNotSupportedException(Trimmed);
        return new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };
    }

    public BanterWireFormat Format { get; } = format;

    /// <summary>
    /// Whether this build carries the JSON wire format at all.
    ///
    /// <para>A trimmer feature switch. JSON is a debugging convenience - readable frames in logs
    /// and captures - and it is the ONLY thing in this assembly a trimmed publish cannot swallow:
    /// its serializer is reflection-based, so ILLink reports IL2026 on all four call sites and the
    /// publish fails. MessagePack has no such problem, because the resolver above is composed from
    /// source-generated formatters.
    /// </para>
    ///
    /// <para>A head that sets <c>Banter.Protocol.JsonWireFormatSupported=false</c> lets ILLink
    /// substitute this to a constant, see the guard in each helper below throw unconditionally,
    /// and drop the System.Text.Json calls after it as unreachable. The browser head does that; it
    /// has no log to read a frame out of, and pays about half its download for the privilege. The
    /// enum stays either way, because it is public API on a packaged assembly.</para>
    /// </summary>
    [FeatureSwitchDefinition("Banter.Protocol.JsonWireFormatSupported")]
    public static bool JsonWireFormatSupported =>
        !AppContext.TryGetSwitch("Banter.Protocol.JsonWireFormatSupported", out var supported) || supported;

    private const string Trimmed =
        "This build was published without the JSON wire format " +
        "(Banter.Protocol.JsonWireFormatSupported=false). MessagePack is the protocol; JSON is a " +
        "debugging aid, and a trimmed head does not carry it.";

    // The guard is INLINE in each of these rather than factored into one Require() call, and it has
    // to be: ILLink removes what follows an unconditional throw in the SAME method. Behind a call
    // it cannot see that, and every System.Text.Json reference survives - which is the whole point
    // of the exercise.
    private static byte[] ToJson(object value, Type type)
    {
        if (!JsonWireFormatSupported) throw new PlatformNotSupportedException(Trimmed);
        return JsonSerializer.SerializeToUtf8Bytes(value, type, JsonOptions);
    }

    private static byte[] ToJson(BanterEnvelope envelope)
    {
        if (!JsonWireFormatSupported) throw new PlatformNotSupportedException(Trimmed);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    private static BanterEnvelope EnvelopeFromJson(ReadOnlySpan<byte> bytes)
    {
        if (!JsonWireFormatSupported) throw new PlatformNotSupportedException(Trimmed);
        return JsonSerializer.Deserialize<BanterEnvelope>(bytes, JsonOptions)
               ?? throw new InvalidDataException("Envelope decoded to null.");
    }

    private static object? PayloadFromJson(ReadOnlyMemory<byte> payload, Type type)
    {
        if (!JsonWireFormatSupported) throw new PlatformNotSupportedException(Trimmed);
        return JsonSerializer.Deserialize(payload.Span, type, JsonOptions);
    }

    /// <summary>Wraps a payload in a v1 envelope, serializing it in this codec's format.</summary>
    public BanterEnvelope CreateEnvelope<TPayload>(TPayload payload, string? replyTo = null)
        where TPayload : notnull
    {
        var type = PayloadRegistry.MessageTypeFor(payload.GetType());
        var payloadBytes = Format == BanterWireFormat.MessagePack
            ? MessagePackSerializer.Serialize(payload.GetType(), payload, MsgPackOptions)
            : ToJson(payload, payload.GetType());
        return new BanterEnvelope(BanterEnvelope.CurrentVersion, type, BanterEnvelope.NewMsgId(), replyTo, payloadBytes);
    }

    public byte[] EncodeEnvelope(BanterEnvelope envelope) =>
        Format == BanterWireFormat.MessagePack
            ? MessagePackSerializer.Serialize(envelope, MsgPackOptions)
            : ToJson(envelope);

    public BanterEnvelope DecodeEnvelope(ReadOnlyMemory<byte> bytes) =>
        Format == BanterWireFormat.MessagePack
            ? MessagePackSerializer.Deserialize<BanterEnvelope>(bytes, MsgPackOptions)
            : EnvelopeFromJson(bytes.Span);

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
            : PayloadFromJson(envelope.Payload, payloadType);
    }

    public TPayload DecodePayload<TPayload>(BanterEnvelope envelope) where TPayload : class =>
        DecodePayload(envelope) as TPayload
            ?? throw new InvalidDataException(
                $"Envelope {envelope.Type} did not decode to {typeof(TPayload).Name}.");
}
