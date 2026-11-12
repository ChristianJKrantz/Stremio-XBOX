// Runs the real Resources/shell.js in a minimal fake page with a fake WebView2 bridge and a fake
// Stremio core (state shapes copied from the live web.stremio.com player model).
// Usage (Node 18+, from the repo root): node tests/shell-session.test.js
'use strict';
const fs = require('fs');
const vm = require('vm');

const path = require('path');
const source = fs.readFileSync(process.argv[2] || path.join(__dirname, '..', 'StremioXbox', 'Resources', 'shell.js'), 'utf8');

// ---- Fake page: hash history with async popstate/hashchange like a browser ----
const winListeners = {};
function fire(type, event) { event = event || { type: type }; (winListeners[type] || []).forEach(function (f) { f(event); }); return event; }
function keyEvent(key, keyCode) {
    return { type: 'keydown', key: key, keyCode: keyCode, isTrusted: true, target: {}, prevented: false, stopped: false,
        preventDefault: function () { this.prevented = true; }, stopImmediatePropagation: function () { this.stopped = true; } };
}
let stack = [''];
let idx = 0;
function hashOf(url) { const i = String(url).indexOf('#'); return i >= 0 ? String(url).slice(i) : ''; }
const location = {
    get hash() { return stack[idx]; },
    set hash(v) {
        v = String(v);
        v = v.charAt(0) === '#' ? v : '#' + v;
        stack = stack.slice(0, idx + 1); stack.push(v); idx++;
        setTimeout(function () { fire('popstate'); fire('hashchange'); }, 0);
    },
    href: 'https://web.stremio.com/'
};
const history = {
    pushState: function (s, t, url) { stack = stack.slice(0, idx + 1); stack.push(hashOf(url)); idx++; },
    replaceState: function (s, t, url) { stack[idx] = hashOf(url); },
    back: function () { if (idx > 0) { idx--; setTimeout(function () { fire('popstate'); }, 0); } }
};
// A controller is connected, as on an Xbox: Stremio Web's own gamepad support would see it.
function Navigator() {}
Navigator.prototype.getGamepads = function () { return [{ id: 'Xbox Controller', buttons: [], axes: [0, 0] }]; };
const navigator = new Navigator();
navigator.userAgent = 'test';
const classes = {};
const document = {
    addEventListener: function () {}, querySelectorAll: function () { return []; }, querySelector: function () { return null; }, getElementById: function () { return null; },
    activeElement: null, body: {}, documentElement: { classList: { add: function (c) { classes[c] = true; }, remove: function (c) { delete classes[c]; } } }, head: { appendChild: function () {} },
    createElement: function () { return { classList: { add: function () {}, remove: function () {} } }; }
};

// ---- Fake WebView2 bridge ----
let posts = [];
const hostListeners = [];
const webview = {
    postMessage: function (m) { posts.push(JSON.parse(m)); },
    addEventListener: function (t, f) { hostListeners.push(f); }
};
function send(m) { hostListeners.forEach(function (f) { f({ data: JSON.stringify(m) }); }); }

// ---- Fake Stremio core ----
let coreLog = [];
let loaded = null;
function playerState() {
    if (!loaded) { return { selected: null, metaItem: null, libraryItem: null, nextVideo: null, seriesInfo: null }; }
    return {
        selected: loaded,
        title: 'Breaking Bad - Pilot (1x1)',
        metaItem: { type: 'Ready', content: { name: 'Breaking Bad', background: 'https://img.example/bg.jpg',
            videos: [{ id: 'tt0903747:1:1', title: 'Pilot', season: 1, episode: 1 }, { id: 'tt0903747:1:2', title: "Cat's in the Bag...", season: 1, episode: 2 }] } },
        seriesInfo: { season: 1, episode: 1 },
        introOutro: { intro: { from: 5000, to: 62000, duration: 57000 }, outro: null },
        libraryItem: { _id: 'tt0903747', state: { video_id: 'tt0903747:1:1', timeOffset: 1500000 } },
        nextVideo: { id: 'tt0903747:1:2', title: "Cat's in the Bag...", season: 1, episode: 2,
            deepLinks: { player: null, metaDetailsStreams: '#/detail/series/tt0903747/tt0903747%3A1%3A2' } }
    };
}
const core = {
    dispatch: function (action, field) {
        coreLog.push(JSON.parse(JSON.stringify({ action: action, field: field })));
        if (action.action === 'Load' && action.args.model === 'Player') { loaded = action.args.args; }
        if (action.action === 'Unload') { loaded = null; }
        return Promise.resolve();
    },
    getState: function (model) {
        if (model === 'player') { return Promise.resolve(playerState()); }
        if (model === 'ctx') { return Promise.resolve({ profile: { settings: { bingeWatching: true, subtitlesLanguage: 'eng', audioLanguage: 'ita' } } }); }
        return Promise.reject(new Error('unknown model'));
    }
};

