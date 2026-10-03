# Banter vs Buzz

Buzz is the project that made the **same bet**. Both start from the position that
an AI agent in a workspace should be a *participant* rather than an integration —
that it should hold an identity, be addressable in a room, and do work people can
see — and both conclude that this means building the workspace rather than bolting
a webhook onto someone else's. Block announced Buzz on 21 July 2026; Banter had
been doing the same thing on a smaller scale since the summer.

So this comparison cannot lean on "chat server vs agent platform". The differences
are narrower and sharper: **what sits at the centre of the network, what a message
is, how an agent attaches, and how much surrounds the chat.**

It is also the comparison where Banter comes off worst, and the document would be
useless if it hid that. Buzz ships several things Banter has only planned, has a
company behind it, and is further along on the thing both projects call auditing.

*Version note: Buzz statements were checked in October 2026 against **Buzz 0.5.26**
(released 2026-09-29) and the repository's `ARCHITECTURE.md` — a Rust monorepo under
Apache 2.0, Block, Inc. Desktop builds exist for macOS, Windows and Linux; mobile
clients are unfinished. Banter statements come from this repository at **v0.11.0**
(2026-10-01).*

## At a glance

| | **Banter** | **Buzz** |
|---|---|---|
| The shared bet | Humans and agents in the same rooms, self-hosted | Humans and agents in the same rooms, self-hosted |
| Language | **C# / .NET 10** end to end | **Rust** monorepo |
| Licence | MIT | Apache 2.0 |
| Backing | One author | **Block, Inc.** |
| Network shape | **A mesh** — CupriNet vessels, a WebRTC DataChannel for browsers, plain TCP as fallback | **One relay**, authoritative: "no peer-to-peer event exchange, no gossip, no replication" |
| What a message is | A MessagePack envelope on an authenticated session | **A signed Nostr event** (`kind` integer, secp256k1), individually verifiable |
| Identity | Accounts; agents enrol with a keypair and sign a login challenge | **Nostr keypairs for everyone**, humans and agents alike |
| Audit | Server-side history in SQL; no hash chain | **`buzz-audit` hash-chain log**, plus every action signed |
| To run one | **One process and a SQLite file** (Postgres optional) | Relay + **Postgres + Redis** (+ S3/Blossom for media) |
| Rooms | Rooms, sub-rooms with inherited sensitivity, DMs | **Channels in four kinds** — Stream, Forum, DM, Workflow |
| Threading | **None** | Forum channels |
| Search | **None** | `buzz-search` over Postgres FTS |
| Agents attach by | Linking the **agent SDK** into a C# process | **ACP** (`buzz-acp` spawns 1–32 agent subprocesses over stdio JSON-RPC) or `buzz-cli` |
| ACP | A project directory with nothing in it — **deferred** | **Shipped**, and the main way agents attach |
| Agent routing | **Delegator election, dispatch modes, announced attributes, sensitivity-based egress** | `@mention` events queued per channel |
| Work tracking | **Work ledger** — `TASK_*`, claims, leases that outlive a connection | **YAML workflow engine**, Workflow channels |
| Agent tools | **MCP executed server-side under per-agent grants** | Agent's own tools, in the agent's own process |
| Git hosting | **None** | **Included** |
| Voice | **STT/TTS** — speak a message, hear one read back | **Huddle** — live Opus between up to ~25 peers, no SFU |
| Attachments | Content-addressed, per-room grants, quotas, manifest-verified and resumable both ways | `buzz-media` via Blossom/S3, 50 MB per upload |
| Clients | Desktop (Win/macOS/Linux), **Android**, **browser via WebAssembly**, CLI | Desktop (Win/macOS/Linux), any Nostr app, web, HTTP bridge; **mobile unfinished** |
| UI technology | **CupriFace** — HTML/CSS painted by Skia, no browser, no JavaScript | Desktop app with a web stack |
| Maturity | Pre-1.0, single author, 1,252 tests | Pre-1.0, company-backed, moving weekly |

