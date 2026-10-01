// Navigation progress bar.
//
// Why this is plain JS and not a Blazor component: the defect it fixes is that nothing paints
// between clicking a module in the sidebar and that module's page arriving. The app renders
// interactively with prerendering on (Program.cs AddInteractiveServerRenderMode, and no call
// site passes prerender:false), so the server builds the whole page -- including any spinner in
// OnInitializedAsync -- before the browser is sent a single byte. A component cannot show a
// spinner for a load that has not reached the browser yet. Only something already running in
// the page can, which means JS.
//
// The app also uses Blazor enhanced navigation, so the PREVIOUS page stays fully rendered and
// interactive for the whole load. Without this bar there is no visual change at all and the
// operator clicks again. Diagnosed in docs/ServiceHealthLoadFeedback-Plan.md lines 44-66.
//
// Two independent start signals, because only one of them is proven in this app:
//   1. blazor:enhancednavigationstart -- the documented signal, NOT yet confirmed against the
//      installed Blazor version on a dev deploy (docs/GlobalProgressSystem-Plan.md S1).
//   2. A capturing click listener on same-origin links -- the fallback, which depends on
//      nothing but the DOM.
// Both call start(); start() is idempotent. If (1) turns out to fire, the fallback is harmless
// duplication. If it does not, the bar still works. The end signal, blazor:enhancedload, IS
// already proven here: App.razor has subscribed to it for theme reapplication since before this.
(function () {
    'use strict';

    var BAR_ID = 'nav-progress-bar';
    var active = false;
    var bar = null;

    function ensureBar() {
        if (bar && bar.isConnected) {
            return bar;
        }
        bar = document.getElementById(BAR_ID);
        if (!bar) {
            bar = document.createElement('div');
            bar.id = BAR_ID;
            bar.setAttribute('role', 'progressbar');
            bar.setAttribute('aria-label', 'Loading the page');
            // aria-valuenow is deliberately omitted: this bar does not know how far along the
            // load is, and announcing a number it invented is the exact dishonesty the progress
            // rules forbid. An indeterminate progressbar is a valid ARIA state.
            document.body.appendChild(bar);
        }
        return bar;
    }

    function start() {
        if (active) {
            return;
        }
        active = true;
        ensureBar().classList.add('nav-progress-running');
    }

    function stop() {
        if (!active) {
            return;
        }
        active = false;
        ensureBar().classList.remove('nav-progress-running');
    }

    // Enhanced navigation keeps the old page on screen, so the end signal is an event rather
    // than a page unload. This one is proven in this app.
    document.addEventListener('blazor:enhancedload', stop);

    // The documented start signal. Unconfirmed here; harmless if it never fires.
    document.addEventListener('blazor:enhancednavigationstart', start);

    // Fallback start signal. Capturing, so it runs before Blazor's own handler takes the click.
    document.addEventListener('click', function (e) {
        // Honour anything that means "not a plain navigation": modified clicks open a new tab
        // and never replace this page, so showing a loading bar for them would be a lie.
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
        // In-page anchors and script hrefs navigate nowhere.
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

        start();
    }, true);

    // A full (non-enhanced) navigation replaces the document, so the bar goes with it. These two
    // cover the back/forward cache, where the restored page would otherwise keep a stale bar.
    window.addEventListener('pageshow', stop);
    window.addEventListener('pagehide', stop);
})();
