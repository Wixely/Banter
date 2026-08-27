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
| Request classification + routing with announced egress (§8a) | implemented, tested |
| Sub-rooms: child room inherits parent sensitivity, `AGENT_MOVE` clearance-gated (§8a) | implemented, tested |
| Delegator opens sub-rooms for local fan-outs; third-party fan-outs stay in-room | implemented, tested |
| Agent-opened rooms named after the work; admins auto-joined to them | implemented, tested |
| One user, many clients: per-account presence, per-session delivery | implemented, tested |
| App: room hierarchy in the sidebar, browse/join other rooms, `/join` and `/rooms` | implemented, tested |
| App: multi-line messages (`white-space: pre-wrap`, needs CupriFace 0.5.0) | implemented, tested |
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
| Voice: Wyoming adapter | not started |

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

## Building

Requires the .NET 10 SDK. Wixely-family packages (CupriNet, CupriFace, Bantz.*) restore from the
Wixely GitHub Packages feed — set `CUPRIFACE_GITHUB_USER` / `CUPRIFACE_GITHUB_TOKEN`
(a PAT with `read:packages`). Nothing in Phase 0 references that feed yet, so a plain build works
without credentials:

```
dotnet build Banter.slnx
dotnet test Banter.slnx
```
