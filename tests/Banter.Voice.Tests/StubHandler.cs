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
