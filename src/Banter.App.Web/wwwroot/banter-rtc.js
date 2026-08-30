// Banter's only JavaScript: a WebRTC data channel, loaded by the app rather than the page.
//
// Everything else a browser host needs — the frame loop, the canvas blit, pointer and touch and
// keyboard, the ARIA mirror, IME, clipboard — belongs to CupriFace.Web.Mono. This is the one piece
// that is Banter's, because it is Banter's transport rather than the engine's.
//
// There is no signalling server and none is needed. The node is ICE-lite and DTLS-passive, and its
// signed link already carries the ICE credentials and DTLS fingerprint it would have put in an
// answer — so the browser offers, and then writes the node's answer itself. The signature is what
// makes that safe: forging the answer would mean forging the link.
//
// The SDP shape is the node's DCEP responder's, not ours. It follows Nodestar's reference client,
// including the channel label and a=setup:passive.

const rtc = { pc: null, ch: null, state: 0, error: '', inbox: [] };

function fail(why) {
    rtc.error = String((why && why.message) || why);
    rtc.state = 2;
    console.error('[banter/rtc]', why);
}

function teardown() {
    try {
        if (rtc.ch) { rtc.ch.onmessage = rtc.ch.onopen = rtc.ch.onclose = rtc.ch.onerror = null; rtc.ch.close(); }
    } catch { /* already gone */ }
    try {
        if (rtc.pc) { rtc.pc.onconnectionstatechange = rtc.pc.oniceconnectionstatechange = null; rtc.pc.close(); }
    } catch { /* as above */ }
    rtc.ch = null;
    rtc.pc = null;
    rtc.inbox = [];   // cleared AFTER detaching, or a racing delivery lands in the next session
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
        // The node is the DTLS server and never initiates checks; the browser is the client and
        // the controller.
        'a=setup:passive',
        'a=candidate:1 1 udp 2130706431 ' + host + ' ' + port + ' typ host',
        'a=end-of-candidates',
        '',
    ].join('\r\n');
}

export function connect(host, port, ufrag, password, fpAlg, fpHex, onMessage, onClosed) {
    try {
        teardown();
        rtc.state = 0;
        rtc.error = '';

        const pc = new RTCPeerConnection({ iceServers: [] });
        rtc.pc = pc;

        // The browser opens the channel; negotiated:false with id 0 is what the node's DCEP
        // responder expects.
        const ch = pc.createDataChannel('cupri', { ordered: true });
        ch.binaryType = 'arraybuffer';
        rtc.ch = ch;

        ch.onopen = () => { rtc.state = 1; };
        ch.onclose = () => { if (rtc.state !== 2) rtc.state = 3; onClosed(); };
        ch.onerror = e => fail('datachannel: ' + ((e && e.message) || 'error'));
        // The message is queued and C# is nudged to come and get it. It is not handed over
        // directly because a callback cannot carry a byte[] across the boundary — and would
        // marshal element by element if it could. `receive` copies into a buffer C# owns instead.
        ch.onmessage = e => { rtc.inbox.push(new Uint8Array(e.data)); onMessage(); };

        pc.oniceconnectionstatechange = () => {
            console.log('[banter/rtc] ice ' + pc.iceConnectionState);
            if (pc.iceConnectionState === 'failed') fail('ice failed');
        };

        // Noticing that the far end has GONE is the slow part of WebRTC. A peer that dies without
        // closing leaves readyState 'open' — there is no FIN, because there is no TCP — and the
        // browser only gives up when ICE consent freshness expires, about thirty seconds later.
        // 'disconnected' arrives in a few seconds but is legitimately transient on a mobile link,
        // so it starts a grace timer instead of failing, and only a disconnect still there when it
        // fires counts as the peer being gone.
        let lapse = null;
        const cancelLapse = () => { if (lapse !== null) { clearTimeout(lapse); lapse = null; } };
        pc.onconnectionstatechange = () => {
            const s = pc.connectionState;
            console.log('[banter/rtc] connection ' + s);
            if (s === 'failed' || s === 'closed') { cancelLapse(); fail('connection ' + s); return; }
            if (s === 'disconnected') {
                cancelLapse();
                lapse = setTimeout(() => {
                    if (pc.connectionState === 'disconnected') fail('connection lost');
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
            .catch(fail);
    } catch (e) {
        fail(e);
    }
}

/** 0 connecting, 1 open, 2 failed, 3 closed. */
export function state() { return rtc.state; }

export function error() { return rtc.error; }

/**
 * Copies the next queued message into a caller-owned buffer and returns its length: -1 when there
 * is nothing waiting, -2 when the message will not fit (which the caller reports rather than
 * silently truncating).
 */
export function receive(buffer) {
    if (rtc.inbox.length === 0) return -1;
    const message = rtc.inbox[0];
    if (message.length > buffer.length) return -2;
    buffer.set(message);
    rtc.inbox.shift();
    return message.length;
}

export function send(bytes) {
    if (!rtc.ch || rtc.ch.readyState !== 'open') return false;
    try {
        // Copied: the wasm heap can move under us and send() is asynchronous.
        rtc.ch.send(bytes.slice());
        return true;
    } catch (e) {
        fail(e);
        return false;
    }
}

export function close() { teardown(); rtc.state = 3; }
