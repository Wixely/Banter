// Banter's only JavaScript: a WebRTC data channel, linked into the app rather than loaded by it.
//
// Everything else a browser host needs — the frame loop, the canvas blit, pointer and touch and
// keyboard, the ARIA mirror, IME, clipboard — belongs to CupriFace.Web.NativeAot. This is the one
// piece that is Banter's, because it is Banter's transport rather than the engine's.
//
// WHY THIS IS AN EMSCRIPTEN JS LIBRARY AND NOT AN ES MODULE. On the Mono host this was
// `export function connect(...)` and C# reached it with [JSImport]. There is no Mono here, so
// there is no JS interop runtime and nothing to import a module into: the C# side DllImports these
// names and the Emscripten linker binds them at link time (--js-library, see the csproj). The file
// is therefore NOT served to the browser — it is compiled into the wasm module's JS glue.
//
// What that changes, and all it changes:
//   * Arguments are numbers. Strings arrive as UTF-16 pointers into wasm memory (a managed string
//     is null-terminated there, which is what lets UTF16ToString find its end), and byte buffers
//     arrive as a pointer and a length.
//   * Nothing is returned by reference. A string comes back by being copied into a buffer the
//     caller owns, which is why `error` takes one.
//   * Callbacks go the other way through the module's exports rather than as function arguments:
//     `Module._BanterRtcMessage` is the [UnmanagedCallersOnly] export on the C# side, surfaced by
//     UnmanagedEntryPointsAssembly in the csproj.
//
// There is no signalling server and none is needed. The node is ICE-lite and DTLS-passive, and its
// signed link already carries the ICE credentials and DTLS fingerprint it would have put in an
// answer — so the browser offers, and then writes the node's answer itself. The signature is what
// makes that safe: forging the answer would mean forging the link.
//
// The SDP shape is the node's DCEP responder's, not ours. It follows Nodestar's reference client,
// including the channel label and a=setup:passive.

