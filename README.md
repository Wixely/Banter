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
| Delegator election + room dispatch modes (§8a) | implemented, tested |
| Request classification + routing with announced egress (§8a) | implemented, tested |
| Sub-rooms: child room inherits parent sensitivity, `AGENT_MOVE` clearance-gated (§8a) | implemented, tested |
| Fan-out to several agents on request, clearance filter unchanged (§8a) | implemented, tested |
| Upstream gaps found by the spike | all four fixed in [DaggerAgent v1.7.0](https://github.com/Wixely/DaggerAgent/releases/tag/v1.7.0): tool-call events, durable CLI sessions, partial output on timeout, NU1903 cleared |
| Embeddable MCP (MCPHub split) | shipped in [MCPHub v0.6.0](https://github.com/Wixely/MCPHub/releases/tag/v0.6.0) — three packages on the feed, tenancy seam in |
| `Banter.App` (shared CupriApp: rooms, wrapping timeline, streaming, composer) | implemented, tested headlessly |
| App: paged scrollback (anchored history prepend, id dedup) | implemented, tested |
| App: persisted settings (no secrets on disk) | implemented, tested |
| App: file transfer (attachment chips, `/upload`, `/files`, download) | implemented, tested |
| `Banter.App.Desktop` (`banter` host head, TCP or CupriNet) | implemented, needs a manual run against a live server |

## Building

Requires the .NET 10 SDK. Wixely-family packages (CupriNet, CupriFace, Bantz.*) restore from the
Wixely GitHub Packages feed — set `CUPRIFACE_GITHUB_USER` / `CUPRIFACE_GITHUB_TOKEN`
(a PAT with `read:packages`). Nothing in Phase 0 references that feed yet, so a plain build works
without credentials:

```
dotnet build Banter.slnx
dotnet test Banter.slnx
```