## The agreement, and why it still matters

Strip both projects to their claims and they say the same thing: a chat app with a
bot in it is not the same as a workspace an agent belongs to. Both act on it in the
same three ways.

- **The agent has an identity of its own.** Buzz gives every agent a Nostr keypair,
  the same kind of identity a person has. Banter gives it an account, an enrolment
  keypair, and a set of announced attributes the room routes on. Neither treats
  "which bot posted this" as a display-name convention.
- **The agent is addressable in the room.** Not a slash command that returns a
  private reply, but a participant others can see working. Buzz queues `@mention`
  events per channel; Banter has the delegator pick a recipient and announce it.
- **The workspace is yours to run.** Both are self-hostable with no account, no
  usage tier, and no hosted dependency.

They also pay the same price, and it is worth saying plainly because neither can
dodge it: **you are asking people to leave Slack.** Everything downstream of that —
notifications, search, mobile, the hundred small affordances a mature chat app has —
is yours to build, and both projects are visibly short of it. Buzz has search;
Banter does not. Neither has threading that would satisfy somebody used to Slack,
and Banter has none at all.

## The decisive difference: one relay, or a mesh

This is the fork in the road, and Buzz's `ARCHITECTURE.md` is admirably blunt about
which way it went. The relay is "the single source of truth. All reads and writes
flow through it. There is no peer-to-peer event exchange, no gossip, no
replication."

That is a real engineering position, not a limitation. One authoritative relay gives
you ordering, membership enforced in one place (access is checked *before* a
subscription registers, so there is no race window on a private channel), search
over a single Postgres index, and an operational story every sysadmin already knows.

Banter went the other way, and it shows up in what a client dials. A Banter server
can be a plain TCP socket, but the path it is built for is a **CupriNet site**: a
client opens a vessel, completes a Noise handshake against the site's published
identity, and rides a conduit. A browser gets there over a **WebRTC DataChannel**
negotiated from a link the node itself publishes, which is the part that removes the
signalling server — the page holds the node's remote description before it opens a
socket.

What that buys: no relay to operate, a browser that connects without a server in the
middle holding the session, and a path to reaching a node that is not on a
reachable address. What it costs is substantial and should be stated: a bespoke
network stack with its own vocabulary, a handshake that can fail in ways an HTTP
WebSocket cannot, and a browser client that has to carry a WebAssembly runtime to
speak it at all. Buzz's clients are "any Nostr app"; Banter's browser client is 19 MB
of wasm.

If you want one box with Postgres on it and a WebSocket URL to hand out, Buzz's shape
is the better-judged one.

## What a message is, and who can prove it

Buzz's unit is a **signed Nostr event**: a `kind` integer and a payload, signed by
the author's secp256k1 key, with `buzz-audit` keeping a hash chain over the lot.
Three consequences follow, and all three are advantages.

1. **A message is verifiable away from the server that stored it.** Anyone holding
   the event and the public key can check who wrote it. Banter's messages are not
   individually signed: they travel inside an authenticated, encrypted session, so
   the server knows who sent one, and a reader is trusting the server's word for it
   afterwards.
2. **Tampering is detectable rather than merely prohibited.** A hash chain makes an
   edited history show itself. Banter's history is rows in SQLite; an operator with
   file access can rewrite it and nothing will say so.
3. **"Which agent did this" survives the transcript.** For the thing both projects
   are actually for — agents doing work people must later account for — this is the
   stronger foundation, and it is the clearest respect in which Buzz is better
   designed for its own purpose.

Banter's cryptography is real but aimed elsewhere: at the **transport and the
identity of the node**, not at the provenance of each line. The CupriNet vessel is
Noise-encrypted and the site's identity is pinned by a Signet the client verifies,
so what Banter protects well is *the conversation in flight* and *what you are
talking to*. Agent keypairs exist (`AgentKeys.Sign`/`Verify`) but are used to prove
an agent at login, not to sign what it then says.

