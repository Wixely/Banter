# Banter — Architecture & Build Plan

A C#-only suite for managing multiple AI agents and conversing with them (and each other) over an
IRC-style room server, with first-class voice (TTS/STT) on mobile and desktop.

**Hard constraints**

- 100% C#/.NET. No Python, no Node. Anything non-C# (models, speech engines, agents) is reached
  over a protocol boundary (OpenAI-compatible HTTP, Wyoming TCP, ACP stdio/JSON-RPC, BanterProtocol).
- Transport between server ↔ clients/agents: [Wixely/CupriNet](https://github.com/Wixely/CupriNet)
  (managed P2P library, .NET 10, Noise-encrypted channels).
- Unified client where possible: one native CupriFace (`CupriApp`) codebase hosted on desktop
  (Windows/Linux/macOS), Android, and web (WASM). No MAUI. iOS deferred — the web client is the
  interim iPhone answer (decision revisited Phase 6).

---

## 1. System overview

```
                          ┌────────────────────────────┐
                          │        Banter.Server        │
                          │  (hub CupriNode, rooms,     │
                          │   users, history, auth)     │
                          └──────┬───────────┬─────────┘
              CupriNet channels  │           │  CupriNet channels
                 (BanterProtocol)│           │  (BanterProtocol)
        ┌────────────────────────┤           ├───────────────────────┐
        │                        │           │                       │
┌───────┴────────┐    ┌──────────┴─────┐  ┌──┴──────────────┐  ┌─────┴─────────┐
│  Banter.App    │    │  Banter.App    │  │ DaggerAgent      │  │ Custom agent  │
│  (desktop,     │    │  (mobile,      │  │ (banter mode via │  │ (links        │
│  global PTT)   │    │  always-listen)│  │ Banter.Agents.Sdk│  │ Banter.Agents │
└───────┬────────┘    └──────────┬─────┘  │ under Warden)    │  │ .Sdk directly)│
        │ HTTP/TCP               │        └──┬────────┬──────┘  └───────────────┘
        ▼                        ▼           │proc     │HTTP
  Speech providers         Speech providers  ▼         ▼
  (OpenAI-compat /         (same)     other agent   OpenAI-compat /
   Qwen / Wyoming)                    CLIs (Claude  Ollama model
                                      Code, …) +MCP endpoints
```

- **Banter.Server** is logically an IRC server: rooms, users, nicks, presence, ops. Physically it is
  the *hub node* of a CupriNet mesh — clients and agent hosts pair with it and open authenticated
  channels; all room traffic is routed through the server (star topology over a P2P library). We do
  **not** rely on peer-to-peer routing for chat semantics; CupriNet gives us encrypted, NAT-punching
  transport and identity (Sigils), the server gives us ordering, history, and authority.
- **Agents are just users.** Every agent connects (via an agent host) as a user with a nick, joins
  rooms, sends/receives messages. Moving an agent between rooms is a server op, identical to a human
  `/join`. Two agents in the same room can converse because the server relays room messages to all
  members — no special "agent-to-agent" pathway needed.
- **Voice is a client-side concern.** STT turns speech into ordinary chat messages before they hit
  the wire; TTS renders incoming messages to audio on the receiving client. The server stays
  text-only (plus optional voice-note attachments later). This keeps the server simple and lets any
  mix of voice/text clients share a room.

## 2. Solution layout

```
Banter.sln
├── src/
│   ├── Banter.Protocol/        # BanterProtocol: message contracts, serialization, framing
│   ├── Banter.Core/            # shared domain: rooms, users, permissions, history models
│   ├── Banter.Server/          # hub node, room engine, auth, persistence, admin API
│   ├── Banter.Client.Core/     # client runtime: connection, reconnect, state cache, eventing
│   ├── Banter.Voice/           # ITranscriptionEngine (Bantz.Speech.Abstractions) / ITextToSpeech + audio pipeline
│   │   ├── Banter.Voice.OpenAI/    # OpenAI-compatible /v1/audio/* (also covers Qwen/DashScope)
│   │   └── Banter.Voice.Wyoming/   # Wyoming TCP client (Whisper, Piper, openWakeWord)
│   ├── Banter.Agents.Sdk/      # library for writing BanterProtocol agents in C#
│   │                           #   (consumed by DaggerAgent's new "banter" mode — separate repo)
│   ├── Banter.Warden/          # agent supervisor: runs DaggerAgent instances + LLM-endpoint agents
│   │   └── Banter.Agents.Acp/  # ACP client (JSON-RPC over stdio) — deferred, see §8 Path C
│   ├── Banter.App/             # CupriFace CupriApp: shared views/view-models (Bantz-style split)
│   │   ├── Banter.App.Desktop/ #   host head: Win/Linux/macOS executable (tray, global PTT)
│   │   ├── Banter.App.Android/ #   host head: CupriActivity + foreground-service/audio glue
│   │   └── Banter.App.Web/     #   host head: WASM bundle served by Banter.Server (Phase 2.5)
│   └── Banter.Cli/             # console client + server admin tool (first working client)
└── tests/
    ├── Banter.Protocol.Tests/
    ├── Banter.Server.Tests/
    ├── Banter.Voice.Tests/
    └── Banter.Integration.Tests/   # in-proc server + fake clients/agents end-to-end
```

All projects target **.NET 10** (CupriNet and CupriFace require it; the Android head is plain
`net10.0-android`).

## 3. Transport: CupriNet

How Banter maps onto CupriNet's API:

- Server startup: `CupriNode.CreateAsync()` → publish its **mesh-magnet URI** (`IntoneUri()`), which
  doubles as the "server address" users paste or scan (QR) into clients.
- Client/agent connect: `ConjoinAsync(serverUri)` to pair, then `ConsecrateAsync(watchword)` to open
  the authenticated channel. The **watchword** acts as the server password / invite secret; per-user
  credentials ride inside BanterProtocol's `AUTH` message after the channel is up.
- One multiplexed channel per client carries all BanterProtocol frames (CupriNet does stream
  multiplexing over TCP with length-prefixed framing; we add one more framing layer only if the
  channel API is stream-oriented rather than message-oriented — decide during the spike).
- CupriNet extras we get for free and should keep enabled: Noise XX/IK end-to-end encryption, LAN
  discovery + NAT-PMP + UDP hole punching (lets a home server work without port forwarding),
  warm-start peer caches (fast reconnect on mobile).
- **Browser clients (introduced CupriNet 0.2.0; still shipped in current 0.3.4):** via the
  optional `CupriNet.WebRtc` binding
  (managed ICE/DTLS 1.3/SCTP through CupriWebRTC), a node accepts browser WebRTC DataChannel
  peers **with no signalling server** — the server's Intonation URI carries its static WebRTC
  endpoint parameters, and Noise + Consecration run unchanged over the DataChannel, so a browser
  authenticates identically to a native peer. Banter.Server enables this binding; it is the
  transport for the web client (CUPRIFACE-PLAN.md §3). Packages ship from the Wixely GitHub
  Packages feed (`read:packages` PAT).
- **CupriNet 0.3.x extras (current v0.3.4, 2026-08-23) — noted, not adopted:** an optional
  managed **Tor** transport (`CupriNet.Tor`: dual-stack clearnet+onion, or onion-only hiding
  the server IP) if a privacy-preserving deployment is ever wanted; and **Shrines** — content
  served over L2 at a self-authenticating `cupri1…` address, with **named live feeds** pushed
  over the same connection (no polling/WebSocket). Shrines are a candidate for serving the web
  client bundle and for live status fan-out — revisit at Phase 2.5; the Kestrel static-file
  plan stands until then.

**Risk (accepted, mitigated):** CupriNet is pre-1.0 and its crypto is unaudited. Mitigation:
`Banter.Client.Core` and `Banter.Server` talk to transport through an `IBanterTransport`
abstraction with a plain TCP/TLS (or WebSocket) fallback implementation, so a CupriNet breaking
change or blocker never stalls the suite. **First task of Phase 1 is a CupriNet spike** proving:
pair → channel → bidirectional messages → reconnect, on Windows + Android.

## 4. BanterProtocol

Our own wire protocol, versioned from day one. Shared contracts live in `Banter.Protocol` and are
used verbatim by server, clients, and the agent SDK.

- **Encoding:** MessagePack (`MessagePack-CSharp`) inside length-prefixed frames. Compact, fast,
  fully managed, and fine for the small binary blobs we may carry (avatars, voice notes). JSON
  debug mode via a switch for troubleshooting.
- **Envelope:** `{ ver, type, msgId, replyTo?, payload }` — request/response correlation via
  `msgId`/`replyTo`, server pushes have no `replyTo`.
- **Message types (v1):**
  - Session: `HELLO` (carries the CupriMark negotiation payload, see below), `AUTH` / `AUTH_OK` /
    `AUTH_FAIL`, `PING`/`PONG`, `BYE`
  - Presence & rooms (IRC-shaped): `NICK`, `JOIN`, `PART`, `ROOM_LIST`, `ROOM_MEMBERS`, `TOPIC`,
    `KICK`, `MODE` (op/voice flags), `WHOIS`
  - Chat: `MSG` (room), `PRIVMSG` (user↔user), `TYPING`, `HISTORY_REQ`/`HISTORY_CHUNK`,
    `EDIT`/`DELETE` (nice-to-have, schema reserved). `MSG` payloads may carry file references
    (attachment = a stored file id, see Files) which clients render inline.
  - Files (room-scoped storage, §5a): `FILE_PUT_START`/`FILE_PUT_CHUNK`/`FILE_PUT_END` (chunked
    upload), `FILE_GET`/`FILE_CHUNK` (download), `FILE_LIST` (per room), `FILE_INFO`,
    `FILE_GRANT`/`FILE_REVOKE` (assign/remove rooms), `FILE_DELETE`
  - Agent control (server-op only): `AGENT_LIST`, `AGENT_MOVE` (force join/part a room),
    `AGENT_PAUSE`/`AGENT_RESUME`, `AGENT_STATUS` (busy/idle/thinking — surfaced in the UI)
  - Work ledger (§8b): `TASK_POST`, `TASK_CLAIM`, `TASK_ASSIGN`, `TASK_RELEASE`, `TASK_UPDATE`,
    `TASK_DONE`/`TASK_FAIL`, `TASK_LIST`
  - Streaming: `MSG_STREAM_START` / `MSG_STREAM_DELTA` / `MSG_STREAM_END` so agent token streams
    render live in clients instead of arriving as one block
- **Identity:** users authenticate with username + password/token (server-side store); the CupriNet
  Sigil is additionally pinned to the account after first auth, giving device-level trust. Agents
  authenticate with agent tokens minted by the server admin; agent accounts carry an
  `IsAgent` flag plus metadata (model, owner, capabilities) that clients render distinctly.

### Versioning & capability negotiation — CupriMark

We use [Wixely/CupriMark](https://github.com/Wixely/CupriMark) (signed, versioned
capability-negotiation library; range negotiation instead of hard `!= version` cutovers) as
BanterProtocol's versioning mechanism, instead of hand-rolling one in `HELLO`. Why it fits:

- **The fleet upgrades unevenly by nature.** Mobile apps lag behind store review, DaggerAgent
  instances and the server deploy independently, and the CLI is whatever was last built. Range
  negotiation gives staged migration windows instead of flag days — exactly the problem CupriMark
  exists to solve, and Banter has it worse than most projects.
- **Already in the family.** CupriNet uses CupriMark for its own layer negotiation, so the library
  ships with our transport anyway; using it at the app layer means one negotiation model and one
  toolchain (`cuprimark build/inspect/negotiate`) across the whole stack. .NET 6+ target, fine on
  .NET 10.
- **Layered catalogues map to our feature areas.** CupriMark supports independently versioned
  layers; we define one catalogue per protocol area — `banter.core` (session/rooms/chat),
  `banter.files` (§5a), `banter.agent` (agent control), `banter.stream` — so a lightweight client
  can legitimately speak core + stream while skipping agent-control verbs, and each area can
  evolve without bumping the world. This doubles as our *capability* mechanism (what a peer
  supports), not just versioning (which revision it speaks).
- **Cheap and safe on the wire.** Only ordinals travel (contiguous ranges ≈ 2 bytes) inside our
  `HELLO`; meanings resolve locally against signed catalogues. `TranscriptBinding` lets us mix the
  negotiation into the auth transcript so a MITM can't force a downgrade to an older, weaker
  protocol revision. Ed25519 catalogue signing (CupriCurve) with our release key means clients
  reject tampered catalogues.
- **The lockfile gate is CI-grade protocol discipline.** CupriMark's MSBuild task freezes
  published catalogue entries; an accidental breaking change to a shipped message shape fails the
  build instead of failing in the field. That enforces the "versioned from day one" intent
  mechanically.

Adoption plan: single `banter.core` catalogue with loose ranges during Phases 0–2 (protocol still
churning pre-release — keep ceremony minimal, don't sign until the first real release); split into
per-area catalogues and turn on signing + lockfile gates when Phase 5 makes third-party agents via
`Banter.Agents.Sdk` a reality. The `ver` field in the envelope shrinks to the negotiated ordinal.

## 5. Server (`Banter.Server`)

- Hosted as a .NET Generic Host worker (runs as console, Windows service, or systemd unit).
- **Room engine:** in-memory authoritative state (rooms, membership, modes), single writer per room
  (`System.Threading.Channels` actor-ish loop) so ordering is deterministic. Fan-out to member
  connections.
- **Persistence:** Dapper over ADO (no ORM) with a hand-rolled migration manifest (ordered
  per-dialect SQL migrations recorded in a `schema_manifest` table, applied transactionally at
  startup). **SQLite is the default** (zero-setup, fully managed provider); **hosted PostgreSQL
  is a first-class option** via Npgsql — same stores and manifest, per-provider SQL only where
  dialects differ. Covers accounts (PBKDF2-hashed credentials), room definitions/topics, and
  message history. History replay via `HISTORY_REQ` with cursor paging.
- **Rules for agent-filled rooms:** per-room throttle (max agent messages/minute) and a
  turn-taking guard (an agent isn't re-prompted by its own output; loop-breaker if two agents
  ping-pong beyond N exchanges without human input) — configurable per room. This is essential or
  two chatty agents will run away with your token bill.
- **Admin surface:** everything an op needs is in-protocol (`MODE`, `AGENT_MOVE`, …) so `Banter.Cli`
  doubles as the admin tool; no separate web dashboard in v1.

### 5a. Room-scoped storage

Server-side file store for anything users and agents want to share or keep — media, audio, text,
data blobs. Small files only, not repos; the durable "Banter memory" for a room.

- **Model:** a file is a first-class object (id, name, MIME type, size, SHA-256, uploader,
  created, optional description/tags) with a many-to-many **file ↔ room grant** table. A file is
  visible and downloadable to anyone — human or agent — who is currently a member of at least one
  granted room. Grants are assigned at upload and editable afterwards (`FILE_GRANT`/`FILE_REVOKE`)
  by the uploader or a room op; same for `FILE_DELETE`.
- **Storage:** blobs on disk under the server's data dir, named by content hash (automatic dedup —
  granting the same file to five rooms stores it once); metadata + grants in the existing SQLite
  db. No blob bytes in SQLite.
- **Limits ("small files, not repos"):** per-file size cap (default 32 MB) and per-room quota
  (default 1 GB), both configurable; oversize `FILE_PUT_START` is rejected up front, not after
  upload. Files are permanent by default (they survive independent of message history); an
  optional TTL per file exists for scratch shares.
- **Transfer:** chunked over the same BanterProtocol channel (~64 KB chunks, resumable via
  offset) — no second port or side-channel HTTP needed, and CupriNet's encryption covers it.
- **Chat integration:** uploading with a target room emits a `MSG` carrying the file reference,
  so shares appear in the timeline; clients render images/audio inline (audio playback = voice
  notes for free). Files can also be uploaded "quietly" (grant only, no message) for reference
  material.
- **Agent integration:** `Banter.Agents.Sdk` gets `ListFilesAsync(room)` / `GetFileAsync` /
  `PutFileAsync` helpers, and — the main path for DaggerAgent — the server exposes a tiny
  built-in **Banter storage MCP server** (registered in MCPHub like any other) with
  `banter_file_list` / `banter_file_get` / `banter_file_put` tools, scoped automatically to the
  calling tenant's room memberships. An agent's durable memory is then literally "files in the
  rooms it can see", readable through the tooling it already has.

## 6. Voice (`Banter.Voice`)

Provider-agnostic interfaces, three backends, all consumed over the network so no ML runs in-proc:

| Interface | OpenAI-compatible | Qwen | Wyoming |
|---|---|---|---|
| `ITranscriptionEngine` (from `Bantz.Speech.Abstractions`) | `POST /v1/audio/transcriptions` (batch) and Realtime WS (streaming) | DashScope's OpenAI-compatible endpoints — same adapter, different base URL/key | `transcribe` / audio-chunk / `transcript` events over TCP (e.g. faster-whisper) |
| `ITextToSpeech` | `POST /v1/audio/speech`, streamed response | qwen-tts via the same compatible surface | `synthesize` → PCM audio events (e.g. Piper) |
| `IWakeWord` (optional) | — | — | openWakeWord service |

Because Qwen (DashScope) exposes an OpenAI-compatible surface, one well-built OpenAI adapter with
configurable base URL + model name covers OpenAI, Qwen, and every local OpenAI-compatible speech
server (Speaches, LocalAI, etc.). Wyoming is a separate small client: TCP, JSON-line events with
raw PCM payloads — trivially implementable in pure C#.

### 6a. Local STT from Bantz (Whisper.net) + shared packages

[Wixely/Bantz](https://github.com/Wixely/Bantz) — our hold-to-talk dictation app, and the first
published CupriFace application — already ships the STT stack Banter wants: **Whisper.net 1.9.1**
(wrapping MIT whisper.cpp), local `base.en` model (~142 MiB download), Vulkan GPU or CPU
inference, audio never leaving the machine, behind a replaceable `ITranscriptionEngine`
interface. It also ships the best version of our desktop PTT plan: global bindings for keyboard
chords, **XInput gamepad buttons**, and mouse buttons, plus tray behavior, on Windows (Linux
experimental).

**Done — shipped in Bantz v0.2.3 (2026-08-22).** Four packages are published on the Wixely
GitHub Packages feed (same feed as CupriNet/CupriFace), restorable with a `read:packages` PAT;
Bantz dogfoods them via `ProjectReference` and its `samples/MinimalDictation` consumes the
published packages. Extraction plan (executed; canonical copy lives in the Bantz repo):
[BANTZ-SPLIT-PLAN.md](https://github.com/Wixely/Bantz/blob/main/BANTZ-SPLIT-PLAN.md).
The seams as actually shipped:

- `Bantz.Speech.Abstractions` — `ITranscriptionEngine` + `PcmAudio` (16 kHz mono s16),
  `InitializeAsync` with download-progress reporting, dependency-free (CI-tested). This is the
  shared STT contract: Banter's OpenAI-compatible/Qwen/Wyoming adapters implement the same
  interface, so Banter gains local Whisper and **Bantz gains remote STT engines for free**.
  (`Banter.Voice`'s planned `ISpeechToText` merges into this — one contract, not two.)
- `Bantz.Speech.Whisper` — the Whisper.net engine: SHA-256-verified model download (base.en;
  other models by pre-placing the file at a custom model path), Vulkan/CPU selection,
  concurrent-first-run install lock, overridable model/runtime directories (Banter points these
  at its own data dir). Note: runtime (Vulkan vs CPU) choice is process-global — Whisper.net
  limitation.
- `Bantz.Capture` — mic capture (Windows NAudio; Linux ALSA) → 16 kHz mono PCM, push-stream
  frames + record-until-release convenience, device-id seam. Also hosts `AudioSignalAnalyzer`
  (silence/min-activity detection) — the accidental-press filter moved *here* as an opt-in
  caller-side check rather than living inside the Whisper engine, so always-listening mode
  simply doesn't invoke it.
- `Bantz.Input` — global bindings (keyboard/XInput/mouse chords, hold + toggle semantics,
  honest capability query on non-Windows). This *replaces* Banter's planned hand-rolled
  `RegisterHotKey` work and upgrades it: PTT from a gamepad button was not in our plan and is
  now free. **Deviation from plan:** the tray controller was *not* extracted (stayed in
  Bantz.Windows), so Banter's desktop client owns its own tray handling (§7).
- Stays in Bantz (Banter doesn't need it): text injection (`SendInput`/wtype/xdotool), countdown
  UX, tray.

Provider matrix update: **local Whisper.net is the default STT on desktop** (private, no
endpoint dependency); remote providers (OpenAI-compat/Qwen/Wyoming) remain the default on
Android/web where a 142 MiB model + native inference is unattractive, and stay available on
desktop by configuration. Constraint note: whisper.cpp is a native binary under a C# wrapper —
the same "no Python/Node, protocols preferred" bend as Silero VAD, already accepted in practice
the day Bantz shipped it; it is opt-in per platform, never required (remote engines always work).

The "hotkey → speak → main channel" flow (the point of all this): global binding (Bantz.Input)
→ capture (Bantz.Capture) → transcribe (Bantz.Speech.Whisper, local) → `MSG` to the user's
configured **home room** (default `#main`) — from anywhere on the desktop, Banter app focused or
not, so every agent in the room sees it. Optional review-before-send stays per PLAN §6 capture
modes. A natural follow-up (not planned yet): Bantz itself gains a "send to Banter room" output
target next to text-injection, making Bantz a zero-UI Banter voice companion.

**Client audio pipeline** (in `Banter.Voice`, consumed by the CupriFace app and CLI):

```
mic → capture (16 kHz mono PCM) → ring buffer → VAD segmenter → ITranscriptionEngine → draft text → send MSG
incoming MSG (from TTS-enabled sender/room) → ITextToSpeech → playback queue (per-room, per-voice)
```

- **VAD:** start with a managed energy/zero-crossing gate (pure C#, good enough for PTT trimming and
  basic always-listening) — `Bantz.Capture`'s `AudioSignalAnalyzer` (RMS/peak/active-duration per
  frame) is the building block, already shipped. Optionally upgrade to Silero VAD via ONNX Runtime — note that pulls a
  native binary through a NuGet; it honors "no Python/Node" but bends "entirely C#", so it's an
  opt-in flag, not a dependency of the core. Wyoming openWakeWord is the fully-external alternative.
- **Capture modes** (all platforms, shared state machine in `Banter.Voice`):
  1. **Push-to-talk:** press → capture → release → VAD-trim → transcribe → show draft → auto-send
     (configurable: review-before-send vs. send-immediately).
  2. **Always listening:** continuous capture, VAD segments utterances, each segment transcribed and
     sent (optionally gated behind a wake word). Visible "listening" indicator + hard mute switch.
  3. **Desktop global PTT:** hotkey works when the app is unfocused (see §7).
- **TTS policy:** per-room setting on the client — off / agents-only / everyone; per-agent voice
  assignment so multi-agent rooms are distinguishable by ear. Streamed agent messages are spoken
  sentence-by-sentence as deltas complete, not after `MSG_STREAM_END`.

## 7. Client app (`Banter.App`, native CupriFace)

**Decided 2026-08-24: native CupriFace** — [CUPRIFACE-PLAN.md](CUPRIFACE-PLAN.md) Option A. One
`CupriApp` codebase (HTML/CSS to GPU canvas, C# binding) covers desktop (Win/Linux/macOS),
Android, and web (WASM). **MAUI is out of the plan entirely. iOS is deferred** — CupriFace has
no iOS host yet; the web client is the interim iPhone answer, revisited at Phase 6.
**[Bantz](https://github.com/Wixely/Bantz) is the working reference** for how a CupriFace app is
built: a portable UI project consuming the `CupriFace` NuGet package (Bantz.App — embedded
HTML/CSS assets, view-models) plus thin per-OS host heads (Bantz.Windows / Bantz.Linux
`Program.cs`), published as self-contained executables. Banter mirrors that shape with desktop,
Android, and web heads. The Phase 0 spikes (scrollback perf, composer/IME feel, streaming render
rate, WASM round-trip, Android endurance — CUPRIFACE-PLAN §5) remain as validation, with the
fallback ladder: Android spikes fail → MAUI for mobile; fundamentals fail → MAUI everywhere.

Shared UI (in the CupriApp): room list, channel view (streaming message rendering — agent
markdown renders via Markdig → HTML natively, agent status badges), member pane, voice controls,
settings (server URI/watchword, speech providers, hotkeys). Native window chrome, tray controls,
and page zoom are CupriFace framework features (v0.2.x). CupriFace is at **v0.2.12**
(2026-08-24); the 0.2.12 fixes land squarely on chat-layout CSS (`repeat(auto-fill/auto-fit)`
grids, percentage heights inside fixed-height blocks, `:root` custom-property inheritance,
keyframed width/height, `transform-origin`), which de-risks the Phase 0 layout spikes. Bantz
currently pins 0.2.11.

Platform-specific pieces (live in the host heads):

- **Mic capture:** `Bantz.Capture` (WASAPI Windows / ALSA Linux) on desktop; AudioRecord in the
  Android head; WebAudio JS interop on web (voice deferred to web v1.1); AVAudioEngine on macOS
  (Phase 6). All normalize to 16 kHz mono PCM behind the same abstraction.
- **Android always-listening:** foreground service with `microphone` service type + persistent
  notification; battery-exemption prompt; works with screen off — plain `net10.0-android` code
  in the Android head.
- **Desktop global PTT:** `Bantz.Input` global bindings (keyboard chords, XInput gamepad, mouse
  buttons; hold + toggle) — default e.g. `Ctrl+Alt+Space` hold-to-talk; tray icon with
  mute/listening state via CupriFace's built-in tray support.
- **Web:** no global hotkeys (browser sandbox) — on-page PTT button + in-page keybind; ships
  text-first.
- **macOS global hotkey:** CGEvent tap (requires Accessibility permission) — Phase 6, best-effort.

`Banter.Cli` ships first and stays alive as the smoke-test client and server admin console.

## 8. Agent integration

Three paths, one outcome: an agent is a Banter user. Ordered by priority:

**Path A — DaggerAgent (primary):**
[Wixely/DaggerAgent](https://github.com/Wixely/DaggerAgent) is our own pure-C# .NET 10 agent:
OpenAI-compatible or Ollama backends, REPL / one-shot CLI / Kestrel HTTP modes, MCP tool
integration, process tools, and `spawn_subagent` with isolated context and budgets. Because we
fully control it, we modify it rather than bridge to it:

- **Add a `banter` mode to DaggerAgent** — a new host mode (alongside REPL/CLI/HTTP) that
  references `Banter.Agents.Sdk`, connects to the server with an agent token, joins configured
  rooms, and maps: incoming room prompts → agent turns; streamed model output → `MSG_STREAM_*`;
  tool-call activity → `AGENT_STATUS` (so the room sees "running tool: X"). Config follows its
  existing layering (`appsettings.json` → `DAGGER_*` env → CLI args).
- **Per-room context:** one DaggerAgent conversation per (agent instance, room); `AGENT_MOVE`
  parks/resumes conversations. Multiple personas = multiple DaggerAgent instances (or one instance
  with per-room system prompts), each its own Banter user.
- **It calls other agent CLIs for us:** DaggerAgent's process tools + `spawn_subagent` mean it can
  drive Claude Code, Gemini CLI, etc. as child processes with their own budgets. That likely makes
  DaggerAgent **the only agent Banter needs** — external agents become tools *inside* it rather
  than first-class Banter users, which also sidesteps the room-throttling problem for sub-agents
  (the parent's budget bounds them).
- Its MCP client support means any MCP server we stand up is immediately available to every room
  it sits in.

**Path B — BanterProtocol agents (`Banter.Agents.Sdk`):**
The SDK Path A is built on, published for any C# author:

- `BanterAgent` base class: connect/auth/join, message events with room context, streaming send
  helper, status reporting (`AGENT_STATUS`).
- A built-in generic `LlmChatAgent`: system prompt + endpoint config + a rolling per-room context
  window → instant lightweight "personality in a room" with zero code, no DaggerAgent instance
  needed. Warden can host N of these from config.

**Path C — ACP bridge (`Banter.Agents.Acp`, optional/deferred):**
[ACP](https://agentclientprotocol.com/get-started/introduction) is JSON-RPC 2.0 over stdio;
Claude Code, Gemini CLI and others speak it. A Warden-hosted bridge would spawn the process, run
`initialize` → `session/new`, map room messages → `session/prompt` and `session/update`
notifications → `MSG_STREAM_*`, and surface permission requests to room ops. **Build only if**
driving those CLIs through DaggerAgent's process tools proves too lossy (no streaming, no
permission prompts) and we want them as first-class room users. Keep it in the solution layout as
a placeholder; implement in Phase 6 at the earliest.

**`Banter.Warden`** is the supervisor daemon for whatever agents run: starts DaggerAgent
instances and `LlmChatAgent`s from config, restarts with backoff, applies per-room throttles
cooperatively, reports fleet status to the server. If we end up running exactly one DaggerAgent
service (it already deploys as a Windows Service/Docker), Warden can shrink to a thin config
+ supervision layer — don't over-build it.

### MCP access for agents — MCPHub

[Wixely/MCPHub](https://github.com/Wixely/MCPHub) (our own, C#/.NET) is a desktop app that
delivers/manages/proxies MCP endpoints to agents — it aggregates multiple MCP servers behind one
endpoint with namespaced tools (`azdo_*`, `gh_*`, …). Rather than each DaggerAgent instance
carrying its own MCP server list, agents get **one MCP endpoint: MCPHub**, co-located with the
Banter server. Modifications to MCPHub (we control the repo):

1. **Embeddable packages (plan handed to the MCPHub repo:
   [MCPHUB-SPLIT-PLAN.md](https://github.com/Wixely/MCPHub/blob/main/MCPHUB-SPLIT-PLAN.md)).**
   MCPHub's layering already separates proxy core (`MCPHub.Proxy`: upstream registry +
   namespaced aggregated catalog), in-proc Kestrel host (`ProxyHost`), and process supervision
   from the desktop shell. Those ship as NuGet packages (`MCPHub.Proxy`, `MCPHub.Hosting`,
   `MCPHub.Processes`) on the Wixely feed, and Banter **embeds the aggregated `/mcp` endpoint
   in-process** (in `Banter.Server` or Warden) rather than running a separate service; the
   desktop app remains as a management front-end and dogfoods the same packages. The split
   plan also adds the tenancy seam (per-tenant tool visibility/authorization + audit sink with
   args-digest-only rule) that items 2–4 below build on.
2. **Multi-tenancy.** Today it is single-user desktop-shaped. Add tenant identities where a
   *tenant = a Banter agent account*:
   - Each agent's MCPHub token is minted by the Banter server when the agent account is created
     (or by Warden at launch) and injected into the agent's config — agents never share tokens.
   - Per-tenant **grants**: which MCP servers (and optionally which individual tools) a tenant
     can see. `tools/list` is filtered per tenant, and calls to ungranted tools are rejected at
     the proxy — the agent literally cannot discover what it isn't allowed to use.
   - Per-tenant **secret isolation**: downstream server credentials (PATs, connection strings)
     belong to the hub, not the tenant; a tenant can be granted a server without ever seeing its
     credentials. Where downstream identity should differ per tenant (e.g. different SQL logins),
     grants carry a credential-set reference.
   - Per-tenant **audit log**: every tool call recorded as (tenant, tool, args-digest, result
     status, timestamp) — this is the answer to "what did that agent just touch?".
3. **Gating managed from Banter.** Admin surface for grants lives in BanterProtocol so ops manage
   it from `Banter.Cli` like everything else: `AGENT_MCP_GRANTS` (list/set per agent). The server
   pushes grant changes to MCPHub over a small admin API (HTTP + shared secret on localhost);
   MCPHub stays the enforcement point.
4. **Optional room visibility:** MCPHub call events can feed `AGENT_STATUS` ("querying Azure
   DevOps…") so rooms see what an agent is doing mid-turn.

DaggerAgent already has MCP client support, so the agent side needs nothing beyond pointing at
MCPHub's endpoint with its tenant token. Scope note: multi-tenant MCPHub is a real piece of work —
if Phase 5 needs MCP sooner, the interim is per-agent MCPHub grant *profiles* (static config, one
listener per agent) with the full tenant/token model following in Phase 6.

### 8b. Work: delegation & claiming

Chat alone makes agents talk; a **work ledger** makes them accountable. Tasks are first-class,
room-scoped server objects (like files, §5a) so "the main channel" doubles as a job board.

- **Task object:** id, title, body (may reference stored files), poster, room, state
  (`open → claimed/assigned → done | failed | released`), assignee, created/claimed/finished
  timestamps, optional deadline. Persisted in SQLite; every state change is announced into the
  task's room as a system message, so the timeline *is* the audit trail.
- **Two intake modes, both supported:**
  1. **Claiming (marketplace):** anyone posts `TASK_POST` into a room; any agent in the room may
     `TASK_CLAIM`. The server is the arbiter — first claim wins atomically (single-writer room
     engine already guarantees ordering), losers get a clean rejection instead of duplicate work.
  2. **Delegation (dispatcher):** a room can be set to *delegated* mode, naming a **delegator** —
     normally a DaggerAgent instance whose job is routing, not doing. It watches the room, and on
     `TASK_POST` (or on plain conversation it decides is a work request — it may itself
     `TASK_POST` to formalize) issues `TASK_ASSIGN` to the best-fit agent. Its routing knowledge
     is exactly what the protocol already exposes: `AGENT_LIST` metadata (capabilities/skills
     tags, model, owner) + live `AGENT_STATUS` (busy/idle) + its own memory of past task outcomes
     (room files, §5a). `TASK_ASSIGN` is permission-gated: delegators are granted per-room, ops
     always have it.
- **Leases, not black holes:** a claim/assignment carries a lease (default 30 min, per-task
  override). No `TASK_UPDATE`/`TASK_DONE` before expiry → server auto-`TASK_RELEASE`s back to
  `open` and announces it, so a crashed agent can't sit on work forever. Per-agent concurrent
  task cap (default 1) keeps greedy agents from hoarding claims.
- **Voice loop closed:** human hits the global PTT (§6a) → speaks → `MSG` lands in `#main` → the
  delegator formalizes it into a task and assigns, or agents claim → progress and completion
  stream back as room messages (and TTS reads them out to whoever's listening). Nothing about
  work items requires new UI to start — `TASK_*` are ops on the existing room surface; the
  clients add a task-list pane later.
- **DaggerAgent fit:** claiming/asking-for-status are `Banter.Agents.Sdk` calls surfaced to the
  model as tools (`banter_task_claim`, `banter_task_done`, …) alongside its MCP tools; the
  delegator is just a DaggerAgent instance with a routing system prompt and `TASK_ASSIGN`
  permission — no new runtime.

## 9. Build order & milestones

**Phase 0 — Scaffold + spikes (short):**
solution layout, CI (`dotnet build`/`test`), `Banter.Protocol` v1 contracts + serialization tests
+ initial CupriMark `banter.core` catalogue (unsigned, loose ranges);
**CupriNet spike** (pair/channel/reconnect Windows↔Windows and Android↔Windows); **CupriFace
spikes** (CUPRIFACE-PLAN §5: 10k-message scrollback perf and composer/IME feel on desktop + a
real Android device, streaming-delta render rate, WASM host round-trip, Android endurance);
**DaggerAgent spike** (run it against an OpenAI-compatible endpoint, exercise `spawn_subagent`
and driving one external agent CLI through its process tools — confirms Path C/ACP can stay
deferred).
*Exit: green CI; go/no-go on CupriNet as primary transport (fallback transport decision);
CupriFace spikes green or fallback ladder invoked (§7).*

**Phase 1 — Text chat works:**
`Banter.Server` (rooms, auth, history, SQLite) + `Banter.Client.Core` + `Banter.Cli`. Two CLI
clients on different machines chat in a room through the server.
*Exit: IRC-style multi-user, multi-room chat with history replay and reconnect.*

**Phase 2 — CupriFace app (text) + storage:**
`Banter.App` (one CupriApp) with desktop (Windows + Linux) and Android heads: room UI, streaming
message rendering, settings, QR/mesh-magnet server join. Server-side room-scoped storage (§5a):
`FILE_*` verbs, grants, quotas; upload/download from CLI and app, inline image rendering.
**Phase 2.5 — web head:** the same CupriApp as WASM served from Banter.Server (text-first;
CupriNet.WebRtc DataChannel, WebSocket fallback).
*Exit: phone and desktop app in the same room as CLI users; a file uploaded from one client is
listed and fetched from another via room grant; browser client joins the same room text-only.*

**Phase 3 — Voice, PTT:**
~~Bantz package extraction first~~ **done** — `Bantz.Speech.Abstractions`/`Bantz.Speech.Whisper`/
`Bantz.Capture`/`Bantz.Input` published at v0.2.3 on the Wixely feed; Bantz consumes them (§6a).
Phase 3 starts directly on `Banter.Voice` against the shared `ITranscriptionEngine` contract:
local Whisper.net (desktop default) + OpenAI-compatible and Wyoming adapters; capture/playback
per platform; on-screen PTT everywhere; TTS readout with per-sender voices.
*Exit: full round-trip — speak on phone → text in room → spoken aloud on desktop.*

**Phase 4 — Hands-free & hotkeys:**
Always-listening (Android foreground service; desktop), VAD segmentation, optional wake word via
Wyoming; desktop global PTT via `Bantz.Input` (keyboard chords, XInput gamepad, mouse buttons)
with tray state via CupriFace's built-in tray support; web voice (WebAudio capture backend)
best-effort.
*Exit: "always listening" phone on a desk works as a room microphone; desktop hotkey-from-any-app
→ speech lands in `#main` for every agent in the room.*

**Phase 5 — Agents:**
`Banter.Agents.Sdk` + generic `LlmChatAgent`; **DaggerAgent `banter` mode** (modify the
DaggerAgent repo to consume the SDK); `Banter.Warden` supervision; server-side agent controls
(`AGENT_MOVE`, throttles, loop-breaker) + client UI for agent status. ACP bridge only if the
Phase 0 spike showed DaggerAgent-driving-CLIs is insufficient. MCP via MCPHub in interim form:
headless mode + static per-agent grant profiles (full multi-tenancy lands in Phase 6). Agent
storage access: SDK file helpers + the Banter storage MCP server (§5a) registered in MCPHub.
**Work ledger (§8b):** `TASK_*` verbs, leases, claim arbitration, task tools in the SDK, and a
delegator DaggerAgent routing in one room.
*Exit: two DaggerAgent users + a human in one room holding a spoken three-way conversation; op
moves an agent to another room mid-session; an agent completes a task by driving an external
agent CLI as a subprocess; spoken request into `#main` → delegator assigns → agent claims-runs-
reports done, all visible in the timeline.*

**Phase 6 — Hardening & polish:**
macOS desktop head (Apple Silicon) + CGEvent-tap hotkey; **iOS decision point** (wait for
CupriFace iOS vs thin native app vs web-only continues), reconnection edge cases, message
edit/delete, voice-note attachments,
**multi-tenant MCPHub** (tenant tokens, per-tenant grants/secrets/audit, `AGENT_MCP_GRANTS`
admin flow), packaging (MSIX, APK, service installers), docs.

## 10. Key risks & mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| CupriNet pre-1.0 / unaudited crypto / API churn | Transport breakage | `IBanterTransport` abstraction + TCP/TLS fallback impl; Phase 0 spike gates the decision |
| CupriNet on Android (.NET 10 AOT, background sockets) | App instability | Spike on-device early; fallback transport is mobile-safe |
| CupriFace chat table-stakes unproven (virtualized scrollback, composer/IME feel) | Client UX unusable at scale | Phase 0 spikes on desktop + real Android device; fallback ladder: Android fails → MAUI mobile, fundamentals fail → MAUI everywhere |
| Single-stack concentration on pre-1.0 CupriFace (which we maintain) | UI bugs block the client | Accepted — our repo; issues found are fixes to our own product; Bantz ships on it in production today |
| Browser-side CupriNet DataChannel stack unproven | Web client slips | Server side documented (CupriNet 0.2.0); `IBanterTransport` WebSocket fallback; web is Phase 2.5, not v1-critical |
| External agent CLIs driven via DaggerAgent process tools lose streaming/interactivity | Degraded UX for wrapped CLIs | Acceptable for v1 (they're tools, not room users); ACP bridge (Path C) is the escape hatch if it matters |
| MCPHub is desktop-shaped; multi-tenancy (tokens, grant filtering, secret isolation) is a substantial refactor | MCP gating slips | Interim: headless mode + static per-agent grant profiles in Phase 5; full tenant model deferred to Phase 6; agents without MCP still fully functional |
| No iOS app in v1 (CupriFace has no iOS host) | iPhone users limited to web client (Safari, text + tap-to-talk best-effort) | Deferred by decision; Phase 6 revisits (CupriFace iOS / thin native app / web-only); nothing in `Banter.Client.Core`/`Banter.Voice` changes either way |
| Agent feedback loops in shared rooms | Runaway token spend | Server-side throttles + loop-breaker from day one (Phase 5, non-optional) |
| "Entirely C#" vs Silero VAD (native ONNX runtime) | Constraint bend | Managed energy VAD default; Silero opt-in; Wyoming wake word as external alternative |
| Whisper.net wraps native whisper.cpp; 142 MiB model | Constraint bend + heavy on mobile/web | Same bend already shipped in Bantz; desktop-default only, remote engines remain default on Android/web and always available everywhere |
| ~~Bantz package extraction stalls Phase 3~~ **Resolved** — packages shipped in Bantz v0.2.3 (2026-08-22) | — | Extraction complete; remaining Bantz-side item is the identical-behavior manual pass of the v0.2.3 app release |
| Streaming STT latency (batch transcription feels slow) | Voice UX | PTT uses batch (fine); always-listening prefers Realtime WS / Wyoming streaming where configured |

## 11. Open questions (defaults chosen, flag if wrong)

1. **Federation:** v1 assumes a single hub server. CupriNet could enable server-to-server meshing
   later; schema reserves a `origin` field on messages so it isn't a breaking change.
2. **History for agents:** when an agent joins a room, does it get backscroll as context? Default:
   yes, last N messages via `HISTORY_REQ`, capped per agent config.
3. **Voice notes:** now just room-scoped storage files (§5a) with audio MIME + inline playback;
   client capture-to-file UX lands Phase 6.
4. **Who runs speech services:** plan assumes reachable OpenAI/Qwen endpoints and/or self-hosted
   Wyoming (faster-whisper + Piper) on the LAN; the app treats them as per-client settings.
5. **Storage grant semantics on room departure:** files stay with their rooms, not their uploader
   — an agent moved out of a room loses access to that room's files (it's room memory, not agent
   memory). Default chosen; flag if uploader-retained access is wanted.
6. **Storage defaults:** 32 MB per-file cap, 1 GB per-room quota, permanent-by-default with
   optional TTL. All configurable; flag if the defaults are wrong.
7. **Bantz as a Banter companion:** should Bantz itself gain a "send to Banter room" output
   target next to text injection (a zero-UI voice companion that doesn't need the full client
   running)? The shared packages now exist (Bantz v0.2.3, 2026-08-22), so this is cheap —
   still deferred until Banter has a server to send to (Phase 1+).
8. **Task ledger defaults:** 30-min lease, 1 concurrent task per agent, delegator per-room
   grant. Flag if wrong.
