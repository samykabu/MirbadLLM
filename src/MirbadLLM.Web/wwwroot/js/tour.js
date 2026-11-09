// The guided tour and one-time page tips. Plain DOM, no framework: the layout is statically rendered, so the tour
// lives here beside shell.js. Steps point at real elements by their data-tour name; a step whose element is not on
// the page (an unenrolled fleet has no node list, a fleet with no models has no Start button) is skipped, never
// faked. Progress and "seen" flags are per-browser conveniences in localStorage; nothing here is required for the
// app to work, and every storage access is guarded.
(function () {
    'use strict';

    var DONE_KEY = 'mirbadllm-tour';
    var TIP_KEY = 'mirbadllm-tips';

    // route: where the step's element lives ('' = any page). anchors: tried in order, first visible wins.
    var STEPS = [
        {
            route: '',
            anchors: ['[data-tour="nav"]', '.bottomnav'],
            title: 'Find your way by job',
            body: 'Overview shows the whole fleet at a glance, live. Run holds Models, Playground and Access. Fleet holds Nodes and the live node tree. Admin holds backup and restore, and releases.'
        },
        {
            route: '',
            anchors: ['[data-tour="rail-nodes"]', '[data-testid="status-readouts"]'],
            title: 'Every node, live',
            body: 'Each machine in the fleet with its state. Green is online, amber degraded, red not serving. Open one to see its memory, models and services.'
        },
        {
            route: '/',
            anchors: ['[data-tour="overview-headroom"]'],
            title: 'Memory headroom per node',
            body: 'Memory is the scarce resource. The bar splits what MirbadLLM models hold, what other processes use, the reserve kept free (hatched), and what is left. "Can be given to a new model" is the number admission checks before a start.'
        },
        {
            route: '/',
            anchors: ['[data-tour="overview-service-level"]'],
            title: 'Service level at a glance',
            body: 'Availability (Ready of the models that should run), the error budget left and time to first token against its target, each with a word for where it stands. Beside the Signal chart, the fleet counts requests served, tokens today and its crash-free streak.'
        },
        {
            route: '/models',
            anchors: ['[data-tour="model-start"]', '[data-tour="models-list"]'],
            title: 'Run a model',
            body: 'Models is the one place to operate them: state, node, runtime and budget on every row, Start and Stop in place. A vLLM start takes 2 to 5 minutes; the timer shows where it is.'
        },
        {
            route: '/models',
            anchors: ['[data-tour="add-model"]'],
            title: 'Add a model',
            body: 'Pick the node first, then the checkpoint. The fit check refuses a budget that does not fit the node or GPU, and says by how much.'
        },
        {
            route: '/nodes',
            anchors: ['[data-tour="nodes-add"]'],
            title: 'Grow the fleet',
            body: 'Add node gives you a one-line install command for a Linux box or a Windows PC. A new node waits for your approval before it takes models.'
        },
        {
            route: '/access',
            anchors: ['[data-tour="access-base-url"]'],
            title: 'One endpoint for every model',
            body: 'Clients use this OpenAI-compatible URL and send a served name as the model. Copy a ready-made config for your client here, and create API keys in the next tab.'
        },
        {
            route: '',
            anchors: ['[data-tour-start]'],
            title: 'That is the tour',
            body: 'Replay it any time from here. Press ? for keyboard shortcuts: g then m opens Models, g then n Nodes, g then k Access.'
        }
    ];

    var TIPS = {
        tune: 'New to tuning? Pick a goal below: the search finds a faster setting and never applies anything without asking. The time it takes is shown under the goals.',
        overview: 'Pick the time window here and a node on the scope cards; select a leaderboard row for its full readout. History is kept for one hour; a "—" means the node does not report that value.'
    };

    function read(key) {
        try { return localStorage.getItem(key); } catch (e) { return null; }
    }

    function write(key, value) {
        try { localStorage.setItem(key, value); } catch (e) { /* storage unavailable: the tour still works for this view */ }
    }

    function visible(el) {
        if (!el) { return false; }
        var r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0) { return false; }
        var style = getComputedStyle(el);
        return style.visibility !== 'hidden' && style.display !== 'none';
    }

    function findAnchor(step) {
        for (var i = 0; i < step.anchors.length; i++) {
            var list = document.querySelectorAll(step.anchors[i]);
            for (var j = 0; j < list.length; j++) {
                if (visible(list[j])) { return list[j]; }
            }
        }
        return null;
    }

    function currentPath() {
        var base = document.baseURI ? new URL(document.baseURI).pathname : '/';
        var path = location.pathname;
        if (base !== '/' && path.indexOf(base) === 0) { path = '/' + path.slice(base.length); }
        return path.replace(/\/+$/, '') || '/';
    }

    function navigate(route) {
        var target = route.replace(/^\//, '');
        if (window.Blazor && typeof window.Blazor.navigateTo === 'function') {
            window.Blazor.navigateTo(target);
        } else {
            location.href = target || '.';
        }
    }

    function waitFor(step, timeoutMs) {
        return new Promise(function (resolve) {
            var started = Date.now();
            (function poll() {
                var el = findAnchor(step);
                if (el) { resolve(el); return; }
                if (Date.now() - started > timeoutMs) { resolve(null); return; }
                setTimeout(poll, 120);
            })();
        });
    }

    function tourRoot() {
        return document.getElementById('tour-root') || document.body;
    }

    var layer = null;
    var index = -1;
    var restoreFocus = null;

    function build() {
        layer = document.createElement('div');
        layer.className = 'tour';
        layer.setAttribute('data-testid', 'tour');
        layer.innerHTML =
            '<div class="tour-spot" aria-hidden="true"></div>' +
            '<div class="tour-card" role="dialog" aria-modal="false" aria-labelledby="tour-title" aria-describedby="tour-body" tabindex="-1">' +
            '  <h2 class="tour-title" id="tour-title"></h2>' +
            '  <p class="tour-body" id="tour-body"></p>' +
            '  <div class="tour-actions">' +
            '    <span class="tour-count" data-testid="tour-count"></span>' +
            '    <button type="button" class="tour-skip" data-tour-skip>Skip tour</button>' +
            '    <button type="button" class="btn btn-sm btn-ghost" data-tour-back>Back</button>' +
            '    <button type="button" class="btn btn-sm btn-primary" data-tour-next>Next</button>' +
            '  </div>' +
            '</div>';
        tourRoot().appendChild(layer);
        layer.querySelector('[data-tour-skip]').addEventListener('click', function () { end(true); });
        layer.querySelector('[data-tour-back]').addEventListener('click', function () { go(index - 1, -1); });
        layer.querySelector('[data-tour-next]').addEventListener('click', function () { go(index + 1, 1); });
        layer.querySelector('.tour-spot').addEventListener('click', function () { /* the dimmed page is inert while touring */ });
    }

    function place(el) {
        var spot = layer.querySelector('.tour-spot');
        var card = layer.querySelector('.tour-card');
        var r = el.getBoundingClientRect();
        var pad = 6;
        spot.style.left = (r.left - pad) + 'px';
        spot.style.top = (r.top - pad) + 'px';
        spot.style.width = (r.width + pad * 2) + 'px';
        spot.style.height = (r.height + pad * 2) + 'px';

        var vw = document.documentElement.clientWidth;
        var vh = document.documentElement.clientHeight;
        var cw = Math.min(360, vw - 32);
        card.style.width = cw + 'px';
        var ch = card.offsetHeight || 180;
        var top;
        var left;
        if (r.right + 16 + cw <= vw - 16 && r.width < vw * 0.5) {
            left = r.right + 16;
            top = Math.min(Math.max(16, r.top), vh - ch - 16);
        } else if (r.bottom + 16 + ch <= vh - 16) {
            top = r.bottom + 16;
            left = Math.min(Math.max(16, r.left), vw - cw - 16);
        } else if (r.top - 16 - ch >= 16) {
            top = r.top - 16 - ch;
            left = Math.min(Math.max(16, r.left), vw - cw - 16);
        } else {
            top = vh - ch - 16;
            left = (vw - cw) / 2;
        }
        card.style.left = Math.round(left) + 'px';
        card.style.top = Math.round(top) + 'px';
    }

    function render(el) {
        var step = STEPS[index];
        layer.querySelector('.tour-count').textContent = (index + 1) + ' / ' + STEPS.length;
        layer.querySelector('.tour-title').textContent = step.title;
        layer.querySelector('.tour-body').textContent = step.body;
        layer.querySelector('[data-tour-back]').disabled = index === 0;
        layer.querySelector('[data-tour-next]').textContent = index === STEPS.length - 1 ? 'Finish' : 'Next';
        var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: reduce ? 'auto' : 'smooth' });
        setTimeout(function () {
            if (!layer) { return; }
            place(el);
            layer.classList.add('is-on');
            layer.querySelector('.tour-card').focus({ preventScroll: true });
            track(el);
        }, reduce ? 0 : 260);
    }

    // Keep the spotlight on its element while a smooth scroll settles or a live update shifts the layout: one rAF loop
    // that re-places only when the element's box actually moved, stopped when the step or the tour ends.
    var tracking = 0;
    function track(el) {
        var id = ++tracking;
        var last = '';
        (function frame() {
            if (!layer || id !== tracking) { return; }
            var r = el.getBoundingClientRect();
            var key = Math.round(r.left) + ',' + Math.round(r.top) + ',' + Math.round(r.width) + ',' + Math.round(r.height);
            if (key !== last) { last = key; place(el); }
            requestAnimationFrame(frame);
        })();
    }

    function go(next, direction) {
        if (next >= STEPS.length) { end(false); return; }
        if (next < 0) { return; }
        index = next;
        var step = STEPS[index];
        var route = step.route;
        var nav = route && currentPath() !== route ? (navigate(route), true) : false;
        layer.classList.remove('is-on');
        tracking++;
        waitFor(step, nav ? 6000 : 400).then(function (el) {
            if (!layer || index !== next) { return; }
            if (!el) { go(next + direction, direction); return; } // not on this fleet: skip, never fake
            render(el);
        });
    }

    function onKey(e) {
        if (!layer) { return; }
        if (e.key === 'Escape') { e.preventDefault(); end(true); }
        else if (e.key === 'ArrowRight') { e.preventDefault(); go(index + 1, 1); }
        else if (e.key === 'ArrowLeft') { e.preventDefault(); go(index - 1, -1); }
    }

    function onResize() {
        if (!layer || index < 0) { return; }
        var el = findAnchor(STEPS[index]);
        if (el) { place(el); }
    }

    function start() {
        if (layer) { return; }
        document.querySelectorAll('[data-nav-close]').forEach(function (b) { b.click(); }); // close the mobile drawer
        restoreFocus = document.activeElement;
        build();
        window.addEventListener('keydown', onKey, true);
        window.addEventListener('resize', onResize);
        window.addEventListener('scroll', onResize, true);
        go(0, 1);
    }

    function end(skipped) {
        write(DONE_KEY, skipped ? 'skipped' : 'done');
        window.removeEventListener('keydown', onKey, true);
        window.removeEventListener('resize', onResize);
        window.removeEventListener('scroll', onResize, true);
        tracking++;
        if (layer) { layer.remove(); layer = null; }
        index = -1;
        if (restoreFocus && typeof restoreFocus.focus === 'function') { restoreFocus.focus(); }
    }

    // One-time tips: a page that carries an element with data-tip="<key>" shows a dismissible note the first time it
    // is opened. The note is appended to <body>, never inside Blazor-rendered markup, so it cannot disturb the
    // renderer's DOM diffing; it leaves with the page.
    var tipEl = null;

    function showTips() {
        if (layer) { return; }
        var seen = {};
        try { seen = JSON.parse(read(TIP_KEY) || '{}') || {}; } catch (e) { seen = {}; }
        var host = null;
        document.querySelectorAll('[data-tip]').forEach(function (h) {
            var k = h.getAttribute('data-tip');
            if (!host && TIPS[k] && !seen[k]) { host = h; }
        });
        var key = host ? host.getAttribute('data-tip') : null;
        if (tipEl && tipEl.getAttribute('data-key') !== key) { tipEl.remove(); tipEl = null; }
        if (!key || tipEl) { return; }
        tipEl = document.createElement('div');
        tipEl.className = 'tip';
        tipEl.setAttribute('role', 'note');
        tipEl.setAttribute('data-key', key);
        tipEl.setAttribute('data-testid', 'tip-' + key);
        tipEl.innerHTML = '<span class="tip-text"></span><button type="button" class="btn btn-sm btn-outline-secondary tip-close">Got it</button>';
        tipEl.querySelector('.tip-text').textContent = TIPS[key];
        tipEl.querySelector('.tip-close').addEventListener('click', function () {
            seen[key] = true;
            write(TIP_KEY, JSON.stringify(seen));
            tipEl.remove();
            tipEl = null;
        });
        (document.getElementById('tip-slot') || tourRoot()).appendChild(tipEl);
    }

    document.addEventListener('click', function (e) {
        var trigger = e.target.closest && e.target.closest('[data-tour-start]');
        if (trigger && !trigger.closest('.tour')) { e.preventDefault(); start(); }
    });

    function onPage() {
        showTips();
        if (!read(DONE_KEY) && !layer && currentPath() === '/' && !document.querySelector('.login-page')) {
            setTimeout(function () { if (!layer && !read(DONE_KEY)) { start(); } }, 900);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', onPage);
    } else {
        onPage();
    }

    function hookEnhancedLoad() {
        if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
            window.Blazor.addEventListener('enhancedload', onPage);
            return true;
        }
        return false;
    }

    if (!hookEnhancedLoad()) {
        var tries = 0;
        var timer = setInterval(function () { if (hookEnhancedLoad() || ++tries > 50) { clearInterval(timer); } }, 200);
    }

    // Interactive islands render after load; pick up tip hosts that appear later.
    new MutationObserver(function () { showTips(); }).observe(document.documentElement, { childList: true, subtree: true });

    window.mirbadllmTour = { start: start, reset: function () { write(DONE_KEY, ''); } };
})();