If an auditable record of agent actions is why you are reading this, Buzz has the
architecture and Banter has a to-do.

## How an agent attaches

Here the projects diverge on something more interesting than a protocol choice:
**where the agent's coordination lives.**

Buzz treats the agent as a subprocess to be harnessed. `buzz-acp` is a standalone
binary that "spawns AI agent subprocesses (1–32, default 1)", connects to the relay
over WebSocket with NIP-42 auth, and queues `@mention` events per channel. The
agent is Goose or Codex or Claude Code, unmodified, speaking the **Agent
Communication Protocol** over stdio. That is a good design and it is shipping;
Banter's equivalent, `Banter.Agents.Acp`, is a directory containing a `.csproj` and
nothing else, deferred in PLAN §8 as "Path C".

Banter instead expects an agent to be **a program that links its SDK** —
`Banter.Agents.Sdk`, with `LlmChatAgent` for anything speaking an OpenAI-compatible
endpoint. That is more work to adopt and narrower in reach: you write C#, or you
write nothing. What it buys is that the room, not the agent, decides several things
Buzz leaves to the agent:

- **A delegator is elected** per room, and dispatch modes decide whether work goes
  to one agent, the best match, or everyone.
- **Agents announce attributes** — what they can do, where they run — and the
  delegator routes on them. An admin can clamp what an agent is allowed to claim
  about itself, because the machine running an agent is not the authority on how
  much the room trusts it.
- **Sensitivity is a property of the room**, inherited by sub-rooms, and egress is
  announced: a room can know that answering here means data leaving the building.
- **Tools run server-side.** MCP tools execute on the server under per-agent grants,
  so what an agent may reach is an operator's decision in one place rather than each
  agent's own configuration.

Buzz has nothing in this shape, as far as its architecture document shows, and
Banter has nothing in the shape of a working ACP harness. Those are not the same
kind of gap: one is a feature nobody has built, the other is the standard way the
rest of the industry attaches an agent.

## "Voice" is two different features

Both project descriptions say voice, and a reader comparing feature lists would tick
the row for both. They do not overlap at all.

**Buzz has calls.** A huddle is a WebSocket endpoint that authenticates each
participant and forwards opaque Opus frames between peers, with no external SFU and
a soft cap around 25. Recording and per-track publishing are reserved and not yet
built. It never looks inside the audio; it is a conference bridge.

**Banter has speech.** `Banter.Voice` captures a microphone, detects voice activity,
segments utterances, and sends the result as an ordinary message — and reads incoming
messages back with TTS. Providers are an OpenAI-compatible endpoint, a Wyoming server
(Whisper/Piper), or local Whisper.net. There is no call: two people talking through
Banter are exchanging transcribed text, which is also exactly what makes it work in a
room an agent is reading.

Neither can do the other's job. A huddle cannot give an agent the transcript; Banter
cannot give two people a live conversation. And Banter's side carries a caveat Buzz's
does not: **it has never been tested against real hardware.** The engines, the
segmenters and the sessions are covered by 129 tests, all of them headless. Nothing
in this repository has met a microphone.

## Where Buzz is simply ahead

- **It ships ACP.** The standard way to attach a coding agent, working today.
- **Per-event signatures and a hash-chain audit log.** The right architecture for
  accountable agent work, and Banter does not have it.
- **Git hosting in the same identity system**, so a review and the conversation about
  it are the same kind of object.
- **A YAML workflow engine** and Workflow channels — declarative automation Banter
  has no answer to beyond the work ledger.
- **Search.** Banter has none; this is the single most conspicuous omission for
  anybody using a chat app in earnest.
- **Threading**, via Forum channels.
- **Any Nostr client can read it.** Building on an existing protocol means an
  ecosystem Banter's bespoke protocol cannot have.
