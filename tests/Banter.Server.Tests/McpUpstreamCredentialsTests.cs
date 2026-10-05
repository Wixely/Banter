using Banter.Server.Tools;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// How Banter proves who it is to an MCP server that checks.
///
/// <para>This exists because of MCPHub: with per-user permissions on, Banter is a user there like
/// any other, and what it may reach is decided in the hub rather than here. Getting the key to the
/// hub is this file's whole job, and the failures worth testing are the quiet ones — a token named
/// and not found, or two named and one silently ignored.</para>
/// </summary>
public sealed class McpUpstreamCredentialsTests
{
    private static McpUpstreamConfig Hub(
        string? token = null, string? file = null, string? variable = null) => new()
    {
        Key = "mcphub",
        Url = "http://127.0.0.1:5800/mcp",
        Token = token,
        TokenFile = file,
        TokenEnvironmentVariable = variable,
    };

    [Fact]
    public void An_upstream_with_no_token_configured_sends_none()
    {
        Assert.Null(McpUpstreamCredentials.Resolve(Hub(), _ => null));
        Assert.Null(McpUpstreamCredentials.Headers(null));
    }

    [Fact]
    public void A_literal_token_becomes_a_bearer_header()
    {
        var token = McpUpstreamCredentials.Resolve(Hub(token: "mcphub_abc123"), _ => null);

        var headers = McpUpstreamCredentials.Headers(token);

        Assert.NotNull(headers);
        Assert.Equal("Bearer mcphub_abc123", headers["Authorization"]);
    }

    [Fact]
    public void A_token_can_come_from_the_environment()
    {
        var token = McpUpstreamCredentials.Resolve(
            Hub(variable: "BANTER_HUB_KEY"),
            name => name == "BANTER_HUB_KEY" ? "mcphub_from_env" : null);

        Assert.Equal("mcphub_from_env", token);
    }

    /// <summary>
    /// `echo secret > file` is how most mounted secrets are made, and a key with a newline on the
    /// end fails authentication in a way nothing on either side explains.
    /// </summary>
    [Fact]
    public void A_token_file_is_read_without_its_trailing_newline()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "mcphub_from_file\n");

            Assert.Equal("mcphub_from_file", McpUpstreamCredentials.Resolve(Hub(file: path), _ => null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Two sources means one of them is about to be ignored, and which one is an implementation
    /// detail nobody should have to guess at from the outside.
    /// </summary>
    [Fact]
    public void Naming_two_token_sources_is_refused_rather_than_ranked()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => McpUpstreamCredentials.Resolve(Hub(token: "a", variable: "B"), _ => null));

        Assert.Contains("mcphub", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("more than one", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one that must not pass silently. A deployment that meant to authenticate and did not
    /// either gets refused — which reads as the server being down — or gets served anonymously,
    /// which hands Banter whatever an unauthenticated caller is allowed.
    /// </summary>
    [Fact]
    public void A_token_that_is_named_and_missing_stops_the_upstream()
    {
        var fromEnvironment = Assert.Throws<InvalidOperationException>(
            () => McpUpstreamCredentials.Resolve(Hub(variable: "NOT_SET"), _ => null));
        Assert.Contains("TokenEnvironmentVariable", fromEnvironment.Message, StringComparison.Ordinal);

        var fromFile = Assert.Throws<InvalidOperationException>(
            () => McpUpstreamCredentials.Resolve(Hub(file: "/no/such/secret"), _ => null));
        Assert.Contains("mcphub", fromFile.Message, StringComparison.Ordinal);
    }

    /// <summary>The message is read from a log, and this one value is the credential.</summary>
    [Fact]
    public void A_failure_never_quotes_the_token()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => McpUpstreamCredentials.Resolve(Hub(token: "mcphub_secret", file: "/x"), _ => null));

        Assert.DoesNotContain("mcphub_secret", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Surrounding_whitespace_is_not_part_of_the_key()
    {
        Assert.Equal(
            "mcphub_abc",
            McpUpstreamCredentials.Resolve(Hub(variable: "K"), _ => "  mcphub_abc  "));
    }
}
