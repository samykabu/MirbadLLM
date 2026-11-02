// App shell behaviour that must work in the statically rendered layout (no Blazor events there):
// theme toggle, keeping the theme across Blazor enhanced navigation, the side-nav drawer on small screens,
// the skip link, keyboard focus for tabs (no server round trip), keyboard shortcuts, and the client-side
// clocks behind "Updated Xs ago" stamps.
(function () {
    'use strict';

    var THEME_KEY = 'mirbadllm-theme';
    var root = document.documentElement;

    function storedTheme() {
        try {
            return localStorage.getItem(THEME_KEY);
        } catch (e) {
            return null;
        }
    }

    function preferredTheme() {
        var stored = storedTheme();
        if (stored === 'light' || stored === 'dark') {
            return stored;
        }

        return window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    }

    // The theme in force for this document; survives storage being unavailable.
    var current = preferredTheme();

    function syncThemeButtons() {
        var dark = root.getAttribute('data-bs-theme') === 'dark';
        document.querySelectorAll('[data-theme-toggle]').forEach(function (button) {
            button.setAttribute('aria-pressed', dark ? 'true' : 'false');
        });
    }

    function applyTheme(theme) {
        current = theme;
        if (root.getAttribute('data-bs-theme') !== theme) {
            root.setAttribute('data-bs-theme', theme);
        }

        syncThemeButtons();
    }

    function toggleTheme() {
        var next = current === 'dark' ? 'light' : 'dark';
        try {
            localStorage.setItem(THEME_KEY, next);
        } catch (e) {
            // Storage unavailable: the theme still switches for this page view.
        }

        applyTheme(next);
    }

    // Enhanced navigation patches <html> to the server's markup, which has no theme attribute. Put it back
    // before the next paint (mutation observers run as microtasks), and again on 'enhancedload'.
    new MutationObserver(function () {
        if (root.getAttribute('data-bs-theme') !== current) {
            root.setAttribute('data-bs-theme', current);
        }
    }).observe(root, { attributes: true, attributeFilter: ['data-bs-theme'] });

    // ---- Drawer ----
    var lastToggle = null;

    function nav() {
        return document.getElementById('sidenav');
    }

    function backdrop() {
        return document.querySelector('[data-nav-backdrop]');
    }

    function isOpen() {
        var el = nav();
        return !!el && el.classList.contains('is-open');
    }

    function setToggles(open) {
        document.querySelectorAll('[data-nav-toggle]').forEach(function (button) {
            button.setAttribute('aria-expanded', open ? 'true' : 'false');
        });
    }

    function openNav(trigger) {
        var el = nav();
        if (!el) {
            return;
        }

        lastToggle = trigger || null;
        el.classList.add('is-open');
        var bd = backdrop();
        if (bd) {
            bd.classList.add('is-open');
        }

        setToggles(true);
        var first = el.querySelector('[data-nav-close]') || el.querySelector('a');
        if (first) {
            first.focus();
        }
    }

    function closeNav(restoreFocus) {
        var el = nav();
        if (!el || !el.classList.contains('is-open')) {
            return;
        }

        el.classList.remove('is-open');
        var bd = backdrop();
        if (bd) {
            bd.classList.remove('is-open');
        }

        setToggles(false);
        if (restoreFocus && lastToggle && document.body.contains(lastToggle)) {
            lastToggle.focus();
        }
    }

    document.addEventListener('click', function (event) {
        var target = event.target instanceof Element ? event.target : null;
        if (!target) {
            return;
        }

        if (target.closest('[data-theme-toggle]')) {
            toggleTheme();
            return;
        }

        var toggle = target.closest('[data-nav-toggle]');
        if (toggle) {
            if (isOpen()) {
                closeNav(true);
            } else {
                openNav(toggle);
            }

            return;
        }

        if (target.closest('[data-nav-close]') || target.closest('[data-nav-backdrop]')) {
            closeNav(true);
            return;
        }

        if (isOpen() && target.closest('#sidenav a')) {
            closeNav(false);
        }
    });

    document.addEventListener('keydown', function (event) {
        if (!isOpen() || document.querySelector('dialog[open]')) {
            return;
        }

        if (event.key === 'Escape') {
            event.preventDefault();
            closeNav(true);
            return;
        }

        // Keep Tab inside the open drawer.
        if (event.key === 'Tab') {
            var focusable = nav().querySelectorAll('a[href], button:not([disabled])');
            if (focusable.length === 0) {
                return;
            }

            var first = focusable[0];
            var last = focusable[focusable.length - 1];
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        }
    });

    function afterNavigation() {
        applyTheme(current);
        closeNav(false);
        var help = document.getElementById('shortcuts-dialog');
        if (help && help.open) {
            help.close();
        }
    }

    applyTheme(current);

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', afterNavigation);
    }

    // ---- Client-local time ----
    // The controller always renders "until ~HH:MM" instants in UTC (see UiFormat.IsoUtc/ClockTimeUtc and
    // Maintenance.Describe): any <time data-fmt="hm" datetime="...Z"> gets its text replaced here with the
    // viewer's own local HH:MM, read from the UTC instant in `datetime`. This runs for the initial paint,
    // after every enhanced (static) navigation, and via a MutationObserver so it also catches interactive
    // Blazor Server components (the maintenance banner, job progress) that patch the DOM over SignalR
    // rather than navigating.
    function formatLocalTimes(root) {
        (root || document).querySelectorAll('time[data-fmt="hm"]').forEach(function (el) {
            var iso = el.getAttribute('datetime');
            if (!iso || el.dataset.fmtDone === iso) {
                return;
            }

            var date = new Date(iso);
            if (isNaN(date.getTime())) {
                return;
            }

            el.textContent = date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
            el.dataset.fmtDone = iso;
        });
    }

    formatLocalTimes(document);

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', function () {
            formatLocalTimes(document);
        });
    }

    if (document.body) {
        new MutationObserver(function () {
            formatLocalTimes(document.body);
        }).observe(document.body, { childList: true, subtree: true, characterData: true });
    }

    // ---- Skip link ----
    // A hash link only scrolls (and Blazor's enhanced navigation intercepts it), so move focus explicitly: the
    // next Tab then continues from the page content, not from the top of the shell. Capture phase, so this
    // runs before Blazor's own link handling.
    document.addEventListener('click', function (event) {
        var link = event.target instanceof Element ? event.target.closest('[data-skip-link]') : null;
        if (!link) {
            return;
        }

        var main = document.getElementById('main');
        if (main) {
            event.preventDefault();
            main.focus();
            main.scrollIntoView({ block: 'start' });
        }
    }, true);

    // ---- Tabs: roving focus in the browser ----
    // Arrow keys / Home / End on a tab in any [data-roving-tabs] list move focus immediately, update the
    // roving tabindex and selection state for an instant visual, then click the tab so Blazor syncs the
    // selection (and the panel) from the server without focus waiting on that round trip.
    document.addEventListener('keydown', function (event) {
        if (event.altKey || event.ctrlKey || event.metaKey) {
            return;
        }

        var tab = event.target instanceof Element ? event.target.closest('[role=tab]') : null;
        var list = tab ? tab.closest('[data-roving-tabs]') : null;
        if (!list) {
            return;
        }

        var tabs = Array.prototype.filter.call(list.querySelectorAll('[role=tab]'), function (t) {
            return !t.disabled && t.closest('[data-roving-tabs]') === list;
        });
        var index = tabs.indexOf(tab);
        if (index < 0 || tabs.length === 0) {
            return;
        }

        var next;
        switch (event.key) {
            case 'ArrowRight':
            case 'ArrowDown':
                next = tabs[(index + 1) % tabs.length];
                break;
            case 'ArrowLeft':
            case 'ArrowUp':
                next = tabs[(index - 1 + tabs.length) % tabs.length];
                break;
            case 'Home':
                next = tabs[0];
                break;
            case 'End':
                next = tabs[tabs.length - 1];
                break;
            default:
                return;
        }

        event.preventDefault();
        if (next === tab) {
            return;
        }

        tabs.forEach(function (t) {
            var on = t === next;
            t.setAttribute('tabindex', on ? '0' : '-1');
            t.setAttribute('aria-selected', on ? 'true' : 'false');
        });
        next.focus();
        next.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        next.click();
    }, true);

    // Edge fades on a tab row that scrolls sideways (phones): which ends still have tabs beyond them.
    function updateTabFades(list) {
        var bar = list.parentElement;
        if (!bar) {
            return;
        }

        var max = list.scrollWidth - list.clientWidth;
        bar.classList.toggle('is-scroll-start', list.scrollLeft > 2);
        bar.classList.toggle('is-scroll-end', max - list.scrollLeft > 2);
    }

    var fadeFrame = 0;
    function scheduleTabFades() {
        if (fadeFrame) {
            return;
        }

        fadeFrame = requestAnimationFrame(function () {
            fadeFrame = 0;
            document.querySelectorAll('.tabs-list').forEach(updateTabFades);
        });
    }

    document.addEventListener('scroll', function (event) {
        if (event.target instanceof Element && event.target.classList.contains('tabs-list')) {
            updateTabFades(event.target);
        }
    }, true);
    window.addEventListener('resize', scheduleTabFades);

    // ---- "Updated Xs ago" ----
    // Live pages stamp when their data was loaded (data-live-at). The age counts on this browser's clock
    // from the moment a new stamp arrived, so it keeps counting (and turns stale) when the server stops
    // sending updates, and a server clock that is off doesn't skew it.
    var lastLiveUpdate = null;

    function noteStamp(el) {
        var iso = el.getAttribute('data-live-at');
        if (iso && el.dataset.seenIso !== iso) {
            el.dataset.seenIso = iso;
            el.dataset.seenAt = String(Date.now());
            lastLiveUpdate = Date.now();
        }
    }

    function formatAge(seconds) {
        if (seconds < 60) {
            return seconds + 's';
        }

        var minutes = Math.floor(seconds / 60);
        return minutes < 60 ? minutes + 'm ' + (seconds % 60) + 's' : Math.floor(minutes / 60) + 'h ' + (minutes % 60) + 'm';
    }

    function updateStamps() {
        var now = Date.now();
        document.querySelectorAll('[data-live-at]').forEach(function (el) {
            noteStamp(el);
            var text = el.querySelector('[data-live-text]');
            if (!text) {
                return;
            }

            var label;
            var stale = false;
            if (!el.getAttribute('data-live-at') || !el.dataset.seenAt) {
                label = 'Loading…';
            } else {
                var age = Math.max(0, Math.round((now - Number(el.dataset.seenAt)) / 1000));
                var interval = parseInt(el.getAttribute('data-live-interval') || '5', 10) || 5;
                stale = age > interval * 2 + 2;
                label = age < 2 ? 'Updated just now' : 'Updated ' + formatAge(age) + ' ago';
                if (stale) {
                    label += ' · not refreshing';
                }
            }

            if (text.textContent !== label) {
                text.textContent = label;
            }

            el.classList.toggle('is-stale', stale);
        });
    }

    window.mirbadllmLastLiveUpdate = function () {
        return lastLiveUpdate;
    };

    var counted = new WeakSet();
    function countUpSummary() {
        document.querySelectorAll('[data-count-up] .tile-number').forEach(function (el) {
            if (counted.has(el)) return;
            counted.add(el);
            var text = el.textContent;
            var value = Number(text.replace(/,/g, ''));
            if (!Number.isFinite(value) || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
            var decimals = (text.split('.')[1] || '').length;
            var started = performance.now();
            el.setAttribute('aria-label', text);
            function frame(now) {
                if (!el.isConnected) return;
                var progress = Math.min(1, (now - started) / 320);
                el.textContent = progress === 1 ? text : (value * progress).toLocaleString('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
                if (progress < 1) requestAnimationFrame(frame);
                else el.removeAttribute("aria-label");
            }
            requestAnimationFrame(frame);
        });
    }
    countUpSummary();

    updateStamps();
    setInterval(updateStamps, 1000);

    if (document.body) {
        new MutationObserver(function (records) {
            for (var i = 0; i < records.length; i++) {
                if (records[i].type === 'attributes' && records[i].target instanceof Element) {
                    noteStamp(records[i].target);
                }
            }

            countUpSummary();
            scheduleTabFades();
        }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-live-at'] });
    }

    scheduleTabFades();

    // ---- Row action menus (Ui/RowMenu.razor) ----
    // A menu button ([data-row-menu-trigger], popovertarget) opens a native popover menu ([data-row-menu],
    // role=menu) in the top layer, so a scrolling table panel never clips it. Everything happens here, in the
    // browser: aria-expanded follows the popover, opening focuses the first item (ArrowUp on the button: the
    // last), arrows / Home / End move between items, Esc and Tab close it and focus returns to the button.
    // Where CSS anchor positioning is unsupported the menu is placed under the button's right edge by hand.
    var anchorSupported = !!(window.CSS && CSS.supports && CSS.supports('position-anchor: --a'));
    var menuFocusLast = false;

    function menuItems(menu) {
        return Array.prototype.filter.call(menu.querySelectorAll('[role=menuitem]'), function (item) {
            return !item.disabled && item.getAttribute('aria-disabled') !== 'true';
        });
    }

    function menuTrigger(menu) {
        return menu.id ? document.querySelector('[data-row-menu-trigger][popovertarget="' + menu.id + '"]') : null;
    }

    function placeMenu(menu, trigger) {
        if (anchorSupported || !trigger || window.matchMedia('(max-width: 639.98px)').matches) {
            return;
        }

        var r = trigger.getBoundingClientRect();
        var width = menu.offsetWidth;
        var height = menu.offsetHeight;
        var left = Math.max(8, Math.min(r.right - width, window.innerWidth - width - 8));
        var top = r.bottom + 4;
        if (top + height > window.innerHeight - 8 && r.top - height - 4 > 8) {
            top = r.top - height - 4;
        }

        menu.style.inset = 'auto';
        menu.style.left = left + 'px';
        menu.style.top = top + 'px';
    }

    document.addEventListener('toggle', function (event) {
        var menu = event.target;
        if (!(menu instanceof Element) || !menu.matches('[data-row-menu]')) {
            return;
        }

        var trigger = menuTrigger(menu);
        var open = event.newState === 'open';
        if (trigger) {
            trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
        }

        if (open) {
            placeMenu(menu, trigger);
            var items = menuItems(menu);
            var first = menuFocusLast ? items[items.length - 1] : items[0];
            menuFocusLast = false;
            if (first) {
                first.focus();
            }
        }
    }, true);

    document.addEventListener('keydown', function (event) {
        var target = event.target instanceof Element ? event.target : null;
        if (!target || event.ctrlKey || event.metaKey || event.altKey) {
            return;
        }

        var trigger = target.closest('[data-row-menu-trigger]');
        if (trigger && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
            var targetMenu = document.getElementById(trigger.getAttribute('popovertarget'));
            if (targetMenu && typeof targetMenu.showPopover === 'function' && !targetMenu.matches(':popover-open')) {
                event.preventDefault();
                menuFocusLast = event.key === 'ArrowUp';
                targetMenu.showPopover();
            }

            return;
        }

        var menu = target.closest('[data-row-menu]');
        if (!menu) {
            return;
        }

        var items = menuItems(menu);
        var index = items.indexOf(target.closest('[role=menuitem]'));
        var next = null;
        switch (event.key) {
            case 'ArrowDown': next = items[(index + 1) % items.length]; break;
            case 'ArrowUp': next = items[(index - 1 + items.length) % items.length]; break;
            case 'Home': next = items[0]; break;
            case 'End': next = items[items.length - 1]; break;
            case 'Escape':
            case 'Tab':
                if (event.key === 'Escape') {
                    event.preventDefault();
                }

                menu.hidePopover();
                var back = menuTrigger(menu);
                if (back) {
                    back.focus();
                }

                return;
            case ' ':
                // Space activates a link item like Enter does.
                if (index >= 0) {
                    event.preventDefault();
                    items[index].click();
                }

                return;
            default:
                return;
        }

        event.preventDefault();
        if (next) {
            next.focus();
        }
    });

    // Picking an item closes the menu (a same-page link would otherwise leave it open).
    document.addEventListener('click', function (event) {
        var item = event.target instanceof Element ? event.target.closest('[data-row-menu] [role=menuitem]') : null;
        var menu = item ? item.closest('[data-row-menu]') : null;
        if (menu && menu.matches(':popover-open')) {
            menu.hidePopover();
        }
    });

    // ---- Keyboard shortcuts (listed in #shortcuts-dialog) ----
    var GO = { d: '', m: 'models', s: 'services', c: 'chat', n: 'nodes', k: 'access', b: 'backup' };
    var pendingG = 0;

    function shortcutsDialog() {
        return document.getElementById('shortcuts-dialog');
    }

    function openShortcuts() {
        var dialog = shortcutsDialog();
        if (dialog && !dialog.open && typeof dialog.showModal === 'function') {
            dialog.showModal();
        }
    }

    function closeShortcuts() {
        var dialog = shortcutsDialog();
        if (dialog && dialog.open) {
            dialog.close();
        }
    }

    function isTyping(el) {
        if (!(el instanceof Element)) {
            return false;
        }

        if (el.isContentEditable) {
            return true;
        }

        var tag = el.tagName;
        if (tag === 'TEXTAREA' || tag === 'SELECT') {
            return true;
        }

        if (tag !== 'INPUT') {
            return false;
        }

        var type = (el.getAttribute('type') || 'text').toLowerCase();
        return ['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file'].indexOf(type) < 0;
    }

    function go(path) {
        var url = new URL(path, document.baseURI).href;
        if (window.Blazor && typeof window.Blazor.navigateTo === 'function') {
            window.Blazor.navigateTo(url);
        } else {
            location.href = url;
        }
    }

    function visible(el) {
        return !!el && el.getClientRects().length > 0;
    }

    // b / t: on a model's pages, press the page's own Run benchmark / Start tuning button (which opens its
    // confirmation dialog); from another page of the same model, open the Benchmark / Tune page first.
    function modelShortcut(kind) {
        var button = document.querySelector('[data-shortcut="' + kind + '"]');
        if (visible(button)) {
            if (!button.disabled) {
                button.click();
            } else {
                button.focus();
            }

            return true;
        }

        var match = /\/models\/([^/?#]+)/.exec(location.pathname);
        if (match && match[1] !== 'new') {
            go('models/' + match[1] + '/' + kind);
            return true;
        }

        return false;
    }

    document.addEventListener('click', function (event) {
        var target = event.target instanceof Element ? event.target : null;
        if (!target) {
            return;
        }

        if (target.closest('[data-shortcuts-open]')) {
            openShortcuts();
        } else if (target.closest('[data-shortcuts-close]')) {
            closeShortcuts();
        } else if (target === shortcutsDialog()) {
            // A click on the backdrop (the dialog element itself, outside its content) closes it.
            closeShortcuts();
        }
    });

    document.addEventListener('keydown', function (event) {
        if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || event.isComposing) {
            return;
        }

        if (isTyping(event.target) || isOpen()) {
            pendingG = 0;
            return;
        }

        var help = shortcutsDialog();
        var openDialog = document.querySelector('dialog[open]');
        var key = event.key;

        if (key === '?') {
            if (openDialog && openDialog !== help) {
                return;
            }

            event.preventDefault();
            if (help && help.open) {
                closeShortcuts();
            } else {
                openShortcuts();
            }

            return;
        }

        // Nothing else while any dialog is open (the confirm dialogs keep their own keyboard).
        if (openDialog) {
            return;
        }

        var lower = key.length === 1 ? key.toLowerCase() : key;
        if (pendingG && Date.now() - pendingG < 1500) {
            pendingG = 0;
            if (Object.prototype.hasOwnProperty.call(GO, lower)) {
                event.preventDefault();
                go(GO[lower]);
            }

            return;
        }

        pendingG = 0;
        if (key === 'g') {
            pendingG = Date.now();
            return;
        }

        if (key === '/') {
            var filters = Array.prototype.filter.call(document.querySelectorAll('[data-shortcut-filter]'), visible);
            if (filters.length > 0) {
                event.preventDefault();
                filters[0].focus();
                if (typeof filters[0].select === 'function') {
                    filters[0].select();
                }
            }

            return;
        }

        if ((key === 'b' || key === 't') && modelShortcut(key === 'b' ? 'benchmark' : 'tune')) {
            event.preventDefault();
        }

        if (key === 'a') {
            event.preventDefault();
            go('models/new');
            return;
        }

        if (key === 'n') {
            // On Chat, start a new conversation; elsewhere open Chat.
            var newChat = document.querySelector('[data-shortcut="new-chat"]');
            event.preventDefault();
            if (visible(newChat) && !newChat.disabled) {
                newChat.click();
            } else {
                go('chat');
            }
        }
    });

    // Keep "system" users in step when the OS theme flips and nothing is stored.
    if (window.matchMedia) {
        var query = window.matchMedia('(prefers-color-scheme: light)');
        var onChange = function () {
            if (!storedTheme()) {
                applyTheme(preferredTheme());
            }
        };

        if (typeof query.addEventListener === 'function') {
            query.addEventListener('change', onChange);
        }
    }
})();
