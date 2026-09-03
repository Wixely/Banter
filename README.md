# Banter

A C#-only suite for managing multiple AI agents and conversing with them (and each other) over an
IRC-style room server, with first-class voice (TTS/STT) on desktop, Android, and web.

- **Architecture & build plan:** [PLAN.md](PLAN.md)
- **Client UI decision (CupriFace):** [CUPRIFACE-PLAN.md](CUPRIFACE-PLAN.md)

## Status — Phase 0 → 1

| Piece | State |
|---|---|
| Solution layout (`Banter.slnx`, per PLAN §2) | scaffolded |
| `Banter.Protocol` v1 (envelope, payloads, MessagePack + JSON debug codec, framing) | implemented, tested |
| `IBanterTransport` seam + plain-TCP fallback | implemented, tested |
| WebSocket transport | implemented and tested, but **parked and unwired** — the browser path is CupriNodestar's WebRTC, which rules sockets out |
| `Banter.Transport.Shrine` — a CupriNet L2 conduit as an `IBanterConnection` (§2.5) | implemented, 19 tests |
| `Banter.Server.Nodestar` — a Banter server hosted on a CupriNet node | runs: node online, site addressed, conduit served |
| End-to-end over a conduit (client dials the site) | **green over TCP** — handshake, two clients talking, history paging, all on L2 |
| The same, over a `DataChannelVessel` (the browser's vessel) | **green** — message-oriented framing carries Banter unchanged |
| `Banter.App.Web` — the same CupriApp in a browser, over **real WebRTC** | **green**, verified end to end: connect, join, send, and the message read back out of the server's database |
| Web head on the packaged host (`CupriFace.Web.Mono`) | implemented — touch, the ARIA mirror, IME and clipboard come with it, so the browser is no longer the one head a screen reader cannot use |
| `Banter.Server` (room engine, sessions, auth, in-memory history) | implemented, tested |
| `Banter.Client.Core` (`BanterClient`: handshake, requests, push events, auto-reconnect + rejoin) | implemented, tested |
| End-to-end integration tests (chat, history paging, auth, announcements, spoof rejection) | green |
| CI (`dotnet build` / `test`) | in place |
| Persistence: Dapper + migration manifest — SQLite (default) / PostgreSQL (hosted) | implemented, tested (SQLite; Postgres untested pending a server) |
| Room-scoped file storage (§5a: chunked transfer, hash dedup, grants, quotas, announcements) | implemented, tested |
| `Banter.Cli` interactive client | implemented, smoke-tested |
| `Banter.Transport.CupriNet` (Conjoin → Consecrate → Conduit frames behind `IBanterTransport`) | spiked green: chat runs over the mesh; Android on-device pending |
| CupriMark `banter.core` catalogue + HELLO range negotiation | implemented, tested |
| Streamed messages (`MSG_STREAM_*` relay, persisted as one history message) | implemented, tested |
| Agent guardrails (per-room rate limit + loop-breaker, on by default) | implemented, tested |
| CupriFace spikes — virtualized scrollback, frame budget, streaming rebind, composer | green (headless, `tests/Banter.App.Spikes`); Android/WASM outstanding |
| DaggerAgent spike (LM Studio endpoint: turns, tool calls, `spawn_subagent`, driving an external agent CLI) | green — Path C/ACP stays deferred (PLAN Phase 0) |
| `Banter.Agents.Sdk` (`BanterAgent`, `LlmChatAgent`, streaming replies, per-room context) | implemented, tested |
| `Banter.Warden` (runs an LLM agent as a Banter user) | implemented, verified against LM Studio |
| Warden fleet: config-driven supervision, restart with backoff, config validation | implemented, tested (`samples/fleet.json`) |
| Delegator election + room dispatch modes (§8a) | implemented, tested |
| @mentioning an agent reaches it directly, bypassing delegation — but not the egress rule | implemented, tested |
| Request classification + routing with announced egress (§8a) | implemented, tested |
| Sub-rooms: child room inherits parent sensitivity, `AGENT_MOVE` clearance-gated (§8a) | implemented, tested |
| Delegator opens sub-rooms for local fan-outs; third-party fan-outs stay in-room | implemented, tested |
| Agent-opened rooms named after the work; admins auto-joined to them | implemented, tested |
| One user, many clients: per-account presence, per-session delivery | implemented, tested |
| App: room hierarchy in the sidebar, browse/join other rooms, `/join` and `/rooms` | implemented, tested |
| App: multi-line messages (`white-space: pre-wrap`, needs CupriFace 0.5.0) | implemented, tested |
| Message edit and delete (right-click a message, or `/edit` / `/delete`) | implemented, tested — only the author may edit, author or admin may delete, and a delete removes the text from storage |
| App: inline image previews (cached, size-capped, aspect preserved) | implemented, tested |
| App: right-click menu, copy selected text and copy image to the clipboard | implemented, tested; right-click needs a real window to confirm |
| Fan-out to several agents on request, clearance filter unchanged (§8a) | implemented, tested |
| LLM classifier with keyword veto and fail-closed paths (§8a) | implemented, tested |
| Work ledger (§8b: `TASK_*`, claim arbitration, leases, concurrency cap) | implemented, tested |
| Agents working the ledger (skill-matched claims, lease renewal, result reporting) | implemented, tested |
| App: task board panel, `/task` and `/tasks` commands | implemented, tested |
| Tools executed server-side, per-agent grants, room-announced calls (§8c) | implemented, tested; verified live against the desktop MCPHub (453 tools) |
| Agent tool loop over granted tools (streamed `tool_calls`, bounded rounds) | implemented, tested |
| App: tool-grants panel, `/tools [agent]` | implemented, tested |
| Upstream gaps found by the spike | all four fixed in [DaggerAgent v1.7.0](https://github.com/Wixely/DaggerAgent/releases/tag/v1.7.0): tool-call events, durable CLI sessions, partial output on timeout, NU1903 cleared |
| Embeddable MCP (MCPHub split) | shipped in [MCPHub v0.6.0](https://github.com/Wixely/MCPHub/releases/tag/v0.6.0) — three packages on the feed, tenancy seam in |
| `Banter.App` (shared CupriApp: rooms, wrapping timeline, streaming, composer) | implemented, tested headlessly |
| App: paged scrollback (anchored history prepend, id dedup) | implemented, tested |
| App: persisted settings (no secrets on disk) | implemented, tested |
| App: file transfer (attachment chips, `/upload`, `/files`, download) | implemented, tested |
| App: agent roster panel, delegator/mode header, egress styling (§8a made visible) | implemented, tested |
| `Banter.App.Desktop` (`banter` host head, TCP or CupriNet) | implemented; runs against a live server — connects, joins, survives traffic, exits clean |
| Voice: energy gate with hysteresis, PTT trimming, utterance segmentation (§6) | implemented, tested |
| Voice: `VoiceSession` — both capture modes, ordered transcription off the capture thread | implemented, tested |
| Voice: OpenAI-compatible STT (`/audio/transcriptions`), covers OpenAI, Qwen and local servers | implemented, tested |
| Voice: OpenAI-compatible TTS (`/audio/speech`), streamed, raw PCM and WAV | implemented, tested |
| Voice: sentence segmentation, per-sender voices, readback policy, barge-in (§6) | implemented, tested |
| Voice: app controls — microphone toggle, gate indicator, readback policy | implemented, tested |
| Voice: desktop capture (Bantz recorders) and playback (NAudio / `aplay`) | implemented; needs a listen-test with a real microphone |
| Voice: local Whisper as the desktop default ear, remote by configuration | implemented; model download not yet exercised |
| Voice: global push-to-talk hotkey to the home room (`Bantz.Input`) | implemented, parser tested; needs a press-test |
| Voice: Wyoming adapter (faster-whisper ASR, Piper TTS) | implemented, tested against a fake service |
| App: native file picker (`Attach`), `/upload` kept as the typed route | implemented, tested; dialog itself needs a click-test |
| App: close-to-tray (`stayInTray`) | implemented, **off by default** — the icon could not be confirmed headlessly |
| App: connect screen (server/name/password, no command line needed) | implemented, tested |
| `Banter.App.Android` (`CupriActivity` head, TCP) | implemented; builds a signed 26 MB APK, not yet run on a device |
| Android voice: `AudioRecord` capture, `AudioTrack` playback, in-context mic permission | implemented; needs a device |

## Running the server

```
docker compose up -d          # or: dotnet run --project src/Banter.Server
```

An **`admin`** account is created on first run and is always an admin — the oversight rule
(PLAN §8a) puts admins into every room an agent opens, so a deployment without one has agents
holding conversations nobody is watching.

Its password defaults to `admin`, and the server says so loudly on startup. Set one of, in order
of precedence:

| Setting | Notes |
|---|---|
| `BANTER_ADMIN_PASSWORD_FILE` | Path to a file holding the password. Preferred: keeps it out of `docker inspect` and shell history. Trailing newline trimmed. |
| `--admin-password <secret>` | Command line. |
| `BANTER_ADMIN_PASSWORD` | Environment variable; what `compose.yaml` wires up. |

An unreadable secret file warns and falls back rather than refusing to start, so a mount typo
does not turn into a crash loop.

## Using the SDK from another project

An agent is a **client**, not something the server hosts: a separate process that logs in, joins
rooms and answers. Anything that wants to be one references `Banter.Agents.Sdk` — that is what
`Banter.Warden` in this repository does, and what [DaggerAgent](https://github.com/Wixely/DaggerAgent)
does from outside it.

Four packages publish to the Wixely GitHub Packages feed, in lockstep, on a `v*` tag. The SDK pulls
the other three behind it:

| Package | |
|---|---|
| `Banter.Agents.Sdk` | `BanterAgent`, `LlmChatAgent`, the routing attributes — what an agent subclasses |
| `Banter.Client.Core` | `BanterClient`, enrolment, the key on disk |
| `Banter.Core` | accounts, agent identities, request classification |
| `Banter.Protocol` | the wire: verbs, payloads, framing, transports, `AgentKeys` |

Nothing else is published. The server, the CLI, the Warden and the app heads are applications, and
the voice and transport libraries have no consumer outside this repository yet — all of them say so
in their project files rather than relying on a filter in CI.

```
dotnet nuget add source https://nuget.pkg.github.com/Wixely/index.json   --name GitHub-Wixely-Packages --username <your-github-username> --password <a-PAT-with-read:packages>
dotnet add package Banter.Agents.Sdk
```

A consuming repository that uses package source mapping needs a `Banter.*` pattern pointing at that
source, alongside whatever it already has for `CupriNet*` and `CupriFace*`.

**On versions.** The protocol moves, and the four packages move together — mixing versions within
one surface is the failure this lockstep exists to prevent. Across versions, `banter.core` is
negotiated through [CupriMark](https://github.com/Wixely/CupriMark) at HELLO, and new payload fields
are added as trailing optional ones, so a client and a server on different releases agree on what
they both speak rather than failing to decode.

## Building

Requires the .NET 10 SDK. Wixely-family packages (CupriNet, CupriFace, Bantz.*) restore from the
Wixely GitHub Packages feed — set `CUPRIFACE_GITHUB_USER` / `CUPRIFACE_GITHUB_TOKEN`
(a PAT with `read:packages`). Nothing in Phase 0 references that feed yet, so a plain build works
without credentials:

```
dotnet build Banter.slnx
dotnet test Banter.slnx
```

### The Shrine transport (the web head's server half)

`Banter.Transport.Shrine` presents a CupriNet **conduit** as an `IBanterConnection`, so the whole
stack above the transport seam runs over L2 unchanged. It is in `Banter.slnx` and covered by the
ordinary test run: `CupriNet.Nodestar` reached the feed at `0.1.0-alpha.9`, so the local build and
the three per-project `NuGet.config` files it needed are gone.

A client dials the **site's** vessel host (`ShrineVesselHost`) and pins the **site's** Signet. Both
halves matter: the node's own listen port reaches the *node*, and a session with no Shrine behind it
answers every rite with a closed stream. That was [CupriNodestar#2](https://github.com/Wixely/CupriNodestar/issues/2),
and it is why only WebRTC worked before — the browser gate accepts the DataChannel into the
Pilgrimage itself, and nothing else did.

```
dotnet test tests/Banter.Transport.Shrine.Tests/Banter.Transport.Shrine.Tests.csproj

# A Banter server on a CupriNet node rather than a socket
dotnet run --project src/Banter.Server.Nodestar -- --data <dir> --network banter
```

It prints the site's `cupri1…` address once the node is online, along with the port clients dial
(`--site-port`, default 7411), and Nodestar reports `Raw sessions: served.` when the Banter conduit
has been registered.

The local source is declared in a `NuGet.config` beside each of those projects, so the rest of the
repo and CI restore exactly as before. **When alpha.6 reaches the feed:** delete those files, change
the `PackageReference` to `0.1.0-alpha.6`, and put the projects in the solution.

### Ports

Everything Banter binds sits in one block, and none of it is left to a framework default — a
Banter node and a plain Nodestar node used to pick the same web front and the same overlay port, so
whichever started second failed to bind.

| Port | What | Override |
|---|---|---|
| 7770 | `Banter.Server`, plain TCP | `--endpoint` |
| 7771 | mesh server: vessels, for desktop clients | `--site-port` |
| 7772 | mesh server: the node's overlay beacon | `--listen-port` |
| 7773/udp | mesh server: the browser on-ramp (WebRTC) | `--webrtc-port` |
| 7774 | mesh server: clearnet front, serving `link.json` | `--web-port` |
| 7775 | the web head's dev server | `Properties/launchSettings.json` |

The mesh server prints all four of its own at startup, so a failure to bind names the port.

### Debugging a room with agents

**Run → "Server + alice + local agent + DaggerAgent"**: the server, the desktop client, and two
agents with different reach — `dagger` on a local model through LM Studio, `scout` through
[DaggerAgent](https://github.com/Wixely/DaggerAgent), which drives external agent CLIs (Copilot,
Claude Code, Gemini) as tools of its own.

Banter knows nothing about those CLIs, and should not: DaggerAgent serves an OpenAI-compatible
endpoint, so to a Banter agent it is just another `--llm`. What makes it a Copilot agent is
DaggerAgent's configuration. That is PLAN Path A: **external agents are tools inside DaggerAgent
rather than Banter users of their own**, so a sub-agent is bounded by its parent's budget instead of
the room's throttle, and one integration covers every CLI rather than one per vendor.

Since [DaggerAgent v1.8.0](https://github.com/Wixely/DaggerAgent/releases/tag/v1.8.0) that is done
over **ACP** rather than a shell — a long-lived session per job instead of a process per turn, and
no shell access to grant:

```json
"Tools": {
  "AllowCliDelegation": true,
  "AcpAgents": [{
    "Name": "copilot", "Command": "copilot", "Arguments": ["--acp"],
    "Enabled": true, "Protocol": "acp", "PermissionPolicy": "deny"
  }]
}
```

Leave `PermissionPolicy` at `deny`. `ask` forwards the child's permission requests to whoever is
driving the job, and in a chat room nobody is — it would stall until the timeout and deny anyway.

Needs LM Studio on `:1234` and DaggerAgent on `:5090`.

### Debugging the web stack

**Run → "Mesh server + web client"** (`.vscode/launch.json`). That builds and starts a Banter
server on a CupriNet node with WebRTC on, serves the web head at `http://localhost:7775`, and opens
a browser on it. Sign in as **admin / banter**.

The Server field is already filled: the node writes its link to
`src/Banter.App.Web/wwwroot/seed.json` (`--seed-file`, gitignored, rewritten every 30s because links
rotate), and the client fetches it from its own origin at boot. Without that the field would need a
400-character paste on every run.

Breakpoints: **C# in the server** through the ordinary .NET debugger, and **JavaScript in the
client** through VS Code's built-in browser debugger. Breakpoints in the client's *C#* are not
wired — the SDK ships a Mono debug proxy for it, but the only VS Code adapter that drives that proxy
is the Blazor-named one, and there is no Blazor here. It matters less than it sounds: the web head
is now one line of host plus a WebRTC data channel, and everything worth stepping through lives in
`Banter.App` and `Banter.Client.Core`, which the test suite covers headlessly.

Nothing in `Banter.App.Web` is Blazor: no Razor, no components, no `blazor.webassembly.js`.
`Microsoft.NET.Sdk.WebAssembly` is the WASM build SDK, and the UI is the same `CupriApp` the desktop
runs, painted to a canvas by `CupriFace.Web.Mono`.

### The Android head

`Banter.App.Android` is **not** in `Banter.slnx`, on purpose: it needs the `android` workload, and
putting it in the solution would make that a requirement for anyone building the server or the
desktop client. It has its own CI job and is built directly.

```
dotnet workload install android
dotnet build src/Banter.App.Android/Banter.App.Android.csproj -c Release -t:SignAndroidPackage
```

It speaks `tcp://` only for now — CupriNet on Android is still the Phase 0 spike PLAN §10 lists as
outstanding, and the head says so rather than timing out with no reason.

Voice on the phone uses **remote engines only** (PLAN §6a): an OpenAI-compatible endpoint or a
Wyoming service, set through `voice.endpoint` or `voice.wyomingAsr`. Local Whisper stays the
desktop default — a 148 MB model and native inference are not what you want on a phone. A phone
with neither configured simply has no microphone button.

## Licence

MIT — see [LICENSE](LICENSE). The published packages carry the same expression, so a consumer sees
it on the package as well as on the repository.
