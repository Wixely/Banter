// Banter's web head — the whole of its JS. Boot the .NET runtime, hand the engine a <canvas> 2D
// context, blit its pixels each frame, and forward pointer, wheel and keyboard input. No Blazor
// and no UI framework: the interface itself is drawn by the engine, in C#.
//
// Modelled on CupriFace's own WebWasm sample, trimmed to what Banter needs. Anything missing here
// (video underlays, the ARIA mirror, IME candidate positioning) is present there and can be
// lifted when Banter wants it — or inherited outright if that host ever ships as a package.

import { dotnet } from './_framework/dotnet.js'

const canvas = document.getElementById('banter');
const ctx = canvas.getContext('2d');

// A hidden, focused textarea owns keyboard focus and receives NATIVE copy/cut events, so the
// clipboard works with no permission prompt. navigator.clipboard.readText would prompt on paste
// and wedges headless automation.
const kbd = document.createElement('textarea');
kbd.id = 'banter-kbd';
kbd.setAttribute('aria-hidden', 'true');
kbd.autocapitalize = 'off'; kbd.autocomplete = 'off'; kbd.spellcheck = false;
kbd.style.cssText = 'position:absolute;top:0;left:0;width:1px;height:1px;opacity:0;border:0;padding:0;resize:none;overflow:hidden;';
kbd.value = ' '; // non-empty, so copy/cut fire even with nothing selected in the textarea itself
document.body.appendChild(kbd);
const keepSelected = () => { kbd.value = ' '; kbd.setSelectionRange(0, kbd.value.length); };
const focusKbd = () => { kbd.focus({ preventScroll: true }); keepSelected(); };

// Boot progress mirrored into a hidden element, so a headless check can read how far startup got
// even when the canvas never paints. This is the difference between "it is broken" and knowing
// which step broke.
const bootLog = document.createElement('pre');
bootLog.id = 'bootlog'; bootLog.style.display = 'none';
document.body.appendChild(bootLog);
function logBoot(s) { bootLog.textContent += s + '\n'; }
window.addEventListener('error', e => logBoot('WINDOW-ERROR: ' + (e.error && e.error.stack || e.message)));
window.addEventListener('unhandledrejection', e => logBoot('UNHANDLED-REJECTION: ' + (e.reason && e.reason.stack || e.reason)));
for (const lvl of ['error', 'warn', 'info', 'log', 'debug']) {
    const orig = console[lvl].bind(console);
    console[lvl] = (...a) => { try { logBoot(lvl.toUpperCase() + ': ' + a.map(x => x && x.stack || String(x)).join(' ')); } catch {} orig(...a); };
}

// Any failure is drawn onto the canvas, so it is visible without opening dev tools.
function showError(where, err) {
    const msg = (err && (err.stack || err.message)) || String(err);
    console.error('[Banter] ' + where, err);
    logBoot('ERROR(' + where + '): ' + msg);
    ctx.fillStyle = '#14161a'; ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = '#ff6b6b'; ctx.font = '14px monospace';
    ctx.fillText('Banter failed to start (' + where + '):', 16, 28);
    msg.split('\n').slice(0, 24).forEach((line, i) => ctx.fillText(line.slice(0, 110), 16, 52 + i * 18));
}

