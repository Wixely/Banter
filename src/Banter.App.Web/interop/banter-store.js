// The browser's half of "settings that survive a reload".
//
// The desktop head writes a settings file; a browser has localStorage and nothing else that
// outlives the tab. The rule the file version follows applies here unchanged and matters more,
// because this store is readable by any script on the origin: PREFERENCES ONLY, never a secret.
// No password goes through here, and neither does the server link — a link has a lifetime, and one
// remembered past its node's is the stale-link trap that removing --seed-file got rid of.
//
// An Emscripten JS library rather than an ES module, for the same reason as banter-rtc.js: there
// is no Mono here, so the C# side DllImports these and the linker binds them (--js-library, see
// the csproj). Strings arrive as UTF-16 pointers and come back by being copied into a buffer the
// caller owns.
//
// EVERY CALL IS WRAPPED, and that is not defensive habit. localStorage THROWS rather than failing
// quietly in cases a user is entitled to be in: Firefox and Chrome both throw on access when
// cookies are blocked for the site, Safari's private mode has historically thrown on write with a
// quota error, and an embedded context can refuse it outright. A preference that cannot be saved
// is a preference lost, which is a shrug; an exception crossing this boundary would take the app
// down on boot.

mergeInto(LibraryManager.library, {
    /**
     * Reads one value into a caller-owned UTF-16 buffer, returning its length in CHARACTERS, or -1
     * when there is nothing stored (which is different from an empty string, and the caller cares:
     * "never set" means use the default, "" means the user cleared it).
     */
    banter_store_get: (keyPtr, buffer, capacityChars) => {
        try {
            const value = window.localStorage.getItem(UTF16ToString(keyPtr));
            if (value === null) return -1;
            const clipped = value.slice(0, Math.max(0, capacityChars - 1));
            stringToUTF16(clipped, buffer, capacityChars * 2);
            return clipped.length;
        } catch (e) {
            console.warn('[banter/store] cannot read:', e && e.message);
            return -1;
        }
    },

    banter_store_set: (keyPtr, valuePtr) => {
        try {
            window.localStorage.setItem(UTF16ToString(keyPtr), UTF16ToString(valuePtr));
        } catch (e) {
            // Quota, a private window, or cookies blocked for this origin. The preference is lost
            // and the session carries on with it held in memory, which is what a person who has
            // turned storage off has asked for.
            console.warn('[banter/store] cannot write:', e && e.message);
        }
    },
});
