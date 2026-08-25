using Banter.Protocol;

namespace Banter.Core;

/// <summary>What a request needs, before the roster is consulted.</summary>
public sealed record RequestClassification(
    DataSensitivity Sensitivity,
    IReadOnlyList<string> Skills,
    string Rationale);

/// <summary>
/// Decides how sensitive a request is and what skills it needs. Implementations may consult a
/// model; all of them must fail closed — an unsure answer is <see cref="DataSensitivity.Sensitive"/>,
/// because the cost of over-classifying is a slower answer and the cost of under-classifying is
/// data on someone else's servers, permanently.
/// </summary>
public interface IRequestClassifier
{
    Task<RequestClassification> ClassifyAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// A model-free classifier: keyword signals for sensitivity, keyword signals for skills, and
/// <see cref="DataSensitivity.Sensitive"/> whenever nothing clearly says otherwise.
///
/// <para>Deliberately conservative rather than clever. It is the fallback when no model is
/// configured and the backstop when one is unavailable, so its failure mode has to be "keep it
/// local" rather than "guess". Being wrong here costs a local answer; being wrong the other way
/// cannot be undone.</para>
/// </summary>
public sealed class KeywordRequestClassifier : IRequestClassifier
{
    /// <summary>Terms that mark a request as touching our own systems or people's data.</summary>
    private static readonly string[] SensitiveSignals =
    [
        "email", "inbox", "mailbox", "password", "credential", "secret", "token", "api key",
        "customer", "invoice", "salary", "payroll", "contract", "nda", "personal", "private",
        "internal", "confidential", "our database", "production", "prod db", "staff", "employee",
    ];

    /// <summary>
    /// Terms that mark a request as being about the outside world. Only these make something
    /// eligible to leave — the list is an allow-list, not a deny-list, precisely so that anything
    /// unrecognised stays local.
    /// </summary>
    private static readonly string[] PublicSignals =
    [
        "weather", "traffic", "news", "public", "open source", "documentation", "docs for",
        "how do i", "what is", "explain", "definition", "wikipedia", "stack overflow",
    ];

    private static readonly (string Skill, string[] Signals)[] SkillSignals =
    [
        ("code", ["code", "function", "bug", "compile", "refactor", "test", "stack trace", "exception"]),
        ("github", ["github", "pull request", " pr ", "issue", "repo", "repository", "commit", "branch"]),
        ("email", ["email", "inbox", "mailbox", "reply to", "forward"]),
        ("web", ["search", "look up", "website", "url", "browse"]),
        ("docs", ["document", "write up", "summarise", "summarize", "draft"]),
    ];

    public Task<RequestClassification> ClassifyAsync(string text, CancellationToken cancellationToken = default)
    {
        var lower = " " + text.ToLowerInvariant() + " ";

        var skills = SkillSignals
            .Where(s => s.Signals.Any(lower.Contains))
            .Select(s => s.Skill)
            .ToList();

        var sensitiveHit = SensitiveSignals.FirstOrDefault(lower.Contains);
        if (sensitiveHit is not null)
        {
            return Task.FromResult(new RequestClassification(
                DataSensitivity.Sensitive, skills, $"mentions '{sensitiveHit.Trim()}'"));
        }

        var publicHit = PublicSignals.FirstOrDefault(lower.Contains);
        if (publicHit is not null)
        {
            return Task.FromResult(new RequestClassification(
                DataSensitivity.Public, skills, $"looks like a general question ('{publicHit.Trim()}')"));
        }

        // Nothing said it was safe, so it is not treated as safe.
        return Task.FromResult(new RequestClassification(
            DataSensitivity.Sensitive, skills, "nothing marks this as public, so treating it as sensitive"));
    }
}

/// <summary>
/// Wraps another classifier and refuses to let it downgrade below a floor. Lets an operator say
/// "in this room, nothing is ever public" and have that beat whatever a model concludes — the
/// static-policy-wins rule from PLAN §8a.
/// </summary>
public sealed class FlooredClassifier(IRequestClassifier inner, DataSensitivity floor) : IRequestClassifier
{
    public async Task<RequestClassification> ClassifyAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await inner.ClassifyAsync(text, cancellationToken).ConfigureAwait(false);
        if (result.Sensitivity >= floor && result.Sensitivity != DataSensitivity.Unknown)
        {
            return result;
        }

        return result with
        {
            Sensitivity = floor,
            Rationale = $"{result.Rationale}; raised to {floor.ToString().ToLowerInvariant()} by room policy",
        };
    }
}
