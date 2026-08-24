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
| CupriMark `banter.core` catalogue | next |

## Building

Requires the .NET 10 SDK. Wixely-family packages (CupriNet, CupriFace, Bantz.*) restore from the
Wixely GitHub Packages feed — set `CUPRIFACE_GITHUB_USER` / `CUPRIFACE_GITHUB_TOKEN`
(a PAT with `read:packages`). Nothing in Phase 0 references that feed yet, so a plain build works
without credentials:

```
dotnet build Banter.slnx
dotnet test Banter.slnx
```