- **A company's worth of hands**, and weekly releases to show for it.

## Where Banter is genuinely stronger

- **One language, end to end.** Protocol, server, agent SDK, three UI heads and voice
  are all C#. An agent author and a client author are the same person with the same
  toolchain, and the server's types are the client's types.
- **A browser client that is not a browser.** The web head is the same app class as
  the desktop one, compiled to WebAssembly ahead of time and painted on a canvas by
  Skia — no Electron, no JavaScript framework, no second implementation of the UI.
  Buzz's desktop app and web client are a web stack; Banter's three heads are one
  codebase.
- **Android.** A real APK running the same app. Buzz's mobile is unfinished.
- **It runs on nothing.** One process and a SQLite file, from a 2.9 MB zip if .NET is
  already there and a 33 MB self-contained one if it is not. Buzz wants Postgres and
  Redis before it will start.
- **Room-level sensitivity and announced egress**, inherited by sub-rooms — a model
  for "this conversation must not leave the building" that Buzz's channel kinds do
  not express.
- **Server-side tools under per-agent grants**, so an operator decides what an agent
  can reach in one place.
- **A work ledger whose leases outlive a connection**, built for agents that drop off
  and come back.
- **Transfers that verify themselves.** Every file crosses chunk-by-chunk against a
  manifest in both directions, on every transport, and resumes rather than restarting.
  Buzz has a 50 MB upload and an S3 bucket.
- **Headless-testable UI.** 615 tests cover the app layer with no display at all, a good
  share of them clicking and typing into the real interface rather than its view model —
  and 6 more drive a real headless browser over a real mesh, asking the server's own
  database whether the message arrived.

## Choosing

**Choose Buzz when** — and this is most of the time, today:

- You want an auditable record of what agents did, provable after the fact.
- The agents you want to use are Goose, Codex, Claude Code or anything else speaking
  ACP, and you are not going to write C# to adopt them.
- You want code hosting and chat under one identity.
- You want declarative workflow automation.
- You need search, threading, or a mobile client that exists.
- You want a project with a company behind it and an ecosystem around the protocol.
- You are happy to run Postgres and Redis.

**Choose Banter when:**

- You are a .NET shop and the agents are yours to write. One language across the
  server, the agents and every client is the whole argument, and it is a strong one
  if it applies to you.
- You want the room to do the routing — a delegator, announced attributes, dispatch
  modes — rather than each agent deciding when it has been addressed.
- "This conversation must not leave the building" needs to be a property of the room
  and its children, with egress announced.
- You want tool access to be an operator's decision, enforced server-side, not each
  agent's own config.
- Voice means *talking to the room and being read back to*, not a call.
- You want a browser client that is not an Electron app, and a phone client from the
  same code.
- Deployment has to be small: one file, one process, one SQLite database.

**Choose neither** if what you actually want is Slack with good bots. Both projects
are asking you to move, and Slack's integrations have got considerably better at
exactly the thing both of these were founded to fix.

## The honest summary

Buzz is what Banter would look like with a company behind it and a different answer
to one question — put everything through one signed, authoritative relay instead of
building a mesh. On the things both projects exist for, Buzz is ahead where it counts
most: signed events and a hash chain make agent work accountable, and a shipping ACP
harness means the agents people already use can join today. Banter's ACP directory is
empty and its history is unsigned rows in a database.

What Banter has that Buzz does not is a coherent single-language stack reaching
further than Buzz's does — desktop, phone and browser from one app class, with no
browser engine inside it — and a model of *the room as the thing with authority*:
sensitivity that children inherit, egress that is announced, a delegator that routes,
and tools that run under grants the operator set. Buzz gives the agent an identity and
gets out of the way. Banter gives the room an opinion.

If you are choosing a workspace for your team this quarter, choose Buzz. If you are
interested in the second idea — and you write C# — Banter is where it is being worked
out.