mergeInto(LibraryManager.library, {
    $banterRtc: {
        pc: null,
        ch: null,
        state: 0,
        error: '',
        inbox: [],

        fail(why) {
            banterRtc.error = String((why && why.message) || why);
            banterRtc.state = 2;
            console.error('[banter/rtc]', why);
        },

        teardown() {
            try {
                if (banterRtc.ch) {
                    banterRtc.ch.onmessage = banterRtc.ch.onopen = null;
                    banterRtc.ch.onclose = banterRtc.ch.onerror = null;
                    banterRtc.ch.close();
                }
            } catch { /* already gone */ }
            try {
                if (banterRtc.pc) {
                    banterRtc.pc.onconnectionstatechange = null;
                    banterRtc.pc.oniceconnectionstatechange = null;
                    banterRtc.pc.close();
                }
            } catch { /* as above */ }
            banterRtc.ch = null;
            banterRtc.pc = null;
            // Cleared AFTER detaching, or a racing delivery lands in the next session.
            banterRtc.inbox = [];
        },

        answerFrom(host, port, ufrag, password, fpAlg, fpHex) {
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
                // The node is the DTLS server and never initiates checks; the browser is the
                // client and the controller.
                'a=setup:passive',
                'a=candidate:1 1 udp 2130706431 ' + host + ' ' + port + ' typ host',
                'a=end-of-candidates',
                '',
            ].join('\r\n');
        },
    },

    banter_rtc_connect__deps: ['$banterRtc'],
    banter_rtc_connect: (hostPtr, port, ufragPtr, passwordPtr, algPtr, fingerprintPtr) => {
        const host = UTF16ToString(hostPtr);
        try {
            banterRtc.teardown();
            banterRtc.state = 0;
            banterRtc.error = '';

            const pc = new RTCPeerConnection({ iceServers: [] });
            banterRtc.pc = pc;

            // The browser opens the channel; negotiated:false with id 0 is what the node's DCEP
            // responder expects.
            const ch = pc.createDataChannel('cupri', { ordered: true });
            ch.binaryType = 'arraybuffer';
            banterRtc.ch = ch;

            ch.onopen = () => { banterRtc.state = 1; };
            ch.onclose = () => {
                if (banterRtc.state !== 2) banterRtc.state = 3;
                Module._BanterRtcClosed();
            };
            ch.onerror = e => banterRtc.fail('datachannel: ' + ((e && e.message) || 'error'));
            // Queued, and C# nudged to come and get it. Not handed over directly because there is
            // no marshaller here to carry an array across the boundary — `receive` copies into a
            // buffer C# owns instead, which is one copy and no allocation on this side.
            ch.onmessage = e => {
                banterRtc.inbox.push(new Uint8Array(e.data));
                Module._BanterRtcMessage();
            };

            pc.oniceconnectionstatechange = () => {
                console.log('[banter/rtc] ice ' + pc.iceConnectionState);
                if (pc.iceConnectionState === 'failed') banterRtc.fail('ice failed');
            };

            // Noticing that the far end has GONE is the slow part of WebRTC. A peer that dies
            // without closing leaves readyState 'open' — there is no FIN, because there is no TCP
            // — and the browser only gives up when ICE consent freshness expires, about thirty
            // seconds later. 'disconnected' arrives in a few seconds but is legitimately transient
            // on a mobile link, so it starts a grace timer instead of failing, and only a
            // disconnect still there when it fires counts as the peer being gone.
            let lapse = null;
            const cancelLapse = () => { if (lapse !== null) { clearTimeout(lapse); lapse = null; } };
            pc.onconnectionstatechange = () => {
                const s = pc.connectionState;
                console.log('[banter/rtc] connection ' + s);
                if (s === 'failed' || s === 'closed') { cancelLapse(); banterRtc.fail('connection ' + s); return; }
                if (s === 'disconnected') {
                    cancelLapse();
                    lapse = setTimeout(() => {
                        if (pc.connectionState === 'disconnected') banterRtc.fail('connection lost');
                    }, 5000);
                    return;
                }
                if (s === 'connected') cancelLapse();
            };

            const answer = banterRtc.answerFrom(
                host,
                port,
                UTF16ToString(ufragPtr),
                UTF16ToString(passwordPtr),
                UTF16ToString(algPtr),
                UTF16ToString(fingerprintPtr));

            pc.createOffer()
                .then(offer => pc.setLocalDescription(offer))
                .then(() => pc.setRemoteDescription({ type: 'answer', sdp: answer }))
                .catch(e => banterRtc.fail(e));
        } catch (e) {
            banterRtc.fail(e);
        }
    },

    /** 0 connecting, 1 open, 2 failed, 3 closed. */
    banter_rtc_state__deps: ['$banterRtc'],
    banter_rtc_state: () => banterRtc.state,

    /**
     * Copies the last error into a caller-owned UTF-16 buffer and returns its length in
     * CHARACTERS. Nothing comes back by reference here, so a string has to be handed somewhere.
     */
    banter_rtc_error__deps: ['$banterRtc'],
    banter_rtc_error: (buffer, capacityChars) => {
        const message = banterRtc.error || '';
        const clipped = message.length > capacityChars - 1
            ? message.slice(0, Math.max(0, capacityChars - 1))
            : message;
        stringToUTF16(clipped, buffer, capacityChars * 2);
        return clipped.length;
    },

    /**
     * What SCTP actually agreed a single message may be, or 0 before the association is up — which
     * CupriNet reads as "unknown" and lets pass. Reported rather than assumed: the number is the
     * smaller of what the two ends offered, so it is not ours to predict, and a guess that came
     * out too low would refuse pairings that work.
     */
    banter_rtc_max_message_size__deps: ['$banterRtc'],
    banter_rtc_max_message_size: () => {
        const sctp = banterRtc.pc && banterRtc.pc.sctp;
        return sctp && sctp.maxMessageSize ? sctp.maxMessageSize : 0;
    },

    /**
     * Copies the next queued message into a caller-owned buffer and returns its length: -1 when
     * there is nothing waiting, -2 when the message will not fit (which the caller reports rather
     * than silently truncating).
     */
    banter_rtc_receive__deps: ['$banterRtc'],
    banter_rtc_receive: (buffer, capacity) => {
        if (banterRtc.inbox.length === 0) return -1;
        const message = banterRtc.inbox[0];
        if (message.length > capacity) return -2;
        // A fresh view every time: wasm memory can grow, and a captured HEAPU8 would be stale.
        HEAPU8.set(message, buffer);
        banterRtc.inbox.shift();
        return message.length;
    },

    banter_rtc_send__deps: ['$banterRtc'],
    banter_rtc_send: (message, length) => {
        if (!banterRtc.ch || banterRtc.ch.readyState !== 'open') return 0;
        try {
            // Copied out of the heap: send() is asynchronous and wasm memory can move under it.
            banterRtc.ch.send(HEAPU8.slice(message, message + length));
            return 1;
        } catch (e) {
            banterRtc.fail(e);
            return 0;
        }
    },

    banter_rtc_close__deps: ['$banterRtc'],
    banter_rtc_close: () => { banterRtc.teardown(); banterRtc.state = 3; },

    /**
     * The page's own address, which the head needs to resolve the node's link endpoint against.
     * On Mono this was JSHost.GlobalThis.document.baseURI; there is no GlobalThis here.
     */
    banter_page_base: (buffer, capacityChars) => {
        const base = (document && document.baseURI) || '/';
        const clipped = base.slice(0, Math.max(0, capacityChars - 1));
        stringToUTF16(clipped, buffer, capacityChars * 2);
        return clipped.length;
    },
});
