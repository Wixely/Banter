using Banter.Protocol;

namespace Banter.Client.Core;

public class BanterClientException(string message) : Exception(message);

/// <summary>The server rejected AUTH.</summary>
public sealed class BanterAuthException(string reason) : BanterClientException($"Authentication failed: {reason}");

/// <summary>A request was answered with an <see cref="ErrorPayload"/>.</summary>
public sealed class BanterErrorException(ErrorPayload error)
    : BanterClientException($"{error.Code}: {error.Message}")
{
    public string Code { get; } = error.Code;
}

/// <summary>The connection dropped while requests were in flight.</summary>
public sealed class BanterDisconnectedException() : BanterClientException("The connection to the server was lost.");
