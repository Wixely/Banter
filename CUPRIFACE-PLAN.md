# CupriFace as the Banter UI — Suitability Investigation

Companion to [PLAN.md](PLAN.md). Question under investigation: is
[Wixely/CupriFace](https://github.com/Wixely/CupriFace) a suitable UI for the Banter client, and
does a **web-enabled** Banter client now make sense, given CupriNet 0.2.0's browser on-ramp?

> **Re-evaluated 2026-08-17:** CupriFace now supports **Android** (iOS explicitly a lesser
> concern). The original desktop-only framing and the "MAUI for mobile" split are superseded —
> see §1a and the revised §4–§6. Short version: one CupriFace stack for desktop + web + Android;
> MAUI drops out of the plan; iOS deferred.

## 1. What CupriFace is

A desktop UI runtime that renders **HTML + CSS to a GPU canvas** with C# backend binding — an
Electron alternative with no embedded browser and no JavaScript engine. Relevant facts:

- .NET 10, fully managed, AOT-friendly: managed flexbox/grid, CSS via AngleSharp, text shaping
  via HarfBuzz, Skia rendering.
- Targets: **Windows x64, Linux x64, macOS (Apple Silicon)** desktop, plus **web via .NET WASM**
  rendering to `<canvas>` — the same portable `CupriApp` class hosts on desktop or web with no
  platform-specific code.
- Built-in component set: buttons, sliders, switches, badges, progress, tabs, trees, accordions,
  tables, charts, modals, drawers, animations, `<cupri-video>`; data binding via `{{path}}`
  interpolation; CSS-variable theming with dark-mode transitions.
- Windows UIA accessibility bridging on desktop.
- Active project, prebuilt standalone executables per release; NuGet packages exist but not on
  nuget.org (private feed).

### 1a. Android support (added since first draft)

`CupriFace.Android` targets **net10.0-android**: apps extend a `CupriActivity` base class hosting
a GL surface, with the same Skia pipeline as desktop (pixel-identical rendering). Mobile input is
not a stub: tap-on-release, momentum-fling scrolling, long-press, an `OnManipulate` recognized-
gesture layer (drag/pinch/rotate, raw pointers still accessible), and — the part that matters
most for a chat app — **soft keyboard with real IME composition**, not emulated input.
Accessibility is bridged to **TalkBack**, and the project's accessibility claims now span four
platforms "each proven in CI by a real assistive-technology client". APKs come out around ~20 MB.

Consequence: an Android CupriFace app is a plain .NET-for-Android app, so everything Banter's
mobile plan needs from the platform — foreground service for always-listening, `AudioRecord`
capture, permissions, notifications — is ordinary `net10.0-android` code in the same process.
MAUI was only ever providing cross-platform UI plus Essentials conveniences; with CupriFace
covering the UI, MAUI no longer earns its place as a second stack.

### 1b. Updates as of 2026-08-22 (v0.2.7 → v0.2.11)

A rapid release run since the Android re-evaluation. Deltas that matter to Banter:

- **Packages are now installable** from the Wixely GitHub Packages NuGet feed (core engine,
  desktop host, Android host, optional WebM media) — the "releases only" friction is gone;
  Phase 0 starts with a `nuget.config` entry + `read:packages` PAT.
- **Self-contained executables** for Windows/Linux/macOS/Android with no .NET install required —
  simplifies desktop distribution of the Banter client considerably.
- **Native window chrome and tray controls are framework features now** — the tray icon with
  mute/listening state moves from "our Win32 interop" to built-in; only the global PTT hotkey
  (`RegisterHotKey`) remains ours.
- **Desktop pointer and clipboard app integration** landed — one of the two critical composer
  unknowns is now framework-supported (spike shrinks to verifying paste-image and feel).
- **Android video via the platform's native `MediaPlayer`** (`<cupri-video>`) — the media
  playback path for §5a room files on Android; audio-only playback presumably rides the same
  route (verify).
- **Page zoom**: keyboard + Ctrl+wheel, 0.5×–4×, element-anchored, restorable via `ZoomChanged` —
  a real accessibility/readability win for a chat timeline, free of charge.
- **Web target is now CI-gated, including touch** ("fingers on glass, in CI") and a Blazor
  hosting sample — web viability is being proven continuously rather than claimed.
- Hover-interaction stability hardening and a component shipping/override plan (theming story
  forming).

None of this changes direction — it removes friction from the already-recommended path and
shrinks two Phase 0 spikes (clipboard, tray). Noted in passing: CupriNet is now 0.3.x
("Auspice live feeds"); Banter's transport plan is unaffected but worth a look for future
live-status fan-out.

## 2. Suitability for the Banter desktop client

Banter's client logic was already designed UI-agnostic (`Banter.Client.Core` for
connection/state, `Banter.Voice` for the audio pipeline — PLAN.md §2). So the UI framework
question is purely about the presentation layer. Assessment against what a chat client needs:

| Banter need | CupriFace answer | Gap / spike |
|---|---|---|
| Rooms/members/timeline layout | Flexbox/grid + tabs/trees/drawers/modals — comfortably covered | — |
| **Rich agent output (markdown)** | Its native input *is* HTML+CSS — render markdown→HTML (Markdig, pure C#) straight into the view. Better fit than MAUI, where markdown rendering is a chronic pain | — |
| Streaming message rendering (`MSG_STREAM_DELTA`) | Data binding + DOM-ish updates should handle append-as-you-go | Spike: update rate/perf during fast token streams |
| **Large scrollback** | Unknown whether list virtualization exists | **Spike (critical):** 10k-message room scroll perf; if no virtualization, we window the timeline ourselves (cap + HISTORY paging already planned) |
| Multi-line composer, IME, clipboard, drag-drop file upload | Text input exists; Android has real IME composition (§1a); desktop clipboard integration landed in v0.2.x (§1b) | **Spike:** typing feel, paste-image specifically; composer feel on-device Android |
| Inline images / audio playback (voice notes, §5a files) | `<cupri-video>` exists, now backed by native `MediaPlayer` on Android (§1b); Skia handles images | Spike: audio-only playback path; else route audio to `Banter.Voice` playback (same process) and keep UI as controls only |
| Voice pipeline (capture, VAD, PTT) | Not a UI concern — `Banter.Voice` runs in the same .NET process using WASAPI/AVAudioEngine as planned | — |
| **Global PTT hotkey, tray icon** | Tray controls + native window chrome are now framework features (§1b); global hotkey stays ours — a CupriFace desktop app is a normal .NET process, so the planned `RegisterHotKey` interop (PLAN.md §7) works unchanged | — |
| Notifications (mentions) | OS toast via interop, same as MAUI would need | — |
| Theming | CSS variables, dark mode — nicer than MAUI resource dictionaries | — |
| Accessibility | UIA on Windows desktop; Linux/macOS unknown | Accept for v1; note web caveat §3 |
| **Linux desktop** | **Supported — MAUI cannot do this at all** | Net new platform gained |
| Packaging | Prebuilt standalone executables, AOT-friendly | Simpler than MSIX if we want it |

**Fit verdict for desktop: suitable, and in two places better than MAUI** — markdown-native
rendering for agent output, and Linux support. The open risks are chat-client table stakes
(virtualized scrollback, editor/IME polish) that generic component lists never prove; both are
cheap spikes against the real framework.

## 3. The web-enabled client

CupriFace's WASM target changes the calculus: the *same* `CupriApp` codebase becomes a browser
client. With the proven CupriNet RTC connection (browser ↔ server), a web client stops being a
separate product and becomes a third host for the same app.

- **Transport — now documented (CupriNet 0.2.0, 2026-08-14):** the `CupriNet.WebRtc` binding
  (managed ICE/DTLS 1.3/SCTP via CupriWebRTC) lets Banter.Server accept browser WebRTC
  DataChannel peers **with no signalling server**: the server's signed Intonation URI carries its
  static WebRTC endpoint parameters and browsers dial it directly. Noise + Consecration run
  unchanged over the DataChannel, so the browser client authenticates with the same watchword /
  channel model as every native peer — the app layer sees "just another peer". Remaining spike
  item: the README names no *browser-side* client library, so the WASM app's path onto the
  DataChannel (C# over `RTCPeerConnection` JS interop, or a CupriNet WASM target) still needs
  proving. The already-planned `IBanterTransport` WebSocket fallback (PLAN.md §3) stays as the
  safety net if the browser-side stack lags.
- **Deployment is nearly free:** `Banter.Server` already runs Kestrel-adjacent infrastructure;
  serve the WASM bundle as static files from the server itself. Visit `https://server/` → get the
  client → it connects back over RTC/WebSocket. No install, ideal for guests, ops dashboards, and
  quick "check on the agents from any machine" access.
- **Voice on web is the real constraint,** not rendering:
  - Mic capture must use `getUserMedia`/Web Audio via JS interop (WASM hosts allow this even
    though CupriFace itself embeds no JS engine — the browser *is* the host). `Banter.Voice`
    capture gets a third backend: WASAPI / AVAudioEngine / WebAudio-interop.
  - No global hotkeys (browser sandbox) — on-page PTT button + in-page keybind only.
  - Always-listening degrades: background tabs are throttled and mic indicators are prominent.
    Web client always-listening is foreground-tab-only, best-effort.
  - Recommendation: **web v1 ships text-first** (full chat, rooms, files, agent control), voice
    follows as web v1.1 with the WebAudio backend.
- **Web accessibility caveat:** canvas-rendered UI is invisible to screen readers unless the
  framework bridges to hidden DOM; CupriFace's UIA bridge is desktop-only today. Accept and
  document for v1; raise upstream (it's our library).
- **Auth note:** watchword + user credentials flow is unchanged, but a web client served from the
  open internet makes rate-limiting `AUTH` and CORS/origin pinning on the static host worth doing
  when we get there.

## 4. Options considered (revised 2026-08-17 for Android support)

| Option | Shape | Assessment |
|---|---|---|
| **A (recommended): CupriFace everywhere except iOS** | One `Banter.App.Face` CupriApp; hosts: desktop (Win/Linux/macOS), web (WASM), Android (`CupriActivity`). No MAUI. iOS deferred. | **One UI stack, one HTML/CSS design system, one codebase** across every v1 target. Gains Linux + web vs. the MAUI plan; Android keeps its full platform powers (foreground service, AudioRecord) as plain net10.0-android code. The two-stack cost that made the original recommendation a compromise is gone. |
| B: CupriFace desktop + web; MAUI mobile (the original recommendation) | `Banter.App.Face` + `Banter.App.Mobile` (MAUI Android/iOS) | Now only worth it if the Android on-device spikes disappoint, **or** if iOS becomes a must-have before CupriFace iOS exists — MAUI re-enters for iOS (and then Android is a judgment call: keep it on CupriFace or ride along in MAUI). |
| C: MAUI everywhere | As PLAN.md §7 baseline | Deep fallback only; weakest desktop story, no Linux, no web. |

**Recommendation: Option A**, gated on the Phase-0 spikes (which now include on-device Android
checks). Fallback ladder: Android spikes fail → B; CupriFace fundamentals fail → C.

**iOS position (explicitly a lesser concern):** ship v1 without an iOS app. iPhone users are not
locked out — the **web client is the iOS story** in the interim (Safari; text + tap-to-talk
best-effort). Decide at Phase 6 between: wait for CupriFace iOS, or a thin MAUI/native iOS app
if demand materializes. Nothing in `Banter.Client.Core`/`Banter.Voice` needs to change either
way.

## 5. Impact on PLAN.md if Option A is adopted

- **Layout:** `Banter.App` becomes a single `Banter.App.Face` — one portable `CupriApp`
  (views, view-models over `Banter.Client.Core`) plus thin host heads: desktop executable
  (Win/Linux/macOS), WASM web host, and an Android head (`CupriActivity` + foreground-service /
  audio glue). **MAUI leaves the plan entirely**; PLAN.md §7's platform-specifics section
  survives almost intact — it was always about OS APIs (WASAPI, AudioRecord, `RegisterHotKey`,
  foreground services), not MAUI.
- **Phase 0 spikes (all against real CupriFace):**
  1. 10k-message virtualized/windowed scrollback perf — desktop **and** a mid-range Android
     phone;
  2. composer feel — multi-line editing and paste-image on desktop (clipboard integration is
     framework-level as of v0.2.x — verify feel, not existence); soft-keyboard/IME composition
     on-device Android (same: verify feel);
  3. streaming-delta render rate;
  4. WASM host: bundle size, cold start, and a browser → dev-server round-trip over the
     CupriNet.WebRtc DataChannel (proving the browser-side stack; WebSocket fallback if needed);
  5. Android endurance: GL-surface + foreground-service battery drain over an hour of
     always-listening, and APK size sanity (~20 MB baseline + our payload).
- **Phase 2** becomes: one CupriFace app brought up on Windows + Linux + Android against the same
  room-UI milestone. Web host enters as **Phase 2.5**: same CupriApp served from Banter.Server,
  text-first.
- **Phase 3–4 (voice):** logic unchanged (`Banter.Voice`, same process on every host). Capture
  backends: WASAPI (desktop), AudioRecord (Android head), WebAudio interop (web, Phase 4+).
  Global hotkeys/tray land on the desktop head as planned; Android always-listening foreground
  service lands in the Android head.
- **Phase 6:** macOS desktop head (Apple Silicon); iOS decision point (wait for CupriFace iOS vs
  thin native/MAUI app vs web-only continues).
- **Risks table additions:** CupriFace chat-table-stakes unknowns (scrollback/editor — mitigated
  by Phase 0 spikes + fallback ladder A→B→C); single-stack concentration on one pre-1.0 UI
  runtime we maintain ourselves (accepted — we control the repo, and issues found are fixes to
  our own product, not upstream begging); browser-*side* DataChannel client stack unproven
  (server side documented in CupriNet 0.2.0 — WebSocket fallback); canvas accessibility on web
  (improved: a11y now CI-proven with real AT clients on four platforms incl. TalkBack — verify
  the web canvas bridge specifically).

## 6. Verdict (revised 2026-08-17)

**Yes — and with Android support it graduates from "suitable desktop UI" to "the Banter client,
full stop."** One CupriApp codebase and one HTML/CSS design system covers desktop
(Win/Linux/macOS), web (WASM served by Banter.Server over the CupriNet 0.2.0 WebRTC on-ramp,
WebSocket fallback), and Android (CupriActivity head with full platform powers — foreground-
service always-listening and AudioRecord capture are plain net10.0-android code). MAUI exits the
plan; iOS is deferred with the web client as the interim iPhone answer, decided properly at
Phase 6. Adoption is contingent on the Phase 0 spikes — chiefly virtualized scrollback and
composer/IME feel, now on desktop *and* a real Android device — with the fallback ladder:
Android spikes fail → reintroduce MAUI for mobile (old Option A); fundamentals fail → MAUI
everywhere. Web ships text-first; web voice follows via a WebAudio capture backend.