const sandbox = {
    chrome: { webview: webview }, core: core, location: location, history: history, document: document,
    addEventListener: function (t, f) { (winListeners[t] = winListeners[t] || []).push(f); },
    console: { log: function () {}, info: function () {}, warn: function () {}, error: function () {} },
    setTimeout: setTimeout, clearTimeout: clearTimeout, setInterval: setInterval, clearInterval: clearInterval,
    atob: atob, btoa: btoa, Blob: Blob, Response: Response, DecompressionStream: DecompressionStream,
    AbortController: AbortController, fetch: function () { return Promise.reject(new Error('no network in test')); },
    localStorage: { getItem: function () { return null; } }, navigator: navigator, Navigator: Navigator,
    innerWidth: 1920, innerHeight: 1080, KeyboardEvent: function () {}
};
sandbox.window = sandbox;
vm.createContext(sandbox);
vm.runInContext(source, sandbox, { filename: 'shell.js' });
fire('DOMContentLoaded');

// ---- Helpers ----
function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
function waitFor(pred, ms) {
    const start = Date.now();
    return new Promise(function (resolve) {
        (function check() { const v = pred(); if (v || Date.now() - start > ms) { resolve(v); } else { setTimeout(check, 25); } })();
    });
}
async function encodeStream(obj) {
    const deflated = new Blob([new TextEncoder().encode(JSON.stringify(obj))]).stream().pipeThrough(new CompressionStream('deflate'));
    const buf = Buffer.from(await new Response(deflated).arrayBuffer());
    return encodeURIComponent(buf.toString('base64'));
}
const results = [];
function check(name, ok, detail) { results.push((ok ? 'PASS ' : 'FAIL ') + name + (ok ? '' : '  -> ' + JSON.stringify(detail))); }
function postsOf(type) { return posts.filter(function (p) { return p.type === type; }); }
function actions() { return coreLog.map(function (c) { return c.action.action === 'Player' ? 'Player.' + c.action.args.action : c.action.action; }); }
const base = encodeURIComponent('https://v3-cinemeta.strem.io/manifest.json');
async function openPlayer() {
    const enc = await encodeStream({ url: 'https://cdn.example/Breaking.Bad.S01E01.2160p.mkv', name: 'TB 4K', behaviorHints: { filename: 'Breaking.Bad.S01E01.2160p.mkv' } });
    sandbox.history.pushState(null, '', '#/detail/series/tt0903747/tt0903747%3A1%3A1');
    sandbox.history.pushState(null, '', '#/player/' + enc + '/' + base + '/' + base + '/series/tt0903747/' + encodeURIComponent('tt0903747:1:1'));
}

