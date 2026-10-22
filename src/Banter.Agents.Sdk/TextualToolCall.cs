using System.Text.Json;
using System.Text.RegularExpressions;

namespace Banter.Agents.Sdk;

/// <summary>
/// A tool call a model wrote as text instead of making. Local models do this constantly - a
/// chat template without tool support, an endpoint that does not implement function calling, a
/// model that learned the Hermes format - and what reaches the room is
/// <c>&lt;tool_call&gt;{"name":"open_side_room",…}&lt;/tool_call&gt;</c> while nothing happens.
/// Reported as "garbage xml/tool call nonsense and nothing happens", which is exactly it.
///
/// <para>Recognised here and run as the call it was meant to be. Three shapes cover what is seen
/// in the wild: Hermes/Qwen's <c>&lt;tool_call&gt;</c> JSON, Llama's <c>&lt;function=name&gt;</c>,
/// and a bare or fenced JSON object naming a tool.</para>
/// </summary>
public static partial class TextualToolCall
{
    /// <summary>How each shape begins. The stream is held back while what has arrived could still
    /// be one of these, and released the moment it cannot be.</summary>
    private static readonly string[] Openers = ["<tool_call>", "<function_call>", "<function=", "```json", "```", "{\"name\"", "{ \"name\""];

    /// <summary>Whether <paramref name="soFar"/> could still turn out to be a textual tool call -
    /// it is a prefix of an opener, or begins with one.</summary>
    public static bool MightBe(string soFar)
    {
        var s = soFar.TrimStart();
        if (s.Length == 0)
        {
            return true;
        }

        foreach (var opener in Openers)
        {
            if (s.StartsWith(opener, StringComparison.Ordinal) || opener.StartsWith(s, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^\s*<tool_call>\s*(?<json>\{.*\})\s*(</tool_call>)?\s*$", RegexOptions.Singleline)]
    private static partial Regex Hermes();

    [GeneratedRegex(@"^\s*<function_call>\s*(?<json>\{.*\})\s*(</function_call>)?\s*$", RegexOptions.Singleline)]
    private static partial Regex FunctionCall();

    [GeneratedRegex(@"^\s*<function=(?<name>[A-Za-z0-9_\-.]+)>\s*(?<json>\{.*?\})?\s*(</function>)?\s*$", RegexOptions.Singleline)]
    private static partial Regex Llama();

    [GeneratedRegex(@"^\s*```(?:json)?\s*(?<json>\{.*\})\s*```\s*$", RegexOptions.Singleline)]
    private static partial Regex Fenced();

    /// <summary>The tool and its arguments (as JSON) when <paramref name="text"/> is one call and
    /// nothing else; false for prose, for prose with a call inside it, and for anything the
    /// shapes above do not match - a model that wrote a sentence meant it to be read.</summary>
    public static bool TryParse(string text, out string name, out string arguments)
    {
        name = "";
        arguments = "{}";

        if (Llama().Match(text) is { Success: true } llama)
        {
            name = llama.Groups["name"].Value;
            arguments = llama.Groups["json"].Success ? llama.Groups["json"].Value : "{}";
            return IsObject(arguments);
        }

        string? json = null;
        foreach (var shape in new[] { Hermes(), FunctionCall(), Fenced() })
        {
            if (shape.Match(text) is { Success: true } m)
            {
                json = m.Groups["json"].Value;
                break;
            }
        }

        json ??= text.Trim();
        if (!json.StartsWith('{') || !json.EndsWith('}'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // {"function": {"name": …, "arguments": …}} - the OpenAI shape, written out.
            if (root.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                root = fn;
            }

            var named = FirstString(root, "name", "tool", "tool_name", "function_name");
            if (named is not { Length: > 0 })
            {
                return false;
            }

            name = named;
            arguments = FirstArguments(root, "arguments", "parameters", "args", "input");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FirstString(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }

        return null;
    }

    /// <summary>Arguments as a JSON object string: an object as it is, a string of JSON as that
    /// JSON, and nothing as an empty object.</summary>
    private static string FirstArguments(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var v))
            {
                continue;
            }

            if (v.ValueKind == JsonValueKind.Object)
            {
                return v.GetRawText();
            }

            if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s && IsObject(s))
            {
                return s;
            }
        }

        return "{}";
    }

    private static bool IsObject(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
