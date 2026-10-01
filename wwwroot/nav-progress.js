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
// START signals, two of them, because neither Blazor event is guaranteed here:
//   1. blazor:enhancednavigationstart - documented and present in the shipped runtime.
//   2. A capturing click listener on same-origin links - depends on nothing but the DOM, and
//      is the only one of the two that knows WHICH link was clicked, so it supplies the name.
//
// STOP: the authority is the DOM, not a framework event.
//
// The first version of this file stopped only on `blazor:enhancedload` and its comment called
// that signal "already proven here, App.razor has subscribed to it since before this work".
// That was wrong, and it was wrong as REASONING, not as a typo: a subscription is not evidence
// that an event fires. App.razor's theme code also installs a MutationObserver, so the theme
// would keep working whether that event ever fired or not, and I read working theming as proof
// of a firing event. On dev it evidently does not fire - the frame sat on "Loading Mailbox
// Permissions - 1m 27s" across several navigations while the page behind it had loaded
// instantly.
//
// So the stop condition is now something directly observable: the page content actually
// changed. A MutationObserver watches for child nodes being added or removed outside the
// status frame, which is precisely "the new page rendered". The Blazor events are still
// listened for, because if they do fire they are earlier and cheaper - but nothing depends on
// them any more. There is also a hard cap: a readout that cannot be stopped must expire rather
// than stand there lying, which is the failure the owner actually saw.
(function () {
    'use strict';

    var SLOT_ID = 'gp-nav-slot';
    var FRAME_ID = 'gp-frame';
    var NODE_ID = 'gp-nav-live';

    // Give a committed navigation this long to show itself before concluding it was blocked.
    // The href changes before the fetch (see the watchdog below), so this only has to cover
    // the gap between the click and Blazor committing - not the load itself.
    var BLOCKED_NAV_GRACE_MS = 600;

    // Ignore DOM churn in the first moments after the click, so the readout cannot stop itself
    // on the click's own side effects before the new page has had any chance to arrive.
    var CONTENT_SETTLE_MS = 200;

    // A readout that cannot be stopped must expire. Standing there counting up forever is the
    // exact failure this file shipped with, and in a permanent frame it is worse than showing
    // nothing: it is the operator's one trusted surface telling them something false. Five
    // minutes is far longer than any navigation in this app and far shorter than forever.
    var MAX_READOUT_MS = 5 * 60 * 1000;

    var active = false;
    var startedAt = 0;
    var destination = '';
    var ticker = null;
    var hrefAtStart = '';
    var navEventSeen = false;
    var leavingDocument = false;
    var contentWatcher = null;

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
    // True when the mutation touched something that is not part of the status frame. The frame
    // repaints its own elapsed time every second and Blazor re-renders it independently, so
    // without this filter the readout would immediately stop itself.
    function mutatedOutsideTheFrame(records) {
        var frame = document.getElementById(FRAME_ID);
        for (var i = 0; i < records.length; i++) {
            var target = records[i].target;
            if (!target) {
                continue;
            }
            if (frame && (target === frame || frame.contains(target))) {
                continue;
            }
            return true;
        }
        return false;
    }

    function watchForTheNewPage() {
        if (contentWatcher || typeof MutationObserver !== 'function') {
            return;
        }

        contentWatcher = new MutationObserver(function (records) {
            if (!active || Date.now() - startedAt < CONTENT_SETTLE_MS) {
                return;
            }

            // A DOM change alone is NOT proof the new page arrived - the old page is still
            // live and still mutating while we wait. An autocomplete debounce completing
            // after the click (ADIdentityAutocomplete and three siblings each hold their own
            // timer and call StateHasChanged) adds and removes nodes on the page we are
            // leaving, and on its own that would stop the readout while the operator is still
            // waiting. Requiring the URL to have moved first rules that out: Blazor pushes
            // the new URL before it fetches, so href-changed means the navigation committed,
            // and the first content change after THAT is the new page landing.
            if (location.href === hrefAtStart) {
                return;
            }

            if (mutatedOutsideTheFrame(records)) {
                stop();
            }
        });

        // childList only. Attribute changes would fire on the sidebar link gaining its active
        // class the instant the click is handled, and character-data changes would fire on the
        // readout's own ticking text.
        contentWatcher.observe(document.body, { childList: true, subtree: true });
    }

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
        watchForTheNewPage();

        // One second, because the number it prints has one-second resolution. A faster timer
        // would repaint identical text. The same tick re-checks for a refused navigation and
        // enforces the hard cap; a window.confirm blocks timers while it is open, so the first
        // tick after the operator answers it is what clears a cancelled one.
        ticker = window.setInterval(function () {
            abandonIfNavigationNeverHappened();

            if (Date.now() - startedAt > MAX_READOUT_MS) {
                stop();
                return;
            }

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

        if (contentWatcher) {
            contentWatcher.disconnect();
            contentWatcher = null;
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
