// Injected into web.stremio.com by the Stremio-XBOX shell before any page script runs.
// 1. Forwards console errors and uncaught errors to the native debug log.
// 2. Watches for Stremio's player route (#/player/<stream>/...), decodes the stream
//    and hands its HTTP(S) URL to the native Xbox player.
(function () {
    'use strict';

    if (window.__stremioXboxShell) {
        return;
    }
    window.__stremioXboxShell = true;

    var host = window.chrome && window.chrome.webview;
    var nativePlayerEnabled = true;

    // Stremio Web treats any page with window.chrome.webview as running inside Stremio's own
    // desktop shell (stremio-web/src/common/Platform/shell/useShell.ts: active = !!chrome.webview)
    // and then plays through the shell's mpv over that bridge. This app is not that shell, so the
    // web player failed with "Stream is not supported". Keep our reference and hide the bridge.
    var bridgeHidden = false;
    if (host) {
        try {
            Object.defineProperty(window.chrome, 'webview', { configurable: true, get: function () { return undefined; } });
            bridgeHidden = window.chrome.webview === undefined;
        } catch (e) {
            bridgeHidden = false;
        }
        if (!bridgeHidden) {
            try {
                var replacement = {};
                Object.getOwnPropertyNames(window.chrome).forEach(function (key) {
                    if (key !== 'webview') {
                        replacement[key] = window.chrome[key];
                    }
                });
                Object.defineProperty(window, 'chrome', { configurable: true, writable: true, value: replacement });
                bridgeHidden = !(window.chrome && window.chrome.webview);
            } catch (e) {
                bridgeHidden = false;
            }
        }
    }
    var lastHandledStream = null;
    // Where the viewer was before Stremio's player page, to return there when that page stays behind the
    // native player (see leaveWebPlayer).
    var lastPageHash = '#/';
    var leftStream = null; // The stream whose player page leaveWebPlayer left, and when (a bounce back is ignored).
    var leftAt = 0;

    // One source of controller input. Stremio Web has its own gamepad support (Settings > Gamepad support):
    // it polls navigator.getGamepads(), moves its own focus with the left stick and clicks with A. The app
    // already reads the controller natively and moves its own highlight, so with both, two items were
    // highlighted (Stremio's white focus and the app's purple one), they scrolled out of step, and A could
    // act on the one the viewer was not looking at. Inside the app the page sees no gamepad at all.
    var gamepadHidden = false;
    if (host) {
        try {
            var noGamepads = function () { return []; };
            Object.defineProperty(Navigator.prototype, 'getGamepads', { configurable: true, writable: true, value: noGamepads });
            Object.defineProperty(navigator, 'getGamepads', { configurable: true, writable: true, value: noGamepads });
            gamepadHidden = navigator.getGamepads().length === 0;
        } catch (e) {
            gamepadHidden = false;
        }
        ['gamepadconnected', 'gamepaddisconnected'].forEach(function (type) {
            window.addEventListener(type, function (event) { event.stopImmediatePropagation(); }, true);
        });
    }

    function post(message) {
        try {
            if (host) {
                host.postMessage(JSON.stringify(message));
            }
        } catch (e) {
            // Nothing useful to do if the bridge itself fails.
        }
    }

    function format(value) {
        if (value instanceof Error) {
            return value.stack || value.message;
        }
        if (value !== null && typeof value === 'object') {
            try {
                return JSON.stringify(value);
            } catch (e) {
                return String(value);
            }
        }
        return String(value);
    }

    function log(level, text) {
        post({ type: 'log', level: level, text: String(text).slice(0, 2000) });
    }

    ['error', 'warn'].forEach(function (level) {
        var original = console[level];
        console[level] = function () {
            try {
                log(level, Array.prototype.map.call(arguments, format).join(' '));
            } catch (e) {
                // Never break the page's own logging.
            }
            return original.apply(this, arguments);
        };
    });

    window.addEventListener('error', function (event) {
        log('error', 'Uncaught: ' + (event.message || '') + ' @ ' + (event.filename || '') + ':' + (event.lineno || ''));
    });

    window.addEventListener('unhandledrejection', function (event) {
        log('error', 'Unhandled rejection: ' + format(event.reason));
    });

    if (host) {
        host.addEventListener('message', function (event) {
            try {
                var message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
                if (message && message.type === 'config') {
                    nativePlayerEnabled = message.nativePlayer !== false;
                    keyboardNavigation = message.keyboardNav === true;
                    log('info', 'Native player ' + (nativePlayerEnabled ? 'enabled' : 'disabled') + ', keyboard navigation ' + (keyboardNavigation ? 'on' : 'off'));
                } else if (message && message.type === 'nav') {
                    onNavigate(message.action);
                } else if (message && message.type === 'refocusField') {
                    refocusField(message.id);
                } else if (message && message.type === 'player') {
                    onNativePlayer(message);
                }
            } catch (e) {
                log('error', 'Bad config message: ' + format(e));
            }
        });
    }

    // ---- Controller navigation ----------------------------------------------------------
    // The app reads the controller natively and sends {type:'nav', action}. This moves a
    // highlight between focusable elements by screen position (spatial navigation).
    var FOCUSABLE = 'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"]):not([data-focus-guard])';

    // An open popup or dropdown, which navigation stays inside (as in Stremio Web's own gamepad code: a
    // non-empty .modals-container, else an element whose classes contain "dropdown" and "open").
    function openOverlay() {
        var options = document.getElementById('__sx-menu');
        if (options) {
            return { kind: 'options', root: options };
        }
        var modals = document.querySelectorAll('.modals-container');
        for (var i = 0; i < modals.length; i++) {
            if (modals[i].children.length > 0) {
                return { kind: 'modal', root: modals[i] };
            }
        }
        var dropdown = document.querySelector('[class*="dropdown"][class*="open"]');
        return dropdown ? { kind: 'dropdown', root: dropdown } : null;
    }

    // The button that opens a dropdown: next to the open list, inside the same menu element.
    function dropdownToggle(dropdown) {
        var menu = dropdown && dropdown.parentElement;
        if (!menu) {
            return null;
        }
        return Array.prototype.filter.call(menu.querySelectorAll(FOCUSABLE), function (el) {
            return !dropdown.contains(el);
        })[0] || null;
    }

    // B with a popup or dropdown open closes it (instead of leaving the page behind it).
    function closeOverlay() {
        var overlay = openOverlay();
        if (!overlay) {
            return false;
        }
        if (overlay.kind === 'options') {
            closeOptions(true);
        } else if (overlay.kind === 'modal') {
            // Stremio's modals close on an Escape keydown at window level.
            window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', keyCode: 27, bubbles: true }));
        } else {
            var toggle = dropdownToggle(overlay.root);
            if (toggle) {
                toggle.click(); // Escape does not close Stremio's dropdowns; their button does.
                setTimeout(function () {
                    if (toggle.isConnected && isCandidate(toggle)) {
                        setFocus(toggle);
                    }
                }, 50);
            }
        }
        return true;
    }
    var FOCUS_CLASS = '__sx-focus';
    var nativeNavigation = !!host;
    var keyboardNavigation = false; // PC (not Xbox): arrows / Enter / Esc drive the same navigation.
    var lastKeyLog = 0;
    var ANIM_CLASS = '__sx-anim';

    function isTyping(element) {
        return !!element && (element.tagName === 'INPUT' || element.tagName === 'TEXTAREA' || element.isContentEditable);
    }

    function isCandidate(element) {
        if (element.disabled || element.getAttribute('aria-hidden') === 'true') {
            return false;
        }
        if (typeof element.checkVisibility === 'function' &&
            !element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) {
            return false;
        }
        var rect = element.getBoundingClientRect();
        return rect.width >= 4 && rect.height >= 4;
    }

    // The highlighted element. Text fields are highlighted without focusing them, because focusing a field
    // makes WebView2 open the Xbox keyboard; it opens on A instead.
    var highlighted = null;

    function current() {
        if (highlighted && highlighted.isConnected && isCandidate(highlighted)) {
            return highlighted;
        }
        // Stremio redraws rows as catalogs load and update, replacing the highlighted poster with a new copy.
        // Starting over from the top-left (the search bar) made the highlight jump up while scrolling; pick
        // up the copy (same link) or whatever now sits where the highlight was.
        if (highlighted && lastSpot) {
            var replacement = findReplacement();
            if (replacement) {
                log('info', 'Highlight re-attached after the page redrew: ' + describeElement(replacement));
                markHighlight(replacement);
                return replacement;
            }
        }
        var active = document.activeElement;
        return active && active !== document.body && active !== document.documentElement && isCandidate(active) ? active : null;
    }

    // Where the highlight was last seen: its link, its centre on screen and the scroll position then.
    var lastSpot = null;

    function rememberSpot(element) {
        var r = element.getBoundingClientRect();
        var container = scrollParent(element, 'y');
        lastSpot = {
            href: element.getAttribute('href') || '',
            x: r.left + r.width / 2,
            y: r.top + r.height / 2,
            container: container,
            scroll: container ? container.scrollTop : 0,
            scope: openOverlay() ? openOverlay().root : document
        };
    }

    function findReplacement() {
        var scope = lastSpot.scope && lastSpot.scope.isConnected ? lastSpot.scope : document;
        var candidates = Array.prototype.filter.call(scope.querySelectorAll(FOCUSABLE), isCandidate);
        if (lastSpot.href) {
            var same = candidates.filter(function (el) { return el.getAttribute('href') === lastSpot.href; });
            if (same.length) {
                return same[0];
            }
        }
        var shift = lastSpot.container && lastSpot.container.isConnected ? lastSpot.container.scrollTop - lastSpot.scroll : 0;
        var x = lastSpot.x;
        var y = lastSpot.y - shift;
        var best = null;
        var bestDistance = 260; // Only something close by; otherwise start fresh.
        candidates.forEach(function (el) {
            var r = el.getBoundingClientRect();
            var d = Math.sqrt(Math.pow(r.left + r.width / 2 - x, 2) + Math.pow(r.top + r.height / 2 - y, 2));
            if (d < bestDistance) {
                bestDistance = d;
                best = el;
            }
        });
        return best;
    }

    // Lower is better: distance along the direction plus a penalty for sideways offset.
    function score(from, to, direction) {
        var fx = from.left + from.width / 2;
        var fy = from.top + from.height / 2;
        var tx = to.left + to.width / 2;
        var ty = to.top + to.height / 2;
        var primary;
        var secondary;
        switch (direction) {
            case 'up':
                if (to.bottom > from.top + 1) { return Infinity; }
                primary = from.top - to.bottom;
                secondary = Math.max(0, Math.max(from.left, to.left) < Math.min(from.right, to.right) ? 0 : Math.abs(tx - fx));
                break;
            case 'down':
                if (to.top < from.bottom - 1) { return Infinity; }
                primary = to.top - from.bottom;
                secondary = Math.max(0, Math.max(from.left, to.left) < Math.min(from.right, to.right) ? 0 : Math.abs(tx - fx));
                break;
            case 'left':
                if (to.right > from.left + 1) { return Infinity; }
                primary = from.left - to.right;
                secondary = Math.abs(ty - fy);
                break;
            default:
                if (to.left < from.right - 1) { return Infinity; }
                primary = to.left - from.right;
                secondary = Math.abs(ty - fy);
                break;
        }
        return primary + secondary * 2;
    }

    // Last highlighted element per route, so going back returns to the same tile instead of the top.
    var focusByRoute = {};

    // Home is "", "#" or "#/" depending on how it was reached (app start, a link, the step back after playback).
    function routeKey(hash) {
        return !hash || hash === '#' || hash === '#/' ? '#/' : hash;
    }

    // For the app's remote control (testing): highlight the first item whose link or text contains the text.
    window.__sxFocus = function (text) {
        var found = Array.prototype.filter.call(document.querySelectorAll(FOCUSABLE), function (el) {
            return isCandidate(el) && ((el.getAttribute('href') || '').indexOf(text) >= 0 || (el.textContent || '').indexOf(text) >= 0);
        })[0];
        if (!found) {
            return 'not found: ' + text;
        }
        nativeNavigation = true;
        setFocus(found, 'center');
        return describeElement(found);
    };

    function describeForRestore(element) {
        return {
            href: element.getAttribute('href') || '',
            text: (element.textContent || element.getAttribute('aria-label') || '').trim().slice(0, 80)
        };
    }

    function restoreFocus(route) {
        var saved = focusByRoute[routeKey(route)];
        if (!saved && nativeNavigation && /^#\/detail\//.test(route)) {
            focusFirstStream(route); // First visit: start on the first stream, as console apps do.
            return;
        }
        if (!saved && nativeNavigation && routeKey(route) === '#/') {
            focusFirstPoster(route); // Home opens on the first poster (Continue Watching), not the search bar.
            return;
        }
        if (!saved || !nativeNavigation) {
            return;
        }
        var tries = 0;
        (function attempt() {
            if (routeKey(location.hash) !== routeKey(route)) {
                return;
            }
            var found = null;
            if (saved.href) {
                found = Array.prototype.filter.call(document.querySelectorAll('a[href]'), function (el) {
                    return el.getAttribute('href') === saved.href && isCandidate(el);
                })[0] || null;
            }
            if (!found && saved.text) {
                found = Array.prototype.filter.call(document.querySelectorAll(FOCUSABLE), function (el) {
                    return isCandidate(el) && (el.textContent || el.getAttribute('aria-label') || '').trim().slice(0, 80) === saved.text;
                })[0] || null;
            }
            if (found) {
                setFocus(found, 'center');
            } else if (++tries < (/^#\/detail\//.test(route) ? 80 : 20)) {
                // Stremio renders the page a moment after the route changes; a title's streams come from the
                // addons again when the viewer is back from playback, which can take Torrentio 10 s.
                setTimeout(attempt, 150);
            } else if (/^#\/detail\//.test(route) && !(highlighted && highlighted.isConnected)) {
                focusFirstStream(route); // The stream is gone from the list: start on the first one.
            }
        })();
    }

    // A title's page opens with nothing highlighted, so the first press went to the back arrow. Highlight the
    // first stream that plays here (not the "Buy on ..." links), or the first episode, once Stremio lists them.
    // Same title page: Stremio adds the video id to a movie's detail route (#/detail/movie/tt1 becomes
    // #/detail/movie/tt1/tt1) right after it opens, which ended the wait before any stream arrived.
    function sameDetail(a, b) {
        var key = function (hash) { return String(hash || '').split('/').slice(0, 4).join('/'); };
        return key(a) === key(b);
    }

    // Stremio keeps the page left behind (the board) mounted under the title page, so its poster stays
    // "connected": only a highlight the viewer moved (a different element) ends the wait.
    function focusFirstStream(route) {
        var tries = 0;
        var start = highlighted;
        (function attempt() {
            if (!sameDetail(location.hash, route) || (highlighted && highlighted !== start && highlighted.isConnected && isCandidate(highlighted))) {
                return; // Moved on, or the viewer already moved the highlight.
            }
            // Streams only (not the Trailer button, which also links to the player).
            var streams = Array.prototype.filter.call(document.querySelectorAll('a[href^="#/player/"]'), function (el) {
                return !!el.closest('[class*="stream-container"]') && isCandidate(el);
            });
            var episodes = streams.length ? [] : Array.prototype.filter.call(document.querySelectorAll('[class*="video-container"]'), isCandidate);
            var first = streams[0] || episodes[0];
            if (first) {
                setFocus(first, 'nearest');
            } else if (++tries < 100) {
                setTimeout(attempt, 200); // Streams arrive from the addons later (Torrentio can take 10 s and more).
            }
        })();
    }

    function focusFirstPoster(route) {
        var tries = 0;
        (function attempt() {
            if (routeKey(location.hash) !== routeKey(route) || (highlighted && highlighted.isConnected && document.contains(highlighted))) {
                return; // Moved on, or something is highlighted already.
            }
            var first = Array.prototype.filter.call(document.querySelectorAll('a[class*="meta-item"]'), isCandidate)[0];
            if (first) {
                setFocus(first, 'nearest');
            } else if (++tries < 30) {
                setTimeout(attempt, 200); // The board's rows load a moment later.
            }
        })();
    }

    // vertical: an up/down move; the page glides so the new row sits at the same height on screen, as TV
    // apps do, rather than being left at the edge (where it sat half under Stremio's bottom banner).
    // block: jump straight there instead (restoring the highlight after going back).
    function setFocus(element, block, vertical) {
        markHighlight(element);
        if (block) {
            element.scrollIntoView({ block: block, inline: 'nearest', behavior: 'auto' });
        } else {
            glideTo(element, vertical);
        }
        rememberSpot(element);
        // After the glide settles, make sure nothing (a sticky header, Stremio's bottom banner) covers the item.
        setTimeout(function () { ensureVisible(element); }, GLIDE_SETTLE_MS);
    }

    function markHighlight(element) {
        setShield(true);
        focusByRoute[routeKey(location.hash)] = describeForRestore(element);
        var previous = document.querySelector('.' + FOCUS_CLASS);
        if (previous) {
            previous.classList.remove(FOCUS_CLASS, FOCUS_CLASS + '-zoom');
        }
        element.classList.add(FOCUS_CLASS);
        var clip = clipKind(element);
        element.classList.toggle(FOCUS_CLASS + '-inset', clip === 'row');
        element.classList.toggle(FOCUS_CLASS + '-tight', clip === 'tight');
        if (clip !== 'row' && element.querySelector('img')) {
            // Posters grow a little when highlighted (and shrink back smoothly when left).
            element.classList.add(ANIM_CLASS, FOCUS_CLASS + '-zoom');
        }
        highlighted = element;
        if (isTyping(element)) {
            if (document.activeElement && document.activeElement !== document.body) {
                document.activeElement.blur();
            }
        } else {
            element.focus({ preventScroll: true });
        }
    }

    // ---- Hover shield --------------------------------------------------------------------------------------
    // Stremio lights up a poster under the mouse pointer (a white frame). On the console the pointer stays
    // where it was while the page scrolls under it, so a second, white highlight kept popping up next to the
    // controller's. While the controller is in use a transparent sheet lies over the page and takes the
    // pointer; moving a real mouse (or the app's cursor) removes it.
    var shield = null;

    function setShield(on) {
        try {
            if (on && !shield && document.body) {
                var sheet = document.createElement('div');
                sheet.id = '__sx-shield';
                sheet.style.cssText = 'position:fixed;left:0;top:0;right:0;bottom:0;z-index:2147483647;background:transparent;';
                document.body.appendChild(sheet);
                shield = sheet;
            }
            if (shield) {
                if (on && !shield.isConnected && document.body) {
                    document.body.appendChild(shield);
                }
                shield.style.display = on ? 'block' : 'none';
            }
        } catch (e) {
            log('warn', 'Hover shield unavailable: ' + format(e)); // Navigation works without it.
        }
    }

    window.addEventListener('mousemove', function (event) {
        if (shield && shield.style.display !== 'none' && (event.movementX || event.movementY)) {
            setShield(false);
        }
    }, true);
    document.addEventListener('mousedown', function () {
        setShield(false);
    }, true);

    // The element under a point, looking through the shield.
    function elementAt(x, y) {
        var stack = document.elementsFromPoint ? document.elementsFromPoint(x, y) : [document.elementFromPoint(x, y)];
        for (var i = 0; i < stack.length; i++) {
            if (stack[i] && stack[i] !== shield) {
                return stack[i];
            }
        }
        return null;
    }

    // ---- Gliding scroll ----------------------------------------------------------------------------------
    // Each move moves the page like a spring pulling it to its new position: it starts gently, never
    // overshoots, and a press during a glide only moves the destination while keeping the current speed, so
    // holding a direction scrolls in one continuous motion instead of a series of jolts.
    var ROW_ANCHOR = 0.45;     // Up/down: the highlighted row's middle sits at 45 % of the screen height.
    var EDGE_MARGIN = 56;      // Left/right: scroll only when the item comes within this many px of an edge.
    var SPRING = 260;          // Spring stiffness (critically damped): settles in about 0.35 s.
    var GLIDE_SETTLE_MS = 500;
    var glides = [];
    var externalScrolls = 0;
    var frame = window.requestAnimationFrame ? window.requestAnimationFrame.bind(window) : function (f) { return setTimeout(function () { f(Date.now()); }, 16); };

    function isRoot(node) {
        return node === document.scrollingElement || node === document.documentElement || node === document.body;
    }

    // The nearest ancestor that actually scrolls along the axis ('x' or 'y'), else the page itself.
    function scrollParent(element, axis) {
        for (var node = element.parentElement; node && !isRoot(node); node = node.parentElement) {
            var style = getComputedStyle(node);
            var overflow = axis === 'y' ? style.overflowY : style.overflowX;
            var room = axis === 'y' ? node.scrollHeight - node.clientHeight : node.scrollWidth - node.clientWidth;
            if (room > 1 && (overflow === 'auto' || overflow === 'scroll' || overflow === 'overlay')) {
                return node;
            }
        }
        var root = document.scrollingElement || document.documentElement;
        var rootRoom = axis === 'y' ? root.scrollHeight - root.clientHeight : root.scrollWidth - root.clientWidth;
        return rootRoom > 1 ? root : null;
    }

    function visibleRect(container) {
        if (isRoot(container)) {
            return { top: 0, left: 0, bottom: innerHeight, right: innerWidth, height: innerHeight, width: innerWidth };
        }
        return container.getBoundingClientRect();
    }

    // How far to scroll so the span [start, end] sits nicely in [low, high]: anchored, or just inside the edges.
    function scrollDelta(start, end, low, high, anchored) {
        if (anchored && end - start < (high - low) * 0.8) {
            return (start + end) / 2 - (low + (high - low) * ROW_ANCHOR);
        }
        if (start < low + EDGE_MARGIN) {
            return start - (low + EDGE_MARGIN);
        }
        if (end > high - EDGE_MARGIN) {
            return Math.min(end - (high - EDGE_MARGIN), start - (low + EDGE_MARGIN));
        }
        return 0;
    }

    function glide(container, axis, delta) {
        if (Math.abs(delta) < 1) {
            return;
        }
        var g = glides.filter(function (x) { return x.container === container && x.axis === axis; })[0];
        var now = axis === 'y' ? container.scrollTop : container.scrollLeft;
        var max = axis === 'y' ? container.scrollHeight - container.clientHeight : container.scrollWidth - container.clientWidth;
        if (!g) {
            g = { container: container, axis: axis, pos: now, speed: 0 };
            glides.push(g);
            container.style.scrollBehavior = 'auto'; // A CSS smooth scroll would fight the glide.
        }
        // The element was measured at the current scroll position, so aim from there.
        g.target = Math.max(0, Math.min(max, now + delta));
        if (g.running) {
            return;
        }
        g.running = true;
        g.last = 0;
        frame(function step(time) {
            if (g.cancelled) {
                glides.splice(glides.indexOf(g), 1);
                return;
            }
            var dt = (g.last ? Math.min(64, time - g.last) : 16.7) / 1000;
            g.last = time;
            var actual = axis === 'y' ? container.scrollTop : container.scrollLeft;
            if (Math.abs(actual - g.pos) > 2) {
                // Something else scrolled (the page re-rendered or Stremio scrolled): continue from there.
                if (++externalScrolls <= 20) {
                    log('info', 'Scroll moved outside the glide: ' + Math.round(g.pos) + ' -> ' + actual);
                }
                g.pos = actual;
            }
            // Critically damped spring, in small steps for stability.
            var damping = 2 * Math.sqrt(SPRING);
            for (var steps = Math.max(1, Math.ceil(dt / 0.008)), h = dt / steps, i = 0; i < steps; i++) {
                g.speed += (SPRING * (g.target - g.pos) - damping * g.speed) * h;
                g.pos += g.speed * h;
            }
            if (Math.abs(g.target - g.pos) < 0.5 && Math.abs(g.speed) < 8) {
                g.pos = g.target;
                g.speed = 0;
            }
            if (axis === 'y') {
                container.scrollTop = g.pos;
            } else {
                container.scrollLeft = g.pos;
            }
            if (g.pos === g.target) {
                glides.splice(glides.indexOf(g), 1);
                return;
            }
            frame(step);
        });
    }

    function glideTo(element, vertical) {
        try {
            var r = element.getBoundingClientRect();
            var y = scrollParent(element, 'y');
            if (y) {
                var v = visibleRect(y);
                var dy = scrollDelta(r.top, r.bottom, v.top, v.bottom, vertical);
                glide(y, 'y', dy);
                // A list inside a scrolling page (e.g. the stream list): keep the list itself in view too.
                if (!isRoot(y)) {
                    var outer = scrollParent(y, 'y');
                    if (outer) {
                        var o = visibleRect(outer);
                        glide(outer, 'y', scrollDelta(r.top - dy, r.bottom - dy, o.top, o.bottom, false));
                    }
                }
            }
            var x = scrollParent(element, 'x');
            if (x) {
                var h = visibleRect(x);
                glide(x, 'x', scrollDelta(r.left, r.right, h.left, h.right, false));
            }
        } catch (e) {
            element.scrollIntoView({ block: vertical ? 'center' : 'nearest', inline: 'nearest', behavior: 'auto' });
        }
    }

    // Wheel, touch or mouse: the user scrolls by hand, so stop any glide.
    ['wheel', 'touchstart', 'mousedown'].forEach(function (type) {
        window.addEventListener(type, function () {
            glides.forEach(function (g) { g.cancelled = true; });
        }, { capture: true, passive: true });
    });

    // Whether a ring drawn outside the element would be cut off by the nearest ancestor that clips its
    // content: 'row' (fills most of its width), 'tight' (a small control right against its edge) or null.
    function clipKind(element) {
        var r = element.getBoundingClientRect();
        var node = element.parentElement;
        for (var depth = 0; node && node !== document.body && depth < 12; depth++, node = node.parentElement) {
            var style = getComputedStyle(node);
            if (style.overflowX !== 'visible' || style.overflowY !== 'visible') {
                var c = node.getBoundingClientRect();
                // Full-width rows (dropdown options, list entries) leave no room outside for the ring; a
                // poster that is merely scrolled half out of view does, once it scrolls in.
                if (r.width >= c.width * 0.8) {
                    return 'row';
                }
                // Small controls right against the clipping edge (e.g. "Clear history") get a thin ring inside,
                // except items that show a picture (posters), where it would sit under the image.
                var tight = r.left - c.left < 6 || c.right - r.right < 6 || r.top - c.top < 6 || c.bottom - r.bottom < 6;
                return tight && !element.querySelector('img') ? 'tight' : null;
            }
        }
        return null;
    }

    function describeElement(element) {
        if (!element) {
            return 'none';
        }
        var r = element.getBoundingClientRect();
        return element.tagName.toLowerCase() + (element.className && typeof element.className === 'string' ? '.' + element.className.split(' ')[0] : '') +
            ' "' + (element.textContent || '').trim().slice(0, 30) + '" at ' + Math.round(r.left) + ',' + Math.round(r.top) + ' ' + Math.round(r.width) + 'x' + Math.round(r.height);
    }

    function ensureVisible(element) {
        if (highlighted !== element || !element.isConnected) {
            return;
        }
        var r = element.getBoundingClientRect();
        var y = scrollParent(element, 'y');
        var view = y ? visibleRect(y) : { top: 0, bottom: innerHeight };
        var top = Math.max(r.top, view.top, 0);
        var bottom = Math.min(r.bottom, view.bottom, innerHeight);
        if (bottom - top < Math.min(r.height, 40)) {
            log('info', 'Highlight off screen after the glide, centring: ' + describeElement(element));
            glideTo(element, true);
            return;
        }
        if (!y) {
            return;
        }
        // Something on top of the item (a sticky header, Stremio's bottom banner): move just past it.
        var cx = r.left + r.width / 2;
        var hitTop = elementAt(cx, Math.max(top, 0) + 6);
        var hitBottom = elementAt(cx, Math.min(bottom, innerHeight) - 6);
        var coveredTop = hitTop && !(hitTop === element || element.contains(hitTop) || hitTop.contains(element));
        var coveredBottom = hitBottom && !(hitBottom === element || element.contains(hitBottom) || hitBottom.contains(element));
        var delta = 0;
        if (coveredTop && !coveredBottom) {
            delta = r.top - (hitTop.getBoundingClientRect().bottom + 16);
        } else if (coveredBottom && !coveredTop) {
            delta = r.bottom - (hitBottom.getBoundingClientRect().top - 16);
        }
        if (Math.abs(delta) >= 8 && Math.abs(delta) < view.bottom - view.top) {
            log('info', 'Highlight covered by ' + describeElement(coveredTop ? hitTop : hitBottom) + ', moving ' + Math.round(delta) + ' px');
            glide(y, 'y', delta);
            rememberSpot(element);
        }
    }

        function move(direction) {
        var overlay = openOverlay();
        var scope = overlay ? overlay.root : document;
        var candidates = Array.prototype.filter.call(scope.querySelectorAll(FOCUSABLE), isCandidate);
        if (candidates.length === 0) {
            return;
        }
        var from = current();
        if (from && overlay && !overlay.root.contains(from)) {
            // A popup just opened: start inside it, on its selected item if it marks one.
            var selected = candidates.filter(function (el) { return /selected|checked|active/i.test(String(el.className)) || el.getAttribute('aria-selected') === 'true'; })[0];
            setFocus(selected || candidates[0]);
            return;
        }
        if (!from) {
            // Nothing focused yet: start from the top-left-most visible element.
            var visible = candidates.filter(function (el) {
                var r = el.getBoundingClientRect();
                return r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth;
            });
            visible.sort(function (a, b) {
                var ra = a.getBoundingClientRect();
                var rb = b.getBoundingClientRect();
                return (ra.top - rb.top) || (ra.left - rb.left);
            });
            setFocus(visible[0] || candidates[0]);
            return;
        }
        var fromRect = from.getBoundingClientRect();
        var best = null;
        var bestScore = Infinity;
        candidates.forEach(function (el) {
            if (el === from || from.contains(el) || el.contains(from)) {
                return;
            }
            var s = score(fromRect, el.getBoundingClientRect(), direction);
            if (s < bestScore) {
                bestScore = s;
                best = el;
            }
        });
        if (best && (direction === 'up' || direction === 'down')) {
            best = closerRow(fromRect, best, candidates, direction, from) || best;
        }
        if (direction === 'right' && isPoster(from) && (!best || rowOf(best) !== rowOf(from))) {
            // The end of a row: its See All, the way a TV app ends a row with a "more" tile.
            var row = rowOf(from);
            var seeAll = row && Array.prototype.filter.call(row.querySelectorAll('[class*="see-all"]'), isCandidate)[0];
            best = seeAll || best;
        }
        if (best) {
            setFocus(best, null, direction === 'up' || direction === 'down');
        }
    }

    // TV rows: up / down goes to the next row of posters even when it is short and sits off to one side (a
    // Continue Watching row of 3 under the search bar at the right). The plain score skipped it for a row
    // further away that happened to have a poster underneath. Only posters count, so the side menu and
    // buttons keep the plain behaviour.
    // From a poster, the next row's posters also win over its header link (See All sits above the row, at
    // the right, so down from the last poster of a row landed on it instead of the poster below).
    function closerRow(fromRect, best, candidates, direction, from) {
        var bestRect = best.getBoundingClientRect();
        var down = direction === 'down';
        var overHeader = isPoster(from) && !isPoster(best);
        var row = [];
        candidates.forEach(function (el) {
            if (el === from || el === best || from.contains(el) || el.contains(from) || !isPoster(el)) {
                return;
            }
            var r = el.getBoundingClientRect();
            var middle = r.top + r.height / 2;
            var ahead = down ? r.top >= fromRect.bottom - 1 && (overHeader || middle < bestRect.top) :
                r.bottom <= fromRect.top + 1 && (overHeader || middle > bestRect.bottom);
            if (ahead) {
                row.push({ el: el, r: r, gap: down ? r.top - fromRect.bottom : fromRect.top - r.bottom });
            }
        });
        if (row.length === 0) {
            return null;
        }
        var nearestGap = Math.min.apply(null, row.map(function (c) { return c.gap; }));
        if (overHeader && nearestGap > fromRect.height) {
            return null; // No poster row right there: the link (a button below the last row) stays.
        }
        var fx = fromRect.left + fromRect.width / 2;
        var pick = null;
        row.forEach(function (c) {
            if (c.gap > nearestGap + c.r.height / 2) {
                return; // A row further on.
            }
            var dx = Math.abs(c.r.left + c.r.width / 2 - fx);
            if (!pick || dx < pick.dx) {
                pick = { el: c.el, dx: dx };
            }
        });
        return pick ? pick.el : null;
    }

    function rowOf(element) {
        return element && element.closest ? element.closest('[class*="board-row"]') : null;
    }

    function isPoster(element) {
        return /meta-item/.test(String(element.className)) || !!(element.closest && element.closest('[class*="meta-item"]'));
    }

    // Stremio's web player: Space (keydown + keyup) toggles play/pause, arrows seek.
    function pressKey(key, code, keyCode) {
        var target = document.activeElement || document.body;
        ['keydown', 'keyup'].forEach(function (type) {
            target.dispatchEvent(new KeyboardEvent(type, {
                key: key, code: code, keyCode: keyCode, which: keyCode, bubbles: true, cancelable: true,
            }));
        });
    }

    // Typing: when a Stremio text field gets focus (A on the highlight, a cursor click, or Stremio focusing
    // it itself, e.g. the login page), the app passes focus through a native text box and back, then asks
    // for the field to be focused again; WebView2's own Xbox keyboard then types into it.
    var textFields = {};
    var nextTextField = 0;
    var ignoreFocusUntil = 0;

    function registerField(element) {
        if (!element.__sxField) {
            element.__sxField = ++nextTextField;
        }
        textFields[element.__sxField] = element;
    }

    // Fields focused some other way (Stremio's own autofocus, cursor clicks): the app does its pass only if
    // typing has not been set up yet since the page loaded.
    document.addEventListener('focusin', function (event) {
        var element = event.target;
        if (!isTyping(element) || Date.now() < ignoreFocusUntil) {
            return;
        }
        registerField(element);
        post({ type: 'textFocus', id: element.__sxField });
    }, true);

    function refocusField(id) {
        var element = textFields[id];
        if (!element || !element.isConnected) {
            return;
        }
        ignoreFocusUntil = Date.now() + 800;
        element.focus();
    }

    function onNavigate(action) {
        nativeNavigation = true;
        document.documentElement.classList.add('__sx-nav');
        setShield(true);
        if (action === 'menu') {
            if (document.getElementById('__sx-menu')) {
                closeOptions(true);
            } else if (!/^#\/player\//.test(location.hash)) {
                openOptions(current());
            }
            return;
        }
        if (/^#\/player\//.test(location.hash)) {
            if (action === 'activate') {
                pressKey(' ', 'Space', 32);
                return;
            }
            if (action === 'left') {
                pressKey('ArrowLeft', 'ArrowLeft', 37);
                return;
            }
            if (action === 'right') {
                pressKey('ArrowRight', 'ArrowRight', 39);
                return;
            }
        }
        var from = current();
        if (action === 'activate') {
            if (from) {
                if (isTyping(from)) {
                    // Ask the app first, so its focus pass happens before the field (and the keyboard) opens;
                    // the app answers with refocusField.
                    registerField(from);
                    post({ type: 'typingStart', id: from.__sxField });
                    return;
                }
                if (/^#\/settings/.test(location.hash)) {
                    log('info', 'Settings activate: ' + describeElement(from));
                }
                if (resumeFromContinueWatching(from)) {
                    return;
                }
                var dropdown = openOverlay();
                var toggle = dropdown && dropdown.kind === 'dropdown' && dropdown.root.contains(from) ? dropdownToggle(dropdown.root) : null;
                from.click();
                if (toggle) {
                    setTimeout(function () {
                        if (toggle.isConnected && isCandidate(toggle)) {
                            setFocus(toggle);
                        }
                    }, 150);
                }
            } else {
                move('down');
            }
            return;
        }
        if (isTyping(from) && (action === 'left' || action === 'right')) {
            return; // Let the caret move inside text fields.
        }
        move(action);
    }

    // A on a Continue Watching poster resumes it, as console streaming apps do, with the stream Stremio kept
    // for it (its own "player" link); the loading screen offers X to start from the beginning instead. Its
    // poster link only opened the episode's stream list. Without a kept stream, the link opens as before.
    function inContinueWatching(element) {
        if (/^#\/continue-watching/.test(location.hash)) {
            return isPoster(element);
        }
        var row = isPoster(element) && element.closest('[class*="board-row"], [class*="meta-row"]');
        var heading = row && row.querySelector('[class*="title"], [class*="header"]');
        return !!heading && /continue watching/i.test(heading.textContent || '');
    }

    function resumeFromContinueWatching(from) {
        var title = inContinueWatching(from) ? titleOf(from) : null;
        if (!title) {
            return false;
        }
        lookUpTitle(title.id).then(function (info) {
            if (info.playLink) {
                log('info', 'Continue Watching: resuming "' + ((info.libraryItem && info.libraryItem.name) || title.id) + '" with its last stream');
                location.hash = info.playLink.replace(/^#?/, '#');
            } else if (from.isConnected) {
                from.click();
            }
        });
        return true;
    }

    // ---- Title options (Menu button) ---------------------------------------------------------------------
    // Menu on a poster opens a card with what Stremio offers for that title: play / resume, details, add to
    // or remove from the library, mark as watched, remove from Continue Watching. The actions are Stremio's
    // own (the same core calls its poster menus and detail page make), so the library stays in sync.
    var optionsReturn = null;
    // "Play from Beginning": the video to start at 0:00 the next time it plays (once, for 2 minutes).
    var startOver = null;

    function titleOf(element) {
        var link = element && (element.closest('a[href]') || element.querySelector('a[href]'));
        var href = link ? link.getAttribute('href') || '' : '';
        var m = /^#\/detail\/([^/?]+)\/([^/?]+)(?:\/([^/?]+))?/.exec(href);
        if (m) {
            return { type: decodeURIComponent(m[1]), id: decodeURIComponent(m[2]), videoId: m[3] ? decodeURIComponent(m[3]) : null, href: href };
        }
        m = /^#\/player\/[^/]+\/[^/]+\/[^/]+\/([^/?]+)\/([^/?]+)(?:\/([^/?]+))?/.exec(href);
        if (m) {
            return { type: decodeURIComponent(m[1]), id: decodeURIComponent(m[2]), videoId: m[3] ? decodeURIComponent(m[3]) : null, href: href, player: href };
        }
        return null;
    }

    // Looks through Stremio's models for this title: its library entry (with progress) and a preview of it
    // (what "add to library" needs), and whether it is in Continue Watching.
    function lookUpTitle(id) {
        var models = ['continue_watching_preview', 'continue_watching', 'library', 'board', 'discover', 'search', 'meta_details'];
        return Promise.all(models.map(function (model) {
            return coreCall(function (core) { return core.getState(model); }, null);
        })).then(function (states) {
            // Library entries come in three shapes: a full one (meta_details: state with timeOffset, video_id),
            // a Continue Watching one (progress in percent, state with only videoId) and the Library page's
            // summary (progress, watched, no state). Gather what each tells, preferring the full entry.
            var found = { libraryItem: null, preview: null, continueWatching: false, playLink: null, full: null, entry: null,
                progress: 0, watched: null, videoId: null };
            states.forEach(function (state, index) {
                (function walk(value, depth) {
                    if (!value || typeof value !== 'object' || depth > 7) {
                        return;
                    }
                    if (Array.isArray(value)) {
                        value.forEach(function (v) { walk(v, depth + 1); });
                        return;
                    }
                    if (value._id === id && (value.state || index <= 2)) {
                        found.entry = found.entry || value;
                        if (value.state && typeof value.state.timeOffset === 'number') {
                            found.full = found.full || value;
                        }
                        if (index <= 1) {
                            found.continueWatching = true;
                        }
                        if (typeof value.progress === 'number' && value.progress > found.progress) {
                            found.progress = value.progress;
                        }
                        if (typeof value.watched === 'boolean') {
                            found.watched = value.watched;
                        }
                        var video = value.state && (value.state.video_id || value.state.videoId);
                        if (video && !found.videoId) {
                            found.videoId = video;
                        }
                        if (!found.playLink && value.deepLinks && typeof value.deepLinks.player === 'string') {
                            found.playLink = value.deepLinks.player;
                        }
                    } else if (value.id === id && typeof value.type === 'string' && typeof value.name === 'string' && !found.preview) {
                        found.preview = value;
                    }
                    for (var key in value) {
                        if (Object.prototype.hasOwnProperty.call(value, key) && key !== 'addon' && key !== 'manifest') {
                            walk(value[key], depth + 1);
                        }
                    }
                })(state, 0);
            });
            var entry = found.full || found.entry;
            if (entry) {
                var full = found.full && found.full.state;
                found.libraryItem = {
                    _id: entry._id, type: entry.type, name: entry.name, poster: entry.poster,
                    removed: !!entry.removed, temp: !!entry.temp,
                    state: {
                        timeOffset: full ? full.timeOffset : 0,
                        video_id: (full && full.video_id) || found.videoId,
                        timesWatched: full ? full.timesWatched : (found.watched ? 1 : 0),
                        flaggedWatched: full ? full.flaggedWatched : (found.watched ? 1 : 0)
                    }
                };
                // Saved progress, from any shape: a resume time, or a percentage (Continue Watching, Library).
                found.hasProgress = (full && full.timeOffset > 0) || found.progress > 0;
            }
            return found;
        });
    }

    function libraryAction(action, args) {
        return coreDispatch({ action: 'Ctx', args: { action: action, args: args } });
    }

    function openOptions(from) {
        var title = titleOf(from);
        if (!title) {
            log('info', 'Menu: no title options for ' + describeElement(from));
            return;
        }
        optionsReturn = from;
        lookUpTitle(title.id).then(function (info) {
            if (!document.body || document.getElementById('__sx-menu')) {
                return;
            }
            var item = info.libraryItem;
            var inLibrary = !!item && !item.removed && !item.temp;
            var watched = !!item && item.state && (item.state.timesWatched > 0 || item.state.flaggedWatched === 1);
            var name = (item && item.name) || (info.preview && info.preview.name) || (from.textContent || '').trim() || 'This title';
            var poster = (item && item.poster) || (info.preview && info.preview.poster) || '';
            var choices = [];
            var resume = info.playLink || title.player;
            var progress = info.hasProgress ? (item.state.timeOffset || 0) : 0;
            var hasProgress = !!info.hasProgress;
            if (resume) {
                choices.push({ label: info.continueWatching || hasProgress ? 'Resume' : 'Play', run: function () { location.hash = resume.replace(/^#?/, '#'); } });
                if (hasProgress) {
                    choices.push({ label: 'Play from Beginning', run: function () {
                        startOver = { videos: [item.state.video_id, title.videoId, title.id].filter(Boolean), until: Date.now() + 120000 };
                        log('info', 'Play from Beginning: ' + startOver.videos.join(' / ') + (progress ? ' (saved at ' + Math.round(progress / 1000) + ' s)' : ''));
                        location.hash = resume.replace(/^#?/, '#');
                    } });
                }
            }
            choices.push({ label: 'Details', run: function () { location.hash = title.href.replace(/^#?/, '#'); } });
            if (inLibrary) {
                choices.push({ label: 'Remove from Library', run: function () { return libraryAction('RemoveFromLibrary', title.id); } });
            } else if (info.preview || item) {
                choices.push({ label: 'Add to Library', run: function () { return libraryAction('AddToLibrary', info.preview || item); } });
            }
            if (item) {
                choices.push({ label: watched ? 'Mark as unwatched' : 'Mark as watched', run: function () { return libraryAction('LibraryItemMarkAsWatched', { id: title.id, is_watched: !watched }); } });
            }
            if (info.continueWatching) {
                choices.push({ label: 'Remove from Continue Watching', run: function () {
                    return Promise.all([libraryAction('RewindLibraryItem', title.id), libraryAction('DismissNotificationItem', title.id)]);
                } });
            }
            log('info', 'Menu for "' + name + '" (' + title.id + '): ' + choices.map(function (c) { return c.label; }).join(', '));
            showOptions(name, poster, choices);
        });
    }

    function showOptions(name, poster, choices) {
        var root = document.createElement('div');
        root.id = '__sx-menu';
        var card = document.createElement('div');
        card.className = '__sx-menu-card';
        if (poster) {
            var img = document.createElement('div');
            img.className = '__sx-menu-poster';
            img.style.backgroundImage = 'url("' + String(poster).replace(/"/g, '%22') + '")';
            card.appendChild(img);
        }
        var body = document.createElement('div');
        body.className = '__sx-menu-body';
        var heading = document.createElement('div');
        heading.className = '__sx-menu-title';
        heading.textContent = name;
        body.appendChild(heading);
        choices.forEach(function (choice) {
            var button = document.createElement('button');
            button.className = '__sx-menu-item';
            button.textContent = choice.label;
            button.addEventListener('click', function () {
                var keepFocus = /Library|watched|Continue/.test(choice.label);
                closeOptions(keepFocus);
                Promise.resolve(choice.run()).then(function () {
                    if (keepFocus) {
                        showToast(name + ': ' + choice.label.replace(/^Add to/, 'added to').replace(/^Remove from/, 'removed from').replace(/^Mark as/, 'marked as'));
                    }
                });
            });
            body.appendChild(button);
        });
        var hint = document.createElement('div');
        hint.className = '__sx-menu-hint';
        hint.textContent = 'A  Select     B  Close';
        body.appendChild(hint);
        card.appendChild(body);
        root.appendChild(card);
        document.body.appendChild(root);
        var first = root.querySelector('button');
        if (first) {
            setFocus(first, null, false);
        }
    }

    function closeOptions(restore) {
        var root = document.getElementById('__sx-menu');
        if (root) {
            root.parentNode.removeChild(root);
        }
        var back = optionsReturn;
        optionsReturn = null;
        if (restore && back && back.isConnected && isCandidate(back)) {
            setFocus(back, null, false);
        }
    }

    // A short note at the top of the page (e.g. "added to Library").
    function showToast(text) {
        var toast = document.createElement('div');
        toast.className = '__sx-toast';
        toast.textContent = text;
        document.body.appendChild(toast);
        setTimeout(function () { toast.classList.add('__sx-toast-out'); }, 2200);
        setTimeout(function () { if (toast.parentNode) { toast.parentNode.removeChild(toast); } }, 2700);
    }

    // Raw keys from WebView2 itself: logged (to learn what the Xbox sends) and, once native
    // navigation is active, swallowed so a D-pad press is not handled twice.
    window.addEventListener('keydown', function (event) {
        if (!event.isTrusted) {
            return;
        }
        if (keyboardNavigation && onKeyboardNavigation(event)) {
            return;
        }
        if (Date.now() - lastKeyLog > 2000) {
            lastKeyLog = Date.now();
            log('info', 'Key from WebView2: key=' + event.key + ' code=' + event.code + ' keyCode=' + event.keyCode);
        }
        // Controller B (WebView2 sends keyCode 27 with key "Unidentified", or GamepadB) when the page has
        // focus. Off the home screen it goes back in Stremio's own history; on home it is left as it was.
        if (((event.keyCode === 27 && event.key !== 'Escape') || event.key === 'GamepadB') && (!isHome() || openOverlay()) &&
            !isTyping(document.activeElement)) {
            event.preventDefault();
            event.stopImmediatePropagation();
            window.__sxBack();
            return;
        }
        if (!nativeNavigation) {
            return;
        }
        var typing = isTyping(event.target);
        // On the Xbox, WebView2 hands the controller to the page as keys named "Unidentified" carrying arrow
        // (37-40), Enter (13) or gamepad (195-218, e.g. 207 = Menu) key codes. Matching only key names let
        // them through, so each D-pad press also scrolled the page natively and moved Stremio's own focus:
        // the jumpy scrolling, the sideways bounce and the second highlight. Match by key code instead.
        var code = event.keyCode;
        var isArrow = /^Arrow/.test(event.key) || (code >= 37 && code <= 40);
        var isSideways = event.key === 'ArrowLeft' || event.key === 'ArrowRight' || code === 37 || code === 39;
        var isEnter = event.key === 'Enter' || code === 13;
        var isPad = /^Gamepad/.test(event.key) || (code >= 195 && code <= 218);
        if ((isArrow && !(typing && isSideways)) || (isEnter && !typing) || isPad) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    }, true);

    // The same keys must not reach Stremio on release either (it acts on some keys on keyup).
    window.addEventListener('keyup', function (event) {
        if (!event.isTrusted || !nativeNavigation || keyboardNavigation) {
            return;
        }
        var code = event.keyCode;
        if (isTyping(event.target)) {
            return;
        }
        if ((code >= 37 && code <= 40) || code === 13 || (code >= 195 && code <= 218) || /^(Arrow|Gamepad)/.test(event.key)) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    }, true);

    // PC testing without a controller: the keyboard drives the same navigation as the D-pad.
    function onKeyboardNavigation(event) {
        var typing = isTyping(document.activeElement);
        var actions = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' };
        var action = actions[event.key];
        if (event.key === 'Enter' && !typing) {
            action = 'activate';
        }
        if (!typing && (event.key === 'ContextMenu' || event.key === 'm' || event.key === 'M')) {
            action = 'menu';
        }
        if (typing && (event.key === 'ArrowLeft' || event.key === 'ArrowRight' || event.key === 'Enter')) {
            return false; // Caret and form submit stay with the field.
        }
        if (event.key === 'Escape' || (event.key === 'Backspace' && !typing)) {
            if (typing) {
                document.activeElement.blur();
            } else {
                window.__sxBack();
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            return true;
        }
        if (!action || event.ctrlKey || event.altKey || event.metaKey) {
            return false;
        }
        event.preventDefault();
        event.stopImmediatePropagation();
        onNavigate(action);
        return true;
    }

    // Mouse use clears the controller highlight.
    document.addEventListener('mousedown', function () {
        var highlighted = document.querySelector('.' + FOCUS_CLASS);
        if (highlighted) {
            highlighted.classList.remove(FOCUS_CLASS);
        }
        document.documentElement.classList.remove('__sx-nav'); // Pointer in use: Stremio's own focus styles again.
    }, true);

    function addFocusStyle() {
        var style = document.createElement('style');
        style.textContent = '.' + FOCUS_CLASS + ',.' + FOCUS_CLASS + ':focus,.' + FOCUS_CLASS + ':focus-visible{outline:4px solid #7B5BF5 !important;outline-offset:3px !important;' +
            'box-shadow:0 0 0 8px rgba(123,91,245,.35) !important;border-radius:8px;}' +
            '.' + FOCUS_CLASS + '-inset.' + FOCUS_CLASS + '{outline-offset:-4px !important;box-shadow:inset 0 0 0 10px rgba(123,91,245,.35) !important;}' +
            '.' + FOCUS_CLASS + '-tight.' + FOCUS_CLASS + '{outline-width:2px !important;outline-offset:-2px !important;box-shadow:none !important;}' +
            '#__sx-menu{position:fixed;left:0;top:0;right:0;bottom:0;z-index:2147483646;background:rgba(0,0,0,.62);display:flex;align-items:center;justify-content:center;animation:__sx-fade .18s ease-out;}' +
            '.__sx-menu-card{display:flex;gap:28px;min-width:560px;max-width:760px;padding:28px;border-radius:16px;background:rgba(22,20,28,.96);box-shadow:0 24px 80px rgba(0,0,0,.7);animation:__sx-rise .22s cubic-bezier(.2,.8,.2,1);}' +
            '.__sx-menu-poster{flex:0 0 170px;height:255px;border-radius:10px;background:#222 center/cover no-repeat;}' +
            '.__sx-menu-body{flex:1;display:flex;flex-direction:column;min-width:0;}' +
            '.__sx-menu-title{font-size:28px;font-weight:600;color:#fff;margin:4px 6px 18px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}' +
            '.__sx-menu-item{display:block;width:100%;text-align:left;font:inherit;font-size:21px;color:rgba(255,255,255,.85);background:transparent;border:0;border-radius:8px;padding:12px 16px;margin:2px 0;cursor:pointer;}' +
            '.__sx-menu-item.' + FOCUS_CLASS + '{background:rgba(255,255,255,.14);color:#fff;font-weight:600;}' +
            '.__sx-menu-hint{margin:18px 6px 0;font-size:15px;color:rgba(255,255,255,.5);}' +
            '.__sx-toast{position:fixed;top:40px;left:50%;transform:translateX(-50%);z-index:2147483646;padding:14px 26px;border-radius:10px;background:rgba(22,20,28,.95);color:#fff;font-size:19px;box-shadow:0 10px 40px rgba(0,0,0,.5);animation:__sx-fade .2s ease-out;transition:opacity .4s;}' +
            '.__sx-toast-out{opacity:0;}' +
            '@keyframes __sx-fade{from{opacity:0}to{opacity:1}}' +
            '@keyframes __sx-rise{from{opacity:0;transform:translateY(16px) scale(.98)}to{opacity:1;transform:none}}' +
            // Highlighted posters grow slightly; the transition stays on them so they also shrink back smoothly.
            '.' + ANIM_CLASS + '{transition:transform .22s cubic-bezier(.2,.8,.2,1),outline-offset .22s ease !important;}' +
            '.' + FOCUS_CLASS + '-zoom.' + FOCUS_CLASS + '{transform:scale(1.06) !important;}' +
            // Nothing else shows Stremio's own focus ring while the controller drives the page.
            'html.__sx-nav :focus:not(.' + FOCUS_CLASS + '),html.__sx-nav :focus-visible:not(.' + FOCUS_CLASS + '){outline:none !important;}';
        (document.head || document.documentElement).appendChild(style);
    }

    // stremio-core encodes the stream as: JSON -> zlib -> standard base64 -> percent-encoding.
    function decodeStream(encoded) {
        var binary = atob(decodeURIComponent(encoded));
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        var inflated = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('deflate'));
        return new Response(inflated).text().then(JSON.parse);
    }

    function stopWebPlayer() {
        Array.prototype.forEach.call(document.querySelectorAll('video'), function (video) {
            try {
                video.pause();
                video.removeAttribute('src');
                video.load();
            } catch (e) {
                // Ignore; the element may already be gone.
            }
        });
    }

    // ---- Subtitles from the user's addons --------------------------------------------------
    var SUBTITLE_TIMEOUT_MS = 4000;
    var MAX_SUBTITLES = 20;          // the native player downloads each one when the file opens
    var MAX_PER_LANGUAGE = 3;

    function readProfile() {
        try {
            return JSON.parse(localStorage.getItem('profile'));
        } catch (e) {
            return null;
        }
    }

    function providesSubtitles(manifest, type, id) {
        var resources = (manifest && manifest.resources) || [];
        return resources.some(function (resource) {
            var name = typeof resource === 'string' ? resource : resource.name;
            if (name !== 'subtitles') {
                return false;
            }
            var types = (typeof resource === 'object' && resource.types) || manifest.types || [];
            var prefixes = (typeof resource === 'object' && resource.idPrefixes) || manifest.idPrefixes;
            var typeOk = types.length === 0 || types.indexOf(type) !== -1;
            var idOk = !prefixes || prefixes.length === 0 || prefixes.some(function (prefix) { return id.indexOf(prefix) === 0; });
            return typeOk && idOk;
        });
    }

    function fetchWithTimeout(url, ms) {
        var controller = typeof AbortController === 'function' ? new AbortController() : null;
        var timer = setTimeout(function () { if (controller) { controller.abort(); } }, ms);
        return fetch(url, controller ? { signal: controller.signal } : {}).then(function (response) {
            clearTimeout(timer);
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.json();
        }, function (error) {
            clearTimeout(timer);
            throw error;
        });
    }

    // Returns [{url, lang, label}], subtitles bundled with the stream first, then addon results,
    // with the user's preferred subtitle language (Stremio setting) sorted to the top.
    function collectSubtitles(stream, type, videoId) {
        var list = (stream.subtitles || []).filter(function (sub) { return sub && sub.url; }).map(function (sub) {
            return { url: sub.url, lang: sub.lang || '', label: (sub.lang || '?') + ' (stream)' };
        });
        var profile = readProfile();
        if (!profile || !type || !videoId) {
            log('warn', 'Addon subtitles skipped: ' + (!profile ? 'no saved Stremio profile' : 'no title id in the player route'));
            return Promise.resolve(list);
        }
        var addons = (profile.addons || []).filter(function (addon) {
            return addon && addon.transportUrl && providesSubtitles(addon.manifest, type, videoId);
        });
        var requests = addons.map(function (addon) {
            var base = addon.transportUrl.replace(/\/manifest\.json$/, '');
            var name = (addon.manifest && addon.manifest.name) || 'addon';
            var url = base + '/subtitles/' + encodeURIComponent(type) + '/' + encodeURIComponent(videoId) + '.json';
            return fetchWithTimeout(url, SUBTITLE_TIMEOUT_MS).then(function (result) {
                return (result.subtitles || []).filter(function (sub) { return sub && sub.url; }).map(function (sub) {
                    return { url: sub.url, lang: sub.lang || '', label: (sub.lang || '?') + ' (' + name + ')' };
                });
            }).catch(function (e) {
                log('warn', 'Subtitles from ' + name + ' failed: ' + format(e));
                return [];
            });
        });
        return Promise.all(requests).then(function (results) {
            results.forEach(function (result) { list = list.concat(result); });
            var preferred = profile.settings && profile.settings.subtitlesLanguage;
            // The preferred language first, then English (a foreign title plays with English subtitles),
            // so neither is cut by the limits below. Array.sort is stable, so addon order holds within each.
            var rank = function (sub) { return sub.lang === preferred ? 0 : /^(eng?|english)$/i.test(sub.lang) ? 1 : 2; };
            list.sort(function (a, b) { return rank(a) - rank(b); });
            var perLanguage = {};
            var picked = list.filter(function (sub) {
                perLanguage[sub.lang] = (perLanguage[sub.lang] || 0) + 1;
                return perLanguage[sub.lang] <= MAX_PER_LANGUAGE;
            }).slice(0, MAX_SUBTITLES);
            log('info', 'Subtitles found: ' + list.length + ' from ' + addons.length + ' addon(s)' + (preferred ? ', preferred ' + preferred : '') + '; passing ' + picked.length);
            return picked;
        });
    }

    // ---- Stremio's own player model, driven by the native player -----------------------------
    // Stremio Web exposes its core as window.core. Loading the "player" model with the same
    // arguments the web player uses, then feeding it TimeChanged / PausedChanged / Ended from the
    // native player, gives the native player what the web player gets: resume position, Continue
    // Watching, watched marks, the real title and the next episode (binge watching).
    var PLAYER_DEVICE = 'Stremio-XBOX';
    var CORE_INFO_TIMEOUT_MS = 3500;
    var NEXT_POLL_MS = 5000;
    var session = null;

    function coreAvailable() {
        return !!(window.core && typeof window.core.dispatch === 'function' && typeof window.core.getState === 'function');
    }

    function coreCall(run, fallback) {
        if (!coreAvailable()) {
            return Promise.resolve(fallback);
        }
        try {
            return Promise.resolve(run(window.core)).catch(function (e) {
                log('warn', 'Stremio core call failed: ' + format(e));
                return fallback;
            });
        } catch (e) {
            log('warn', 'Stremio core call failed: ' + format(e));
            return Promise.resolve(fallback);
        }
    }

    function coreDispatch(action, field) {
        return coreCall(function (core) { return core.dispatch(action, field); }, null);
    }

    function playerAction(action, args) {
        return coreDispatch({ action: 'Player', args: args === undefined ? { action: action } : { action: action, args: args } }, 'player');
    }

    function playerState() {
        return coreCall(function (core) { return core.getState('player'); }, null);
    }

    // The title's main country of production (Cinemeta lists it first: "Japan, United States"). The app
    // uses it to tell a foreign-language title, played in its original language with English subtitles.
    var COUNTRY_TIMEOUT_MS = 3000;
    function titleCountry(type, id) {
        if (!/^tt\d+$/.test(id || '') || (type !== 'movie' && type !== 'series')) {
            return Promise.resolve('');
        }
        var url = 'https://v3-cinemeta.strem.io/meta/' + encodeURIComponent(type) + '/' + encodeURIComponent(id) + '.json';
        return fetchWithTimeout(url, COUNTRY_TIMEOUT_MS).then(function (result) {
            var country = result && result.meta && result.meta.country;
            return typeof country === 'string' ? country.split(',')[0].trim() : '';
        }).catch(function (e) {
            log('warn', 'Country of the title unknown: ' + format(e));
            return '';
        });
    }

    function stremioSettings() {
        var fallback = (readProfile() || {}).settings || {};
        return coreCall(function (core) { return core.getState('ctx'); }, null).then(function (ctx) {
            return (ctx && ctx.profile && ctx.profile.settings) || fallback;
        });
    }

    function readyContent(loadable) {
        return loadable && loadable.type === 'Ready' ? loadable.content : null;
    }

    // Route parts: <stream>/<streamTransportUrl>/<metaTransportUrl>/<type>/<id>/<videoId>, built into
    // the same Load arguments as Stremio Web's player.
    function playerLoadArgs(stream, parts) {
        var streamTransportUrl = parts[1] || null;
        var metaTransportUrl = parts[2] || null;
        var type = parts[3] || null;
        var id = parts[4] || null;
        var videoId = parts[5] || null;
        return {
            stream: stream,
            streamRequest: streamTransportUrl && type && videoId ? { base: streamTransportUrl, path: { resource: 'stream', type: type, id: videoId, extra: [] } } : null,
            metaRequest: metaTransportUrl && type && id ? { base: metaTransportUrl, path: { resource: 'meta', type: type, id: id, extra: [] } } : null,
            subtitlesPath: type && videoId ? { resource: 'subtitles', type: type, id: videoId, extra: [] } : null
        };
    }

    function startSession(stream, parts) {
        if (session) {
            endSession(session, null);
        }
        var loadArgs = playerLoadArgs(stream, parts);
        session = {
            id: Date.now(),
            loadArgs: loadArgs,
            videoId: loadArgs.streamRequest ? loadArgs.streamRequest.path.id : null,
            loadRequested: false,
            paused: null,
            next: null,
            timer: null
        };
        return session;
    }

    // The web player unloads the model when it unmounts; load ours only after that.
    function waitUntilLeftPlayer() {
        return new Promise(function (resolve) {
            var tries = 0;
            (function check() {
                if (!/^#\/player\//.test(location.hash) || ++tries > 60) {
                    setTimeout(resolve, 400);
                } else {
                    setTimeout(check, 50);
                }
            })();
        });
    }

    // Resolves with the player state once the title is known (or after a few seconds); never rejects.
    function loadSession(s) {
        if (!coreAvailable()) {
            log('warn', 'Stremio core not found; watch progress will not be saved.');
            return Promise.resolve(null);
        }
        return waitUntilLeftPlayer().then(function () {
            if (s !== session) {
                return null;
            }
            s.loadRequested = true;
            return coreDispatch({ action: 'Load', args: { model: 'Player', args: s.loadArgs } }, 'player').then(function () {
                var started = Date.now();
                return new Promise(function (resolve) {
                    (function poll() {
                        playerState().then(function (state) {
                            var metaSettled = !s.loadArgs.metaRequest || (state && state.metaItem && state.metaItem.type !== 'Loading');
                            if ((state && state.selected && metaSettled) || Date.now() - started > CORE_INFO_TIMEOUT_MS || s !== session) {
                                resolve(state);
                            } else {
                                setTimeout(poll, 200);
                            }
                        });
                    })();
                });
            });
        });
    }

    function describeNext(video) {
        if (!video || !video.deepLinks || !(video.deepLinks.player || video.deepLinks.metaDetailsStreams)) {
            return null;
        }
        var episode = typeof video.season === 'number' && typeof video.episode === 'number' ? 'S' + video.season + ' E' + video.episode : '';
        return [episode, video.title || video.name || ''].filter(Boolean).join(' · ') || 'Next episode';
    }

    // Stremio's intro timing for this video (ms), when it knows it: { from, to } or null.
    function describeIntro(state) {
        var io = state && state.introOutro;
        var intro = io && io.intro;
        if (intro && typeof intro.from === 'number' && typeof intro.to === 'number' && intro.to > intro.from) {
            return { from: intro.from, to: intro.to };
        }
        return null;
    }

    function introKey(intro) {
        return intro ? intro.from + '-' + intro.to : '';
    }

    function describeSession(state, s) {
        var info = { title: '', subtitle: '', background: '', resumeMs: 0, next: null, intro: null };
        if (!state) {
            return info;
        }
        var meta = readyContent(state.metaItem);
        var video = null;
        if (meta && Array.isArray(meta.videos)) {
            video = meta.videos.filter(function (v) { return v && v.id === s.videoId; })[0] || null;
        }
        info.title = (meta && meta.name) || (typeof state.title === 'string' ? state.title : '');
        var series = state.seriesInfo;
        var episode = series && typeof series.season === 'number' && typeof series.episode === 'number' ? 'S' + series.season + ' E' + series.episode : '';
        var videoTitle = video ? (video.title || video.name || '') : '';
        info.subtitle = [episode, videoTitle !== info.title ? videoTitle : ''].filter(Boolean).join(' · ');
        info.background = (meta && (meta.background || meta.poster)) || '';
        // Same rule as Stremio Web: resume only when the library item was last on this very video.
        var item = state.libraryItem;
        if (item && item.state && s.videoId && item.state.video_id === s.videoId && item.state.timeOffset > 0) {
            info.resumeMs = item.state.timeOffset;
        }
        if (startOver && startOver.until > Date.now() && startOver.videos.indexOf(s.videoId) >= 0) {
            log('info', 'Starting from the beginning (Play from Beginning) instead of ' + Math.round(info.resumeMs / 1000) + ' s');
            info.resumeMs = 0;
        }
        if (startOver && (startOver.until <= Date.now() || startOver.videos.indexOf(s.videoId) >= 0)) {
            startOver = null; // Used, or too old to mean this playback.
        }
        info.next = describeNext(state.nextVideo);
        info.intro = describeIntro(state);
        return info;
    }

    // The next episode (and its binge stream) can resolve after playback starts; tell the app when it does.
    function watchNext(s, initial, intro) {
        s.next = initial || null;
        s.intro = introKey(intro);
        s.timer = setInterval(function () {
            if (s !== session) {
                clearInterval(s.timer);
                return;
            }
            playerState().then(function (state) {
                var label = state ? describeNext(state.nextVideo) : null;
                if (s === session && label !== s.next) {
                    s.next = label;
                    post({ type: 'nextInfo', session: s.id, next: label || '' });
                }
                var later = state ? describeIntro(state) : null;
                if (s === session && introKey(later) !== s.intro) {
                    s.intro = introKey(later);
                    post({ type: 'introInfo', session: s.id, introFrom: later ? later.from : 0, introTo: later ? later.to : 0 });
                }
            });
        }, NEXT_POLL_MS);
    }

    function reportTime(s, message) {
        if (!s.loadRequested || !message) {
            return;
        }
        // Times are ms. A zero time (e.g. stopped while still buffering) must not wipe the saved position.
        if (typeof message.time === 'number' && message.time > 0 && typeof message.duration === 'number' && message.duration > 0) {
            playerAction('TimeChanged', { time: Math.round(message.time), duration: Math.round(message.duration), device: PLAYER_DEVICE });
        }
        if (typeof message.paused === 'boolean' && message.paused !== s.paused) {
            s.paused = message.paused;
            playerAction('PausedChanged', { paused: message.paused });
        }
    }

    function endSession(s, message) {
        reportTime(s, message);
        if (s.timer) {
            clearInterval(s.timer);
        }
        if (s.loadRequested) {
            // Unloading makes Stremio save the library item (local storage and, when signed in, the API).
            coreDispatch({ action: 'Unload' }, 'player');
        }
        if (session === s) {
            session = null;
        }
    }

    function navigateTo(link) {
        var hash = link.charAt(0) === '#' ? link : '#' + link.replace(/^stremio:\/\/\/?/, '/');
        log('info', 'Next video: ' + hash.slice(0, 120));
        location.hash = hash;
    }

    // Playback ended (auto) or the viewer asked for the next episode: same choices as Stremio Web.
    // Ended with binge watching on: the next episode's binge stream, else its stream list.
    // Ended with binge watching off: stay where we are. Asked for next: binge stream, else stream list.
    function finishSession(s, message, ended) {
        reportTime(s, message);
        if (ended && s.loadRequested) {
            playerAction('Ended');
        }
        Promise.all([s.loadRequested ? playerState() : Promise.resolve(null), stremioSettings()]).then(function (results) {
            var next = results[0] && results[0].nextVideo;
            var links = (next && next.deepLinks) || {};
            var target = !ended || results[1].bingeWatching !== false ? (links.player || links.metaDetailsStreams || null) : null;
            endSession(s, null);
            if (target) {
                navigateTo(target);
            }
            if (!target || !/^#\/player\//.test(target.charAt(0) === '#' ? target : '#' + target)) {
                post({ type: 'playerIdle' }); // The app leaves its loading screen; a player route posts 'preparing' instead.
            }
        });
    }

    // Events from the native player: {type:'player', event, session, time, duration, paused}.
    // After the native player closes, Stremio redraws the row the video came from (new progress, new order),
    // dropping the highlighted poster with it: nothing was highlighted until a press. Put it back on the same
    // title once the row has settled.
    function rehighlight() {
        if (!nativeNavigation || document.getElementById('__sx-menu') || /^#\/player\//.test(location.hash)) {
            return;
        }
        if (highlighted && highlighted.isConnected && isCandidate(highlighted)) {
            if (!highlighted.classList.contains(FOCUS_CLASS)) {
                markHighlight(highlighted);
            }
            return;
        }
        restoreFocus(location.hash);
    }

    function rehighlightSoon() {
        setTimeout(rehighlight, 700);
        setTimeout(rehighlight, 2000);
    }

    function onNativePlayer(message) {
        var s = session && session.id === message.session ? session : null;
        if (/^(stopped|cancel|failed|ended)$/.test(message.event)) {
            rehighlightSoon();
        }
        switch (message.event) {
            case 'progress':
                if (s) { reportTime(s, message); }
                break;
            case 'stopped':
                if (s) { endSession(s, message); }
                break;
            case 'cancel':
            case 'failed':
                if (s) { endSession(s, null); }
                break;
            case 'ended':
            case 'next':
                if (s) {
                    finishSession(s, message, message.event === 'ended');
                } else {
                    post({ type: 'playerIdle' });
                }
                break;
        }
    }

    // Back for both B paths (page key and the app's system back): Stremio routes with history.pushState,
    // which WebView2's GoBack does not follow, so use the page's history. One press may arrive through
    // both paths; act once.
    var lastBack = 0;
    function isHome() {
        return location.hash === '' || location.hash === '#' || location.hash === '#/';
    }
    // What is on the page, for the app's remote control (scripts\Xbox-Remote.ps1 state).
    window.__sxState = function () {
        var menu = document.getElementById('__sx-menu');
        var focused = current();
        var items = menu ? Array.prototype.slice.call(menu.querySelectorAll('.__sx-menu-item')) : [];
        var toast = document.querySelector('.__sx-toast');
        return {
            route: location.hash,
            focused: describeElement(focused),
            title: titleOf(focused),
            menu: menu ? {
                title: ((menu.querySelector('.__sx-menu-title') || {}).textContent || ''),
                items: items.map(function (item) { return item.textContent; }),
                focused: items.indexOf(focused)
            } : null,
            toast: toast ? toast.textContent : null
        };
    };

    window.__sxBack = function () {
        if (Date.now() - lastBack > 500 && closeOverlay()) {
            lastBack = Date.now();
            return true;
        }
        if (isHome()) {
            return false;
        }
        if (Date.now() - lastBack > 500) {
            lastBack = Date.now();
            history.back();
        }
        return true;
    };

    // Stremio can put its player page in the history twice (starting from Continue Watching), so one step
    // back lands on it again: its own player then loads behind the native one, fails ("Video is not
    // supported") and keeps working in the background, and B after playback showed its empty player. Check
    // once the step back has settled; if the page is still on this stream's player page, go back to where
    // the viewer came from instead.
    function leaveWebPlayer(stream, why) {
        setTimeout(function () {
            var still = /^#\/player\/([^/?]+)/.exec(location.hash);
            if (!still || still[1] !== stream) {
                return;
            }
            stopWebPlayer();
            leftStream = stream;
            leftAt = Date.now();
            log('info', "Still on Stremio's player page after the step back (" + why + '); going to ' + lastPageHash);
            location.replace(location.href.split('#')[0] + (lastPageHash.charAt(0) === '#' ? lastPageHash : '#' + lastPageHash));
        }, 1200);
    }

    function onRouteChanged() {
        post({ type: 'route', home: isHome() });
        restoreFocus(location.hash);
        var match = /^#\/player\/([^/?]+)/.exec(location.hash);
        if (!match) {
            lastHandledStream = null;
            lastPageHash = location.hash || '#/';
            return;
        }
        if (match[1] === lastHandledStream) {
            return;
        }
        if (match[1] === leftStream && Date.now() - leftAt < 10000) {
            stopWebPlayer(); // Stremio sent the page back to a player page just left: never a second playback.
            log('warn', "Stremio returned to the player page just left; ignoring it");
            return;
        }
        lastHandledStream = match[1];

        // #/player/<stream>/<streamTransportUrl>/<metaTransportUrl>/<type>/<id>/<videoId>
        var parts = location.hash.slice('#/player/'.length).split('?')[0].split('/').map(function (part) {
            try { return decodeURIComponent(part); } catch (e) { return part; }
        });
        var type = parts[3] || '';
        var videoId = parts[5] || parts[4] || '';

        decodeStream(match[1]).then(function (stream) {
            log('info', 'Stream: ' + JSON.stringify(stream).slice(0, 1500));

            if (!nativePlayerEnabled) {
                log('info', 'Native player is off; leaving playback to the web player.');
                return;
            }
            if (typeof stream.url !== 'string' || !/^https?:\/\//i.test(stream.url)) {
                log('warn', 'Stream has no http(s) url; leaving it to the web player.');
                return;
            }

            var hints = stream.behaviorHints || {};
            stopWebPlayer();
            var s = startSession(stream, parts);
            // The native player shows its loading screen right away while subtitles and Stremio's info load.
            post({ type: 'preparing', session: s.id, name: stream.name || '' });
            // Leave the web player route so it does not keep loading the same file.
            history.back();
            leaveWebPlayer(match[1], 'native playback started');

            function postPlay(subtitles, info, settings) {
                if (s !== session) {
                    return; // Cancelled (B on the loading screen) or replaced.
                }
                post({
                    type: 'play',
                    session: s.id,
                    url: stream.url,
                    name: stream.name || '',
                    description: stream.description || stream.title || '',
                    filename: hints.filename || '',
                    hasRequestHeaders: !!(hints.proxyHeaders && hints.proxyHeaders.request),
                    subtitles: subtitles,
                    title: info.title,
                    subtitle: info.subtitle,
                    background: info.background,
                    startPosition: info.resumeMs,
                    introFrom: info.intro ? info.intro.from : 0,
                    introTo: info.intro ? info.intro.to : 0,
                    next: info.next || '',
                    binge: settings.bingeWatching !== false,
                    subtitlesLanguage: settings.subtitlesLanguage || '',
                    audioLanguage: settings.audioLanguage || '',
                    country: info.country || ''
                });
                watchNext(s, info.next, info.intro);
            }

            Promise.all([collectSubtitles(stream, type, videoId), loadSession(s), stremioSettings(), titleCountry(type, parts[4] || '')]).then(function (results) {
                var info = describeSession(results[1], s);
                info.country = results[3] || '';
                log('info', 'Session: "' + info.title + '" ' + info.subtitle + ', resume ' + Math.round(info.resumeMs / 1000) + ' s, next ' + (info.next || 'none') + ', country ' + (info.country || 'unknown'));
                postPlay(results[0], info, results[2] || {});
            }).catch(function (e) {
                log('error', 'Preparing playback failed: ' + format(e));
                postPlay([], describeSession(null, s), {});
            });
        }).catch(function (e) {
            log('error', 'Could not decode player stream: ' + format(e));
        });
    }

    // Stremio Web uses React Router's HashRouter, which navigates with history.pushState;
    // that does not fire 'hashchange', so hook the history API as well.
    ['pushState', 'replaceState'].forEach(function (method) {
        var original = history[method];
        history[method] = function () {
            var result = original.apply(this, arguments);
            setTimeout(onRouteChanged, 0);
            return result;
        };
    });
    window.addEventListener('popstate', onRouteChanged);
    window.addEventListener('hashchange', onRouteChanged);
    window.addEventListener('DOMContentLoaded', function () {
        log('info', 'User agent: ' + navigator.userAgent);
        log(bridgeHidden ? 'info' : 'warn', 'WebView2 bridge hidden from Stremio Web: ' + bridgeHidden);
        log(gamepadHidden ? 'info' : 'warn', 'Gamepad hidden from Stremio Web (the app drives navigation): ' + gamepadHidden);
        if (host) {
            document.documentElement.classList.add('__sx-nav');
        }
        addFocusStyle();
        onRouteChanged();
    });
})();
