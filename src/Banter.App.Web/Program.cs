using Banter.App;
using Banter.App.Web;
using CupriFace.Web;

// The web head. The same CupriApp the desktop and Android heads run, in a browser, on a canvas —
// no Blazor, and no JavaScript of ours except the WebRTC data channel that is Banter's transport.
//
// This used to be ~340 lines of host: the frame loop, damage-rect blitting, pointer and keyboard
// dispatch, cursor, favicon, fonts. All of it now belongs to CupriFace.Web.Mono (CupriFace#73),
// along with the touch recogniser, the ARIA mirror and IME that the hand-carried copy had dropped
// — so the web head is no longer the one head a screen reader cannot use.
WebHost.Run(BanterWeb.Build());