try {
    logBoot('create...');
    const { setModuleImports, getAssemblyExports, getConfig, runMain } = await dotnet
        .withDiagnosticTracing(false)
        .create();
    logBoot('created');

    // ---- WebRTC -------------------------------------------------------------------------------
    // No signalling server, and none is needed: the node is ICE-lite and DTLS-passive, and its
    // signed link already carries the ICE credentials and DTLS fingerprint it would have sent in an
    // answer. So the browser offers, and then writes the node's answer itself. Forging it would
    // mean forging the link.
    //
    // The SDP shape is the node's DCEP responder's, not ours — it follows Nodestar's reference
    // client, including the channel label and a=setup:passive.
    const rtc = { pc: null, ch: null, state: 0, error: '' };

    function rtcFail(why) {
        rtc.error = String(why && why.message ? why.message : why);
        rtc.state = 2;
        console.error('[banter/rtc]', why);
    }

    function rtcTeardown() {
        try {
            if (rtc.ch) { rtc.ch.onmessage = rtc.ch.onopen = rtc.ch.onclose = rtc.ch.onerror = null; rtc.ch.close(); }
        } catch { /* already gone */ }
        try {
            if (rtc.pc) { rtc.pc.onconnectionstatechange = rtc.pc.oniceconnectionstatechange = null; rtc.pc.close(); }
        } catch { /* as above */ }
        rtc.ch = null; rtc.pc = null;
    }

    function answerFrom(host, port, ufrag, password, fpAlg, fpHex) {
        const fp = fpHex.toUpperCase().match(/../g).join(':');
        return [
            'v=0',
            'o=- 0 0 IN IP4 ' + host,
            's=-',
            't=0 0',
            'a=group:BUNDLE 0',
            'm=application ' + port + ' UDP/DTLS/SCTP webrtc-datachannel',
            'c=IN IP4 ' + host,
            'a=mid:0',
            'a=sctp-port:5000',
            'a=max-message-size:262144',
            'a=ice-ufrag:' + ufrag,
            'a=ice-pwd:' + password,
            'a=ice-lite',
            'a=fingerprint:' + fpAlg + ' ' + fp,
            // The node is the DTLS server and never initiates checks; the browser is the client
            // and the controller.
            'a=setup:passive',
            'a=candidate:1 1 udp 2130706431 ' + host + ' ' + port + ' typ host',
            'a=end-of-candidates',
            '',
        ].join('\r\n');
    }

    let img = null;
    window.__paints = 0; // diagnostic: how many times the canvas was actually painted
    setModuleImports('banter', {
        // rgba is a view over the engine's bitmap in WASM memory; slice() reads it across in one
        // copy. (dx,dy,dw,dh) is the damage rect, which narrows the blit to what changed.
        present: (rgba, w, h, dx, dy, dw, dh) => {
            if (!img || img.width !== w || img.height !== h) img = ctx.createImageData(w, h);
            img.data.set(rgba.slice());
            ctx.putImageData(img, 0, 0, dx, dy, dw, dh);
            window.__paints++;
        },
        cursor: name => { canvas.style.cursor = name; },
        // The node's link, if one was left for us. Same origin, so no CORS and no server to ask.
        seedLink: () => seed,
        navigate: href => { window.open(href, '_blank', 'noopener'); },
        favicon: dataUri => {
            let link = document.querySelector('link[rel="icon"]');
            if (!link) { link = document.createElement('link'); link.rel = 'icon'; document.head.appendChild(link); }
            link.href = dataUri;
        },

        rtcConnect: (host, port, ufrag, password, fpAlg, fpHex) => {
            try {
                rtcTeardown();
                rtc.state = 0; rtc.error = '';

                const pc = new RTCPeerConnection({ iceServers: [] });
                rtc.pc = pc;

                // The browser opens the channel; negotiated:false with id 0 is what the node's
                // DCEP responder expects.
                const ch = pc.createDataChannel('cupri', { ordered: true });
                ch.binaryType = 'arraybuffer';
                rtc.ch = ch;

                ch.onopen = () => { rtc.state = 1; };
                ch.onclose = () => { if (rtc.state !== 2) rtc.state = 3; I.RtcClosed(); };
                ch.onerror = e => rtcFail('datachannel: ' + ((e && e.message) || 'error'));
                ch.onmessage = e => I.RtcMessage(new Uint8Array(e.data));

                pc.oniceconnectionstatechange = () => {
                    console.log('[banter/rtc] ice ' + pc.iceConnectionState);
                    if (pc.iceConnectionState === 'failed') rtcFail('ice failed');
                };

                // Noticing that the far end has GONE is the slow part of WebRTC. A peer that dies
                // without closing leaves readyState 'open' — there is no FIN, because there is no
                // TCP — and Chrome only gives up when ICE consent freshness expires, about thirty
                // seconds later. 'disconnected' arrives in a few seconds but is legitimately
                // transient on a mobile link, so it starts a grace timer instead of failing, and
                // only a disconnect still there when it fires counts as the peer being gone.
                let lapse = null;
                const cancelLapse = () => { if (lapse !== null) { clearTimeout(lapse); lapse = null; } };
                pc.onconnectionstatechange = () => {
                    const s = pc.connectionState;
                    console.log('[banter/rtc] connection ' + s);
                    if (s === 'failed' || s === 'closed') { cancelLapse(); rtcFail('connection ' + s); return; }
                    if (s === 'disconnected') {
                        cancelLapse();
                        lapse = setTimeout(() => {
                            if (pc.connectionState === 'disconnected') rtcFail('connection lost');
                        }, 5000);
                        return;
                    }
                    if (s === 'connected') cancelLapse();
                };

                pc.createOffer()
                    .then(offer => pc.setLocalDescription(offer))
                    .then(() => pc.setRemoteDescription({
                        type: 'answer',
                        sdp: answerFrom(host, port, ufrag, password, fpAlg, fpHex),
                    }))
                    .catch(rtcFail);
            } catch (e) {
                rtcFail(e);
            }
        },
        rtcState: () => rtc.state,
        rtcError: () => rtc.error,
        rtcSend: bytes => {
            if (!rtc.ch || rtc.ch.readyState !== 'open') return false;
            try {
                // Copied: the wasm heap can move under us and send() is asynchronous.
                rtc.ch.send(bytes.slice());
                return true;
            } catch (e) {
                rtcFail(e);
                return false;
            }
        },
        rtcClose: () => { rtcTeardown(); rtc.state = 3; },
    });

    // A link left by a node that was told to seed us (the server's --seed-file). Absent in a
    // normal deployment, where the person pastes a link — so a miss is silence, not an error.
    //
    // Read once here for the first paint, then kept looking for in the background: under a
    // "server + client" launch the two start together, and the browser regularly wins. Polling
    // rather than blocking, so a deployment with no seed at all is not made to wait for one.
    let seed = '';
    const readSeed = async () => {
        try {
            const res = await fetch('seed.json', { cache: 'no-store' });
            if (!res.ok) return '';
            return (await res.json()).link || '';
        } catch {
            return '';
        }
    };
    seed = await readSeed();

    const config = getConfig();
    const exports = await getAssemblyExports(config.mainAssemblyName);
    const I = exports.Interop;

    // Backing-store pixels, not CSS pixels: on a HiDPI display the two differ, and sizing the
    // canvas in CSS pixels would render the whole UI soft.
    function sizeCanvas() {
        const dpr = window.devicePixelRatio || 1;
        canvas.width = Math.max(1, Math.round(canvas.clientWidth * dpr));
        canvas.height = Math.max(1, Math.round(canvas.clientHeight * dpr));
    }
    sizeCanvas();
    window.addEventListener('resize', sizeCanvas);

    // Pointer coordinates in the same space the canvas is rendered in.
    function at(e) {
        const r = canvas.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        return [(e.clientX - r.left) * dpr, (e.clientY - r.top) * dpr];
    }

    let live = false; // no exported call is legal until the runtime is running

    canvas.addEventListener('pointerdown', e => {
        if (!live) return;
        const [x, y] = at(e);
        I.PointerDown(x, y, e.detail || 1);
        focusKbd();          // clicking the canvas must not steal focus from the keyboard textarea
        e.preventDefault();
    });
    canvas.addEventListener('pointermove', e => { if (live) { const [x, y] = at(e); I.PointerMove(x, y); } });
    canvas.addEventListener('pointerup', e => { if (live) { const [x, y] = at(e); I.PointerUp(x, y); } });
    canvas.addEventListener('wheel', e => {
        if (!live) return;
        const [x, y] = at(e);
        I.Wheel(x, y, e.deltaY);
        e.preventDefault();
    }, { passive: false });

    // A fallback table only until the runtime hands over its own; the engine's ordinals are the
    // truth and replace these the moment it is live.
    let EK = { Backspace: 1, Delete: 2, ArrowLeft: 3, ArrowRight: 4, Home: 5, End: 6, Enter: 7 };
    const MOD_SHIFT = 1, MOD_CTRL = 2, MOD_ALT = 4;

    kbd.addEventListener('keydown', e => {
        if (!live) return;
        const mods = (e.shiftKey ? MOD_SHIFT : 0) | ((e.ctrlKey || e.metaKey) ? MOD_CTRL : 0) | (e.altKey ? MOD_ALT : 0);

        // Ctrl/Cmd chords first, and only swallowed when the app actually claimed them — otherwise
        // the browser's own Ctrl+R, Ctrl+T and friends would silently stop working.
        if ((e.ctrlKey || e.metaKey) && e.key.length === 1) {
            if (I.KeyChord(e.key, mods)) e.preventDefault();
            return;
        }

        const name = (e.key === 'Tab' && e.shiftKey) ? 'ShiftTab' : e.key;
        if (name in EK) { I.EditKeyPress(EK[name], mods); e.preventDefault(); return; }
        if (e.key.length === 1) { I.KeyChar(e.key); e.preventDefault(); }
    });

    kbd.addEventListener('copy', e => { const t = I.CopySelection(); if (t) { e.clipboardData.setData('text/plain', t); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener('cut', e => { const t = I.CutSelection(); if (t) { e.clipboardData.setData('text/plain', t); e.preventDefault(); } keepSelected(); });
    kbd.addEventListener('paste', e => { const t = e.clipboardData.getData('text/plain'); e.preventDefault(); if (t) I.KeyChar(t); keepSelected(); });

    // runMain, not dotnet.run(): run() exits after Main and every later [JSExport] call would fail
    // against a dead runtime.
    logBoot('runMain...');
    await runMain();
    logBoot('runMain ok');
    live = true;
    EK = JSON.parse(I.EditKeyMap());
    focusKbd();
    window.addEventListener('focus', focusKbd);

    I.Init();
    logBoot('Init ok');

    // The seed was not there when the page loaded, so watch for the node writing one. Offered to
    // the connect screen, which declines it if the person has started typing their own.
    if (!seed) {
        (async () => {
            for (let i = 0; i < 60 && !seed; i++) {
                await new Promise(r => setTimeout(r, 500));
                seed = await readSeed();
                if (seed) { logBoot('seed arrived'); I.SeedArrived(seed); }
            }
        })();
    }

    let firstTick = true;
    function frame(now) {
        try { I.Tick(canvas.width, canvas.height, now); if (firstTick) { firstTick = false; logBoot('Tick ok'); } }
        catch (err) { showError('Tick', err); return; }
        requestAnimationFrame(frame);
    }
    requestAnimationFrame(frame);
} catch (err) {
    showError('boot', err);
}
