// Navigation reporting for the bottom status frame.
//
// Why this is plain JS and not part of the Blazor component: while a page is loading, the
// server is busy producing it. The component in the frame cannot re-render to say "loading" -
// the circuit that would render it is the thing we are waiting on. Worse, the app renders with
// prerendering on (Program.cs AddInteractiveServerRenderMode, no call site passes
// prerender:false), so the whole page is built server-side before the browser is sent a byte,
// and Blazor enhanced navigation leaves the PREVIOUS page fully rendered and interactive
// meanwhile. Nothing on screen changes. Only code already running in the browser can report it.
// Diagnosed in docs/ServiceHealthLoadFeedback-Plan.md lines 44-66.
//
// This writes a live readout into #gp-nav-slot, a deliberately empty element the frame renders
// for it: the destination name taken from the clicked link, plus a ticking elapsed time so the
// operator can see it is still moving rather than wedged.
//
// Two independent start signals, because only one of them is proven in this app:
//   1. blazor:enhancednavigationstart - the documented signal. Present in the shipped runtime
//      (verified by reading blazor.web.js on the dev host), but its firing is not observable
//      from this repo.
//   2. A capturing click listener on same-origin links - depends on nothing but the DOM, and
//      it is also the only one of the two that knows WHICH link was clicked, so it is what
//      supplies the destination name.
// The end signal, blazor:enhancedload, is already proven here: App.razor has subscribed to it
// for theme reapplication since before this work.
(function () {
    'use strict';

    var SLOT_ID = 'gp-nav-slot';
    var FRAME_ID = 'gp-frame';
    var NODE_ID = 'gp-nav-live';

    // Give a committed navigation this long to show itself before concluding it was blocked.
    // The href changes before the fetch (see the watchdog below), so this only has to cover
    // the gap between the click and Blazor committing - not the load itself.
    var BLOCKED_NAV_GRACE_MS = 600;

    var active = false;
    var startedAt = 0;
    var destination = '';
    var ticker = null;
    var hrefAtStart = '';
    var navEventSeen = false;
    var leavingDocument = false;

    function host() {
        // The frame renders the slot. If the frame is not on the page (an unauthenticated or
        // error page), fall back to the body so the readout still appears rather than silently
        // doing nothing.
        return document.getElementById(SLOT_ID) || document.getElementById(FRAME_ID) || document.body;
    }

    function node() {
        var existing = document.getElementById(NODE_ID);
        if (existing && existing.isConnected) {
            return existing;
        }

        var created = document.createElement('div');
        created.id = NODE_ID;
        created.className = 'gp-nav-live';
        host().appendChild(created);
        return created;
    }

    function elapsedText() {
        var seconds = Math.floor((Date.now() - startedAt) / 1000);
        if (seconds < 1) {
            return '';
        }
        if (seconds < 60) {
            return ' - ' + seconds + 's';
        }
        return ' - ' + Math.floor(seconds / 60) + 'm ' + (seconds % 60) + 's';
    }

    function paint() {
        if (!active) {
            return;
        }
        node().textContent = 'Loading ' + destination + elapsedText();
    }

    // A click is not a navigation. The app's UnsavedChangesGuard can refuse one
    // (Components/Shared/UnsavedChangesGuard.razor: window.confirm, then
    // context.PreventNavigation), and a refused navigation fires no load event - so without
    // this the frame would read "Loading Module Config - 94s" forever, with a timer still
    // running behind it. A permanent frame telling a standing lie is worse than no frame.
    //
    // The signal is exact rather than a guess. Blazor's enhanced navigation pushes the new URL
    // BEFORE it fetches, and only after the location-changing handlers have approved, so:
    //   - navigation committed  -> location.href has already changed;
    //   - full page navigation  -> beforeunload has fired;
    //   - navigation refused    -> neither, and only then do we stop.
    function abandonIfNavigationNeverHappened() {
        if (!active || navEventSeen || leavingDocument) {
            return;
        }
        if (location.href !== hrefAtStart) {
            return;
        }
        if (Date.now() - startedAt < BLOCKED_NAV_GRACE_MS) {
            return;
        }

        stop();
    }

    function start(name) {
        if (active) {
            return;
        }
        active = true;
        startedAt = Date.now();
        destination = name || 'the page';
        hrefAtStart = location.href;
        navEventSeen = false;

        var frame = document.getElementById(FRAME_ID);
        if (frame) {
            // Takes the frame out of its idle styling for the duration, so the change is
            // visible and not merely additive text.
            frame.classList.add('gp-frame-navigating');
        }

        paint();
        // One second, because the number it prints has one-second resolution. A faster timer
        // would repaint identical text. The same tick re-checks for a refused navigation; a
        // window.confirm blocks timers while it is open, so the first tick after the operator
        // answers it is what clears a cancelled one.
        ticker = window.setInterval(function () {
            abandonIfNavigationNeverHappened();
            paint();
        }, 1000);

        window.setTimeout(abandonIfNavigationNeverHappened, BLOCKED_NAV_GRACE_MS + 50);
    }

    function stop() {
        if (!active) {
            return;
        }
        active = false;

        if (ticker !== null) {
            window.clearInterval(ticker);
            ticker = null;
        }

        var frame = document.getElementById(FRAME_ID);
        if (frame) {
            frame.classList.remove('gp-frame-navigating');
        }

        var live = document.getElementById(NODE_ID);
        if (live && live.parentNode) {
            live.parentNode.removeChild(live);
        }
    }

    // The proven end signal.
    document.addEventListener('blazor:enhancedload', stop);

    // Also an end signal, and the one that fires when an enhanced navigation finishes without
    // replacing the document body.
    document.addEventListener('blazor:enhancednavigationend', stop);

    // The documented start signal. It carries no destination, so it reports generically; the
    // click listener below normally wins the race and supplies the name. Its arrival is also
    // positive proof the navigation was committed rather than refused, which switches the
    // blocked-navigation watchdog off.
    document.addEventListener('blazor:enhancednavigationstart', function () {
        navEventSeen = true;
        start('');
    });

    // A full page navigation is leaving this document, so an unchanged href proves nothing and
    // the watchdog must not fire.
    window.addEventListener('beforeunload', function () { leavingDocument = true; });

    // Fallback start signal, and the one that knows the destination. Capturing, so it runs
    // before Blazor's own handler takes the click.
    document.addEventListener('click', function (e) {
        // Anything that means "not a plain navigation": a modified click opens a new tab and
        // never replaces this page, so reporting a load would be a lie.
        if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) {
            return;
        }

        var anchor = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (!anchor) {
            return;
        }
        if (anchor.target && anchor.target !== '_self') {
            return;
        }
        if (anchor.hasAttribute('download')) {
            return;
        }

        var href = anchor.getAttribute('href') || '';
        if (href === '' || href.charAt(0) === '#' || href.toLowerCase().indexOf('javascript:') === 0) {
            return;
        }

        var url;
        try {
            url = new URL(anchor.href, document.baseURI);
        } catch (err) {
            return;
        }
        if (url.origin !== window.location.origin) {
            return;
        }
        // Same page, different fragment only: no load happens.
        if (url.pathname === window.location.pathname && url.search === window.location.search) {
            return;
        }

        start((anchor.textContent || '').replace(/\s+/g, ' ').trim());
    }, true);

    // A full (non-enhanced) navigation replaces the document, so the readout goes with it.
    // These cover the back/forward cache, where a restored page would keep a stale readout.
    window.addEventListener('pageshow', stop);
    window.addEventListener('pagehide', stop);
})();
