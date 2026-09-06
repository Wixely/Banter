using Banter.Core;
using Banter.Protocol;

namespace Banter.Server;

/// <summary>
/// Questions an agent asks the room, and the answers coming back.
///
/// <para>An ask is a message with choices attached, not a dialogue. The room has other agents
/// working in it and other people reading it, so stopping all of them to ask one of them something
/// would be the wrong shape — and a question nobody happens to be looking at should still be there
/// an hour later.</para>
///
/// <para>Pending asks live in memory. They are deliberately not persisted: the agent waiting on one
/// is a process with a timeout, and a server restart takes its wait with it — an answer relayed to
/// an agent that is no longer listening for it would be worse than none. The message that carried
/// the question is stored like any other, so the history still reads correctly afterwards.</para>
/// </summary>
internal sealed partial class RoomEngine
{
    private sealed record PendingAsk(string Room, string Asker, AskPayload Ask);

    private readonly Dictionary<string, PendingAsk> _asks = new(StringComparer.Ordinal);

    private async ValueTask HandleAskAsync(ClientSession session, BanterEnvelope envelope, AskPayload ask)
    {
        if (!_rooms.TryGetValue(ask.Room, out var room) || !room.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {ask.Room}."), replyTo: envelope.MsgId);
            return;
        }

        if (!session.IsAgent)
        {
            // A person with a question can simply ask it. Structured asks exist so an agent's
            // question can be answered by clicking rather than by typing something it can parse.
            session.Send(new ErrorPayload("NOT_AN_AGENT", "Only agents ask structured questions."),
                replyTo: envelope.MsgId);
            return;
        }

        if (ask.Questions.Count == 0 || ask.Questions.Any(q => q.Key.Length == 0))
        {
            session.Send(new ErrorPayload("BAD_ASK", "An ask needs at least one question, each with a key."),
                replyTo: envelope.MsgId);
            return;
        }

        var id = Guid.NewGuid().ToString("N");

        // The question goes into the room as an ordinary message too, so it is in the history, in
        // the readback, and legible to a client that knows nothing about asks. The ask then names
        // that message, which is what lets a client hang the controls off the right row rather
        // than floating them somewhere of its own choosing.
        var said = await RelayAsync(session, room, Describe(ask with { AskId = id })).ConfigureAwait(false);

        var owned = ask with { AskId = id, Asker = session.Nick, MessageId = said };
        _asks[id] = new PendingAsk(room.Name, session.Nick, owned);

        Broadcast(room, owned);
        session.Send(owned, replyTo: envelope.MsgId);
    }

    private async ValueTask HandleAnswerAsync(ClientSession session, BanterEnvelope envelope, AnswerPayload answer)
    {
        if (!_asks.TryGetValue(answer.AskId, out var pending))
        {
            // Already answered, or from before a restart. Not an error worth alarming anybody
            // with: the client simply stops offering it.
            session.Send(new ErrorPayload("ASK_CLOSED", "That question has already been answered."),
                replyTo: envelope.MsgId);
            return;
        }

        if (!_rooms.TryGetValue(pending.Room, out var room) || !room.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {pending.Room}."), replyTo: envelope.MsgId);
            return;
        }

        // Taken before anything can fail, so one question is answered once however this turns out.
        _asks.Remove(answer.AskId);

        var owned = answer with { Responder = session.Nick };
        var summary = Summarise(pending.Ask, owned);

        // Said in the room as an ordinary message as well: what somebody chose is part of the
        // conversation, and an answer that existed only as a payload would be invisible in the
        // history and to everybody who was not looking when it happened.
        // Threaded to the question: in a room where three agents are asking at once, "yes" on its
        // own belongs to whichever question the reader happens to assume it does.
        await RelayAsync(session, room, summary, pending.Ask.MessageId).ConfigureAwait(false);

        foreach (var target in SessionsFor(pending.Asker))
        {
            target.Send(owned);
        }

        Broadcast(room, new AskClosedPayload(room.Name, answer.AskId, session.Nick, summary));
        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    /// <summary>The question as a line of chat, for anything that cannot render the controls.</summary>
    private static string Describe(AskPayload ask)
    {
        var parts = ask.Questions.Select(q =>
        {
            var options = q.Options.Count == 0
                ? ""
                : " — " + string.Join(" / ", q.Options.Select(o => o.Label));
            return $"{q.Text}{options}";
        });

        return "[asking] " + string.Join("  ", parts);
    }

    private static string Summarise(AskPayload ask, AnswerPayload answer)
    {
        var parts = answer.Answers.Select(a =>
        {
            var question = ask.Questions.FirstOrDefault(q => q.Key == a.Key);
            var chosen = a.Chosen
                .Select(v => question?.Options.FirstOrDefault(o => o.Value == v)?.Label ?? v)
                .ToList();

            var said = chosen.Count > 0 ? string.Join(", ", chosen) : "";
            if (a.Text.Length > 0)
            {
                said = said.Length > 0 ? $"{said} — {a.Text}" : a.Text;
            }

            return $"{question?.Header ?? a.Key}: {(said.Length > 0 ? said : "no answer")}";
        });

        return "[answered] " + string.Join("  ", parts);
    }

    /// <summary>
    /// Drops every ask belonging to a session that has gone. An agent that is no longer here
    /// cannot be told what was chosen, so leaving its questions on screen would invite somebody
    /// to answer into nothing.
    /// </summary>
    private async ValueTask CloseAsksFromAsync(string nick)
    {
        foreach (var (id, pending) in _asks.Where(a => string.Equals(a.Value.Asker, nick, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _asks.Remove(id);
            if (_rooms.TryGetValue(pending.Room, out var room))
            {
                Broadcast(room, new AskClosedPayload(room.Name, id, "", $"{nick} left before this was answered."));
                await AnnounceSystemAsync(room, $"{nick} left; its question is no longer waiting for an answer.")
                    .ConfigureAwait(false);
            }
        }
    }
}
