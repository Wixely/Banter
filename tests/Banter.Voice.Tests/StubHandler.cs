using System.Net;

namespace Banter.Voice.Tests;

/// <summary>
/// Stands in for a speech server. Records what went out — the request body is where the
/// interesting assertions live, since the whole adapter is "put the right things in a form" —
/// and returns whatever the test wants back.
/// </summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public static StubHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    /// <summary>Serves bytes the way a speech server does, a few at a time — a synthesis arrives
    /// over the wire in pieces, and the adapter has to reassemble samples across them.</summary>
    public static StubHandler Audio(byte[] body, int bytesPerRead = 100) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChokedStream(body, bytesPerRead)),
        });

    public HttpRequestMessage? Request { get; private set; }

    /// <summary>The request body as text. Binary parts come through with replacement characters,
    /// which is fine: the form's field names and values are all ASCII.</summary>
    public string Body { get; private set; } = "";

    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        Request = request;
        Body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return respond(request);
    }
}

/// <summary>A stream that hands over at most <paramref name="bytesPerRead"/> bytes at a time.</summary>
internal sealed class ChokedStream(byte[] data, int bytesPerRead) : Stream
{
    private int _position;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var take = Math.Min(Math.Min(bytesPerRead, count), data.Length - _position);
        Array.Copy(data, _position, buffer, offset, take);
        _position += take;
        return take;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
