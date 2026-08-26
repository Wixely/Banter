namespace Banter.Voice;

/// <summary>Whose messages a room is read aloud for (PLAN §6, "TTS policy").</summary>
public enum ReadbackPolicy
{
    Off,

    /// <summary>The default: agents are read aloud, people are not.</summary>
    AgentsOnly,

    Everyone,
}

/// <summary>Who gets spoken.</summary>
public static class Readback
{
    /// <summary>
    /// Whether a message should be read aloud.
    ///
    /// <para>A user's own messages are never spoken, under any policy. In always-listening mode
    /// speaking them puts them back through the microphone, which transcribes them, which sends
    /// them — a loop the room cannot break out of. Even without that, hearing a machine repeat
    /// what you just said is not a feature.</para>
    /// </summary>
    public static bool ShouldSpeak(ReadbackPolicy policy, bool senderIsAgent, bool senderIsSelf) =>
        !senderIsSelf && policy switch
        {
            ReadbackPolicy.Off => false,
            ReadbackPolicy.AgentsOnly => senderIsAgent,
            ReadbackPolicy.Everyone => true,
            _ => false,
        };
}

/// <summary>
/// Gives each sender a voice of their own, so a room with three agents in it is followable by ear
/// (PLAN §6). Keyed by name rather than by arrival, so an agent that reconnects, or that is heard
/// in two rooms, sounds like itself both times.
///
/// <para>Hold one of these per client rather than per room: an agent moved between rooms by an
/// op (§8a) changing voice on the way would read as a different agent.</para>
///
/// <para>Not thread-safe; assign from the thread that handles incoming messages.</para>
/// </summary>
public sealed class VoiceAssignment
{
    private readonly IReadOnlyList<VoiceDescriptor> _pool;
    private readonly Dictionary<string, string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _assigned = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    public VoiceAssignment(IReadOnlyList<VoiceDescriptor> pool) => _pool = pool;

    /// <summary>
    /// The voice for <paramref name="sender"/>, or null when the backend offered no voices at all
    /// — in which case the caller sends no voice and takes the server's default.
    ///
    /// <para>A name's hash picks the voice; where that one is already spoken for, the next free
    /// one is taken instead. Pure hashing would be the more stable rule, but it collides often
    /// enough to matter — six voices and five agents lose two of them to a shared voice more
    /// often than not — and two agents sounding identical defeats the point of assigning voices
    /// at all. The cost is that a collision resolves by who was heard first.</para>
    ///
    /// <para>Past the size of the pool, senders do share. <see cref="Pin"/> is the answer for a
    /// room where it matters which ones.</para>
    /// </summary>
    public string? For(string sender)
    {
        if (_pinned.TryGetValue(sender, out var pinned))
        {
            return pinned;
        }

        if (_assigned.TryGetValue(sender, out var already))
        {
            return already;
        }

        if (_pool.Count == 0)
        {
            return null;
        }

        var start = (int)(Hash(sender) % (uint)_pool.Count);
        var chosen = _pool[start].Id;

        for (var offset = 0; offset < _pool.Count; offset++)
        {
            var candidate = _pool[(start + offset) % _pool.Count].Id;
            if (_taken.Add(candidate))
            {
                chosen = candidate;
                break;
            }
        }

        _assigned[sender] = chosen;
        return chosen;
    }

    /// <summary>Fixes a sender's voice, overriding what their name would otherwise pick.</summary>
    public void Pin(string sender, string voiceId) => _pinned[sender] = voiceId;

    public void Unpin(string sender) => _pinned.Remove(sender);

    /// <summary>
    /// FNV-1a over the lowercased name. Not <c>string.GetHashCode</c>: that is seeded per process,
    /// so every restart would deal the voices out again and the room would sound like a different
    /// cast of characters each morning.
    /// </summary>
    private static uint Hash(string sender)
    {
        var hash = 2166136261u;
        foreach (var c in sender.ToLowerInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}