(async function run() {
    // A: picking a stream -> loading screen at once, then play with Stremio's info.
    posts = []; coreLog = [];
    await openPlayer();
    const prep = await waitFor(function () { return postsOf('preparing')[0]; }, 3000);
    check('A preparing posted at once', !!prep, posts);
    let play = await waitFor(function () { return postsOf('play')[0]; }, 12000) || {};
    check('A play posted', !!play.type, posts.map(function (p) { return p.type; }));
    check('A same session id', prep && play.session === prep.session, [prep, play.session]);
    check('A title', play.title === 'Breaking Bad', play.title);
    check('A episode line', play.subtitle === 'S1 E1 · Pilot', play.subtitle);
    check('A resume position', play.startPosition === 1500000, play.startPosition);
    check('A intro timing (Skip intro)', play.introFrom === 5000 && play.introTo === 62000, [play.introFrom, play.introTo]);
    check('A next label', play.next === "S1 E2 · Cat's in the Bag...", play.next);
    check('A binge + subtitle + audio language', play.binge === true && play.subtitlesLanguage === 'eng' && play.audioLanguage === 'ita', [play.binge, play.subtitlesLanguage, play.audioLanguage]);
    check('A backdrop', play.background === 'https://img.example/bg.jpg', play.background);
    check('A url/filename kept', play.url === 'https://cdn.example/Breaking.Bad.S01E01.2160p.mkv' && play.filename === 'Breaking.Bad.S01E01.2160p.mkv', play);
    check('A left the web player route', !/^#\/player\//.test(location.hash), location.hash);
    const load = coreLog.filter(function (c) { return c.action.action === 'Load'; })[0];
    const la = load && load.action.args.args;
    check('A Load args like Stremio Web', !!la && load.field === 'player' && la.streamRequest.path.id === 'tt0903747:1:1' &&
        la.metaRequest.path.id === 'tt0903747' && la.streamRequest.base === 'https://v3-cinemeta.strem.io/manifest.json' &&
        la.subtitlesPath.id === 'tt0903747:1:1' && la.stream.url === play.url, load);

    // B: progress, pause, zero time ignored, other session ignored, stop -> TimeChanged / PausedChanged / Unload.
    coreLog = [];
    const sid = play.session;
    send({ type: 'player', event: 'progress', session: sid, time: 600000, duration: 2900000, paused: false });
    send({ type: 'player', event: 'progress', session: sid, time: 0, duration: 2900000, paused: false });
    send({ type: 'player', event: 'progress', session: sid + 1, time: 700000, duration: 2900000, paused: false });
    send({ type: 'player', event: 'progress', session: sid, time: 610000, duration: 2900000, paused: true });
    await sleep(50);
    const times = coreLog.filter(function (c) { return c.action.args && c.action.args.action === 'TimeChanged'; }).map(function (c) { return c.action.args.args.time; });
    check('B TimeChanged only for real times of this session', JSON.stringify(times) === '[600000,610000]', times);
    const pauses = coreLog.filter(function (c) { return c.action.args && c.action.args.action === 'PausedChanged'; }).map(function (c) { return c.action.args.args.paused; });
    check('B PausedChanged false then true', pauses.join() === 'false,true', actions());
    check('B device name', coreLog[0].action.args.args.device === 'Stremio-XBOX', coreLog[0]);
    coreLog = [];
    send({ type: 'player', event: 'stopped', session: sid, time: 620000, duration: 2900000, paused: true });
    await sleep(50);
    check('B stop saves then unloads', actions().join() === 'Player.TimeChanged,Unload', actions());
    send({ type: 'player', event: 'progress', session: sid, time: 630000, duration: 2900000, paused: false });
    await sleep(50);
    check('B nothing after stop', actions().join() === 'Player.TimeChanged,Unload', actions());

    // C: playback ended, binge on, next episode without a binge stream -> its stream list + playerIdle.
    posts = []; coreLog = [];
    await openPlayer();
    play = await waitFor(function () { return postsOf('play')[0]; }, 12000) || {};
    coreLog = []; posts = [];
    send({ type: 'player', event: 'ended', session: play.session, time: 2899000, duration: 2900000, paused: true });
    await waitFor(function () { return postsOf('playerIdle')[0]; }, 3000);
    check('C Ended then Unload', actions().filter(function (a) { return a !== 'Player.PausedChanged'; }).join() === 'Player.TimeChanged,Player.Ended,Unload', actions());
    check('C opened next episode stream list', location.hash === '#/detail/series/tt0903747/tt0903747%3A1%3A2', location.hash);
    check('C playerIdle posted', postsOf('playerIdle').length === 1, posts);

    // D: B on the loading screen -> no play for that session, nothing left loaded.
    posts = []; coreLog = [];
    await openPlayer();
    const prep2 = await waitFor(function () { return postsOf('preparing')[0]; }, 3000);
    send({ type: 'player', event: 'cancel', session: prep2.session });
    await sleep(6000);
    check('D cancelled stream never posts play', postsOf('play').length === 0, posts.map(function (p) { return p.type; }));
    const acts = actions();
    check('D nothing left loaded', acts.indexOf('Load') === -1 || acts.lastIndexOf('Unload') > acts.lastIndexOf('Load'), acts);

    // E: next for an unknown session -> playerIdle, so the app leaves its loading screen.
    posts = [];
    send({ type: 'player', event: 'next', session: 12345 });
    await sleep(50);
    check('E next for unknown session answers playerIdle', postsOf('playerIdle').length === 1, posts);

    // F: next episode requested with a binge stream available -> navigates to its player route, which starts a new session.
    posts = []; coreLog = [];
    await openPlayer();
    play = await waitFor(function () { return postsOf('play')[0]; }, 12000) || {};
    const encNext = await encodeStream({ url: 'https://cdn.example/Breaking.Bad.S01E02.mkv', name: 'TB 4K' });
    const nextPlayer = '#/player/' + encNext + '/' + base + '/' + base + '/series/tt0903747/' + encodeURIComponent('tt0903747:1:2');
    const realState = playerState;
    playerState = function () { const s = realState(); if (s.nextVideo) { s.nextVideo.deepLinks.player = nextPlayer; } return s; };
    posts = []; coreLog = [];
    send({ type: 'player', event: 'next', session: play.session, time: 2800000, duration: 2900000, paused: false });
    const prep3 = await waitFor(function () { return postsOf('preparing')[0]; }, 3000);
    check('F next episode opens its binge stream', !!prep3 && prep3.session !== play.session, posts);
    check('F no playerIdle when a player route opens', postsOf('playerIdle').length === 0, posts);
    const play3 = await waitFor(function () { return postsOf('play')[0]; }, 12000) || {};
    check('F next episode plays', play3.url === 'https://cdn.example/Breaking.Bad.S01E02.mkv', play3);
    check('F first session saved and unloaded before the next Load', actions().filter(function (a) { return a !== 'Player.PausedChanged'; }).slice(0, 3).join() === 'Player.TimeChanged,Unload,Load', actions());

    // G: one controller input. The page sees no gamepad, and connect events never reach Stremio.
    check('G page sees no gamepad', sandbox.navigator.getGamepads().length === 0, sandbox.navigator.getGamepads());
    const connected = fire('gamepadconnected', { type: 'gamepadconnected', stopped: false, stopImmediatePropagation: function () { this.stopped = true; } });
    check('G gamepadconnected stopped', connected.stopped === true, connected);
    check('G controller styling on (Stremio focus ring hidden)', classes['__sx-nav'] === true, classes);

    // H: on Xbox (no keyboard navigation) arrow keys from WebView2 are swallowed before Stremio's own
    // spatial navigation sees them; on PC they drive the app's navigation; Esc goes back.
    send({ type: 'config', nativePlayer: true, keyboardNav: false });
    let k = fire('keydown', keyEvent('ArrowDown', 40));
    check('H Xbox: arrow swallowed', k.prevented && k.stopped, k);
    // What the console really sends (from its log): key "Unidentified" with arrow / Enter / gamepad key codes.
    [40, 37, 13, 207].forEach(function (code) {
        const x = fire('keydown', keyEvent('Unidentified', code));
        check('H Xbox: Unidentified keyCode ' + code + ' swallowed', x.prevented && x.stopped, x);
    });
    send({ type: 'config', nativePlayer: true, keyboardNav: true });
    k = fire('keydown', keyEvent('ArrowRight', 39));
    check('H PC: arrow handled by the app', k.prevented && k.stopped, k);
    sandbox.history.pushState(null, '', '#/detail/series/tt0903747');
    const before = location.hash;
    await sleep(600); // __sxBack ignores a second back within 500 ms
    k = fire('keydown', keyEvent('Escape', 27));
    await sleep(50);
    check('H PC: Esc goes back', k.prevented && location.hash !== before, [k.prevented, before, location.hash]);
    k = fire('keydown', keyEvent('a', 65));
    check('H PC: other keys untouched', !k.prevented && !k.stopped, k);

    console.log(results.join('\n'));
    const failed = results.filter(function (r) { return r.indexOf('PASS') !== 0; }).length;
    console.log(failed ? failed + ' FAILED' : 'ALL ' + results.length + ' PASSED');
    process.exit(failed ? 1 : 0);
})().catch(function (e) { console.log(results.join('\n')); console.log('ERROR', e && e.stack || e); process.exit(2); });
