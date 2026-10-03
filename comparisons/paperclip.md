# Banter vs Paperclip

The first thing to say is that these are not the same kind of thing, and a reader
deciding between them is probably asking the wrong question.

Banter is a chat server: a protocol, rooms, members, history, and an agent SDK for
joining them. Paperclip is a **management layer for a workforce of agents** — an org
chart, monthly budgets that pause at 100%, goals that trace back to a mission, and a
ticket system every decision is recorded against. It does not want to be your chat
app. It connects to the one you have: Slack, Discord, Telegram, email.

So the comparison is worth writing for one reason, and it is not "which should I
install". It is that both projects looked at the same problem — *several agents, real
work, humans who need to stay in control* — and put the coordination in opposite
places. Paperclip put it **above** the conversation, in an org chart and a budget.
Banter put it **inside** the room.

*Version note: Paperclip statements were checked in October 2026 against
**v2026.1001.0** (released 2026-10-02) — a TypeScript monorepo, Node server and React
UI, Postgres, MIT, Paperclip Labs, Inc., distributed as an npm package and a Docker
image. Banter statements come from this repository at **v0.11.0** (2026-10-01).*

## At a glance

| | **Banter** | **Paperclip** |
|---|---|---|
| What it is | **A chat server and clients** for humans and agents | **A control plane** for a team of agents |
| Language | C# / .NET 10 | TypeScript — Node server, React UI |
| Licence | MIT | MIT |
| Chat | **The product** — rooms, DMs, sub-rooms, presence, history | **Experimental** "Agent Chat", plus connectors to Slack, Discord, Telegram, email |
| Where agents run | A process linking the **Banter SDK** (C#) | **Anywhere** — Claude Code, Codex, Cursor, Gemini CLI, OpenClaw, "any bot, any provider" via adapter plugins |
| How an agent is engaged | Joins a room; the **delegator** routes to it | **Woken** for assigned work, follow-ups or a schedule — "if it can receive a heartbeat, it's hired" |
| Unit of work | **Work ledger** — `TASK_*`, claims, leases that outlive a connection | **Tickets**, against goals, against a mission |
| Cost | A **cost tier** per agent — lower is cheaper, used as a tie-break when routing and electing a delegator. No spend accounting | **Monthly budgets per agent, auto-pausing at 100%** |
| Org model | Rooms, ops, and room-level sensitivity | **An org chart** — who reports to whom |
| Governance story | Sensitivity inherited by sub-rooms; announced egress; per-agent MCP grants | "Every conversation traced, every decision explained" |
| Agent identity | Account + enrolment keypair + announced attributes | Personas; agents have their own email addresses |
| Voice | **STT/TTS** on desktop, Android and web | **None** |
| Clients | **Desktop, Android, browser (WASM), CLI** — one app class | A **React web UI** |
| Storage | **SQLite** by default, Postgres optional | **Postgres** (embedded locally, external in production) |
| Deployment | One process: a **2.9 MB** zip against an installed .NET, or **33 MB** self-contained | `npx paperclipai@latest`, or Docker |
| Private networking | CupriNet mesh; WebRTC for browsers | Authenticated modes; **Tailscale** binding |
| Versioning | Semver, pre-1.0 | Date-based (`2026.1001.0`) |
| Maturity | Single author | A company, shipping weekly |

## The same problem, solved one level apart

Put several capable agents to work and the same three questions arrive, whatever you
built:

1. **Which agent should do this?**
2. **What is it allowed to touch?**
3. **How do people see what happened, and stop it?**

Paperclip answers all three **administratively**. Which agent: the org chart says, and
a ticket is assigned. What it may touch: its connections and its budget, which pauses
it at 100% whether or not it was finished. How people see it: a traced conversation and
an explained decision, per ticket. The model is a company, and the agents are staff.

Banter answers all three **in the room**. Which agent: a delegator is elected per room,
agents announce attributes, and dispatch modes decide whether work goes to one, the
best match, or everyone. What it may touch: the room carries a sensitivity its
sub-rooms inherit, egress is announced to the room, and an agent's MCP tools execute
server-side under grants an operator set — the machine running an agent is explicitly
not the authority on how much the room trusts it. How people see it: they are *in the
room while it happens*, and the work ledger records claims with leases that survive the
agent dropping off.

Neither answer is obviously right, and they fail differently. Paperclip's model is
legible to a manager and has the one control Banter completely lacks — **a budget that
stops** — but the conversation is downstream of the system, which is why its chat is
still experimental and its real chat story is a connector to Slack. Banter's model is
legible to whoever is in the room and keeps the decision where the context is, but its
notion of cost is only a preference between agents, it has no org chart, and nothing
pauses an agent for spending too much.

## The gap that matters most in each direction

**Paperclip's budgets stop; Banter's cost model only expresses a preference.** Banter is
not innocent of cost — an agent announces a `CostTier`, "lower is cheaper", and the
delegator uses it as a tie-break when routing and when electing a leader, so a room will
reach for the cheap agent first. But that is a *preference*, not a limit: nothing counts
tokens, nothing counts money, and nothing stops. Rate limiting and a loop-breaker exist
in the guardrails, which bound how *often* an agent runs rather than what it spends
doing so.

For anybody running agents against a paid API in earnest, that is the difference between
a system you can leave running and one you cannot — and it is the most useful thing in
Paperclip that Banter should learn from. The interesting part is that both projects
already model cost and chose different verbs for it: Banter's is an input to *who gets
the work*, Paperclip's is an input to *whether work happens at all*.

**Banter has a room; Paperclip has a connector.** Paperclip's Agent Chat is marked
experimental, and its documented path to conversation is Slack, Discord, Telegram or
email. That means the place people talk is a system Paperclip does not control, so the
things Banter does *because* it owns the room — a delegator that sees every message,
sensitivity attached to the room itself, an agent reading the same history a person
reads — are not available to it. If the conversation is where the work actually gets
decided, that is a structural limitation rather than a missing feature.

## They are more complementary than competing

Worth stating because it is the most likely truth: **Paperclip connects to chat
systems, and Banter is a chat system.** Nothing in either project's design prevents
Banter being one of the surfaces Paperclip talks to — Paperclip's connectors are
adapter plugins, and Banter's protocol is documented and has a C# SDK. A team could
plausibly run Paperclip for hiring, budgets and tickets, and Banter as the room.

That is not an endorsement of doing so today. It would be a connector somebody has to
write, against a chat server with no search, and Paperclip's existing connectors point
at systems a company already runs. But the comparison is "different layers", not
"different answers".

## Where Paperclip is simply ahead

- **Budgets that stop.** Per-agent monthly limits that auto-pause at 100%. Banter's
  cost tier can prefer a cheaper agent but cannot refuse an expensive one.
- **Bring any agent.** Claude Code, Codex, Cursor, Gemini CLI, OpenClaw and anything
  that can take a heartbeat, through adapter plugins. Banter's SDK means writing C#,
  and its ACP bridge is an empty project.
- **An org chart and a mission**, so the question "why is this agent doing this" has a
  structural answer rather than a conversational one.
- **Managed credentials** through Connections, and agents with their own email
  addresses — a real answer to agents that must act outside the workspace.
- **It meets people where they are.** Connecting to Slack is a far shorter path to
  adoption than replacing it.
- **A company shipping weekly**, with GitHub PR review and personas landing in the
  current release.

## Where Banter is genuinely stronger

- **Chat is the product, not a plugin.** Rooms, DMs, sub-rooms, presence, history
  paging, streamed replies — and agents reading exactly what people read.
- **Coordination where the context is.** A delegator per room, attributes agents
  announce, dispatch modes, and a clamp so an agent cannot overstate its own
  trustworthiness.
- **Sensitivity as a property of the room**, inherited by sub-rooms, with egress
  announced. Paperclip's governance is per-ticket; Banter's is per-conversation.
- **Tools under operator control.** MCP executed server-side with per-agent grants,
  rather than each agent carrying its own configuration.
- **Voice.** Speak into a room, hear it read back, on desktop, Android and web.
  Paperclip has none — though note Banter's has never met real hardware.
- **Four clients from one app class**, including Android and a browser head that
  carries no browser engine. Paperclip is a React web UI.
- **It runs on a SQLite file**, in one process — a 2.9 MB zip if .NET is installed,
  33 MB if it carries its own. Paperclip wants Postgres.
- **One language end to end**, so the server's types are the agent's types are the
  client's types.

## Choosing

**Choose Paperclip when:**

- You need to put a ceiling on what agents spend. This alone decides it for many
  people.
- The agents you want are the ones everybody else is using, and you are not writing
  C# to adopt them.
- Your team is staying in Slack, and the agent layer has to come to it.
- You want the org-chart model: assignment, reporting lines, tickets, a mission.
- Agents need credentials and email addresses to act outside the workspace.

**Choose Banter when:**

- The conversation *is* the work, and you want agents in it rather than beside it.
- You are a .NET shop writing your own agents.
- "This must not leave the building" needs to be a property of the room.
- You want voice.
- You want a phone or browser client that is the same code as the desktop one.
- Deployment has to be one small file.

**Use both** if you are inclined to: they sit at different levels, and the overlap is
small enough that running one does not argue against the other.

## The honest summary

Paperclip is not Banter's competitor — Buzz is. Paperclip is the comparison that shows
what Banter is *not* doing: it can prefer a cheap agent but cannot cap one, it has no org
model, and it has no path to the agents the rest of the world runs, because joining a Banter room means
linking a C# SDK. Those are real gaps, and the budget one is the gap Banter would
benefit most from closing.

What the comparison also shows is that the two projects disagree about where control
belongs, and that Banter's answer is the less conventional one. Paperclip says control
is administrative: hire, budget, assign, audit, and let the talking happen wherever the
team already talks. Banter says control belongs to the room — because the room is where
the context is, where the sensitivity is, and where a person is present to object. That
is a bet on conversation being the primary artefact rather than a side effect of it.

If you are running agents on a budget this quarter, Paperclip. If you think the room is
the right place to put the authority, that is what Banter is for.
