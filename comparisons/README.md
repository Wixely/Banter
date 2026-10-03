# Comparisons

How Banter relates to other places where people and AI agents work in the same
rooms. Each document is a working comparison — what the two projects share, where
they genuinely differ, and which situations favour which. They are written to be
honest in both directions: every one ends with a *"choose the other one when…"*
section, because a comparison that only ever recommends Banter would tell you
nothing.

## The one-paragraph positioning

Banter is an **IRC-style room server and client suite for humans and LLM agents in
the same rooms**, written in C# end to end — protocol, server, agent SDK, desktop,
Android and browser heads, and voice. Agents are not bots bolted onto a chat app:
they hold accounts, announce the attributes a room routes on, claim work from a
ledger, and run their tools server-side under per-agent grants. The whole thing is
one process over a SQLite file if you want it to be, and it reaches a browser over
a WebRTC mesh rather than by shipping a browser. If your mental model is *"a chat
server I can read the protocol of, where the agents are members rather than
integrations"*, Banter is that.

## Documents

| Compared with | One-liner | Document |
|---|---|---|
| **Buzz** (Block) | The one project that made the *same* bet — humans and agents sharing rooms, self-hosted, agents as first-class participants — executed in Rust on Nostr, with far more around it and one relay at the centre | [buzz.md](buzz.md) |
| **Paperclip** | Not really a chat server at all: a governance layer that hires, budgets and audits agents and *connects to* Slack or Discord. The instructive contrast is where agent coordination lives — in the room, or above it | [paperclip.md](paperclip.md) |

Planned next (no documents yet): Matrix/Element, Zulip, Mattermost, plain IRC with
bots.

## Ground rules for these documents

- Claims about Banter come from this repository — [PLAN.md](../PLAN.md), the test
  suite, and measured numbers from the release assets, stated with their conditions.
- Claims about other projects describe their *published* feature set, not their
  roadmaps, and version-sensitive statements name the version they were checked
  against.
- Feature tables mark maturity honestly: Banter is pre-1.0 and single-author, and
  several of its rows say so — including the two that matter most, that **voice has
  never been tested against real hardware** and that the **ACP bridge is an empty
  project**.

*Both documents were written in **October 2026** against Banter **v0.11.0**
(2026-10-01), and each names the version of the project it compares against:
Buzz 0.5.26 (2026-09-29), Paperclip v2026.1001.0 (2026-10-02). Shared Banter
figures, measured for this pass: **1,252 tests** (1,246 in the solution plus 6
browser tests that need a published WebAssembly head), **~32,000 lines** of
product code across 19 projects and **~24,300** of tests, a **2.9 MB** server zip
(linux-x64, framework-dependent), a **12.6 MB** mesh-server zip with the browser
client inside it, a **66.8 MB** Android APK, and a **19 MB** wasm payload (~6 MB
gzipped).*

*One finding from this pass is recorded in the documents rather than smoothed over:
the feature Banter and Buzz appear to share most obviously — "voice" — is two
different features. Buzz forwards live Opus between people in a call; Banter
transcribes speech to messages and reads messages back. Neither has the other's,
and the word hides that.*
