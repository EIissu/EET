/*
 * Boot, routing, theming and the data chain.
 *
 * Zero credentials is the normal case, not the error case, so the page has three
 * sources in order of preference and says out loud which one it is showing:
 *
 *   1. GET /api/career on the host that served this page -- live or the API's own
 *      fixture, whichever the server decided. IsFixture tells us which.
 *   2. mock/<game>-career.json next to this page, when the API is unreachable.
 *   3. mock/mock-data.js, injected as a classic script, for a page opened straight
 *      off disk -- a file:// page is not allowed to fetch anything at all.
 *
 * Only if all three fail does the reader see an empty state, and it is one that
 * tells them what to do rather than a stack trace.
 */
(function (root) {
  'use strict';

  var EET = (root.EET = root.EET || {});
  EET.booted = true; // read by the loader in index.html; set before any work is done

  var U = EET.util;
  var el = U.el;

  // Each API names the player differently and both REQUIRE one, so the page keeps the
  // name per game rather than guessing: Halo takes ?player= (XUID or gamertag),
  // Destiny takes ?q= (a Bungie name) or ?membershipType=&membershipId=.
  //
  // `identity` is every query parameter that names a player TO THAT API, and the two
  // lists share nothing on purpose. An XUID is meaningless to Bungie and a membership
  // id is meaningless to 343, so a parameter left behind by the other game is not a
  // harmless leftover: Destiny answers 400 to ?player=, and it prefers ?membershipId=
  // over ?q= whenever both are present, which silently deadens the lookup box. Every
  // route that changes who or what is being looked at clears the whole set first.
  //
  var GAMES = {
    halo: {
      key: 'halo', label: 'Halo Infinite', mock: 'mock/halo-career.json', mockKey: 'halo',
      playerParam: 'player', placeholder: 'gamertag or XUID',
      identity: ['player']
    },
    destiny: {
      key: 'destiny', label: 'Destiny 2', mock: 'mock/destiny-career.json', mockKey: 'destiny',
      playerParam: 'q', placeholder: 'Bungie name, e.g. Guardian#4417',
      identity: ['q', 'membershipType', 'membershipId']
    }
  };

  /** Own properties only: ?game=constructor must not resolve to Object's constructor. */
  function own(table, key) {
    return Object.prototype.hasOwnProperty.call(table, key) ? table[key] : undefined;
  }

  /** Drop every player-naming parameter of every game, so none can outlive its game. */
  function clearIdentity(params) {
    Object.keys(GAMES).forEach(function (k) {
      GAMES[k].identity.forEach(function (name) { params.delete(name); });
    });
    return params;
  }

  /**
   * The query string as it should look when <paramref name="game"/> is the one on screen:
   * that game's own player parameters preserved exactly, every other game's discarded.
   */
  function paramsFor(game) {
    var current = new URLSearchParams(root.location.search);
    var kept = game.identity
      .map(function (name) { return [name, current.get(name)]; })
      .filter(function (pair) { return pair[1]; });

    var params = clearIdentity(new URLSearchParams(root.location.search));
    kept.forEach(function (pair) { params.set(pair[0], pair[1]); });
    params.set('game', game.key);
    return params;
  }

  // The page's own controls. Everything else in the query string belongs to the API
  // and is forwarded untouched, so a membershipType or an xuid the operator adds by
  // hand reaches the endpoint without this file having to know about it.
  var OWN_PARAMS = { game: 1, theme: 1, api: 1 };

  var ALIASES = {
    halo: 'halo', haloinfinite: 'halo', 'halo-infinite': 'halo', hi: 'halo',
    destiny: 'destiny', destiny2: 'destiny', 'destiny-2': 'destiny', d2: 'destiny'
  };

  var THEME_KEY = 'eet-tracker-theme';
  var drawables = [];

  /* ---------------------------------------------------------------- theme */

  function storedTheme() {
    var forced = new URLSearchParams(root.location.search).get('theme');
    if (forced === 'light' || forced === 'dark') return forced;
    try { return root.localStorage ? root.localStorage.getItem(THEME_KEY) : null; }
    catch (e) { return null; }
  }

  function applyTheme(theme) {
    var node = document.documentElement;
    if (theme === 'light' || theme === 'dark') node.setAttribute('data-theme', theme);
    else node.removeAttribute('data-theme');
    try {
      if (root.localStorage) {
        if (theme === 'system') root.localStorage.removeItem(THEME_KEY);
        else root.localStorage.setItem(THEME_KEY, theme);
      }
    } catch (e) { /* private mode: the choice just does not persist */ }
  }

  function themeSwitch() {
    var current = storedTheme() || 'system';
    var group = el('div', { class: 'switch', role: 'group', 'aria-label': 'Colour theme' });
    var options = [['system', 'Auto'], ['light', 'Light'], ['dark', 'Dark']];
    var buttons = options.map(function (opt) {
      var b = el('button', {
        type: 'button', 'aria-pressed': String(opt[0] === current), text: opt[1]
      });
      b.addEventListener('click', function () {
        current = opt[0];
        applyTheme(current);
        buttons.forEach(function (other, i) {
          other.setAttribute('aria-pressed', String(options[i][0] === current));
        });
      });
      group.appendChild(b);
      return b;
    });
    return group;
  }

  /* --------------------------------------------------------------- routing */

  function currentGame() {
    var params = new URLSearchParams(root.location.search);
    var raw = (params.get('game') || 'halo').toLowerCase();
    // Two own-property lookups and a final fallback: an unknown -- or hostile -- ?game=
    // lands on Halo rather than returning undefined and blanking the page.
    return own(GAMES, own(ALIASES, raw) || 'halo') || GAMES.halo;
  }

  function gameSwitch(active) {
    var group = el('div', { class: 'switch', role: 'group', 'aria-label': 'Game' });
    Object.keys(GAMES).forEach(function (k) {
      // Carry a player across only when the target game can actually use it: a Bungie
      // name means nothing to 343 and an XUID means nothing to Bungie. Staying on the
      // current game keeps whoever is already on screen.
      var params = paramsFor(GAMES[k]);
      group.appendChild(el('a', {
        href: '?' + params.toString(),
        'aria-current': k === active.key ? 'page' : null,
        text: GAMES[k].label
      }));
    });
    return group;
  }

  /* ------------------------------------------------------------ data chain */

  function apiUrl(game) {
    var params = new URLSearchParams(root.location.search);
    var base = params.get('api') || '';
    var forward = new URLSearchParams();
    params.forEach(function (value, name) {
      if (!own(OWN_PARAMS, name) && value) forward.set(name, value);
    });
    // The API that serves this page is that game's API; ?api= is only for split hosts.
    var path = base.replace(/\/+$/, '') + '/api/career';
    var query = forward.toString();
    return query ? path + '?' + query : path;
  }

  function currentPlayerQuery(game) {
    var params = new URLSearchParams(root.location.search);
    return params.get(game.playerParam) || params.get('player') || params.get('q') || '';
  }

  /**
   * Both APIs answer 400 without a player, so the page needs a way to name one that
   * is not "edit the URL by hand".
   */
  function lookupForm(game) {
    var input = el('input', {
      type: 'search', class: 'lookup__input', id: 'player-query',
      name: game.playerParam, placeholder: game.placeholder,
      value: currentPlayerQuery(game), autocomplete: 'off', spellcheck: 'false'
    });
    var form = el('form', { class: 'lookup', role: 'search' }, [
      el('label', { class: 'visually-hidden', for: 'player-query', text: 'Player to look up' }),
      input,
      el('button', { class: 'btn', type: 'submit', text: 'Look up' })
    ]);
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var params = new URLSearchParams(root.location.search);
      var value = input.value.trim();
      // Clear every identity parameter, not just the two the box writes. Destiny reads
      // ?membershipId= in preference to ?q=, so leaving a stale one behind means typing
      // a new Bungie name changes the URL and nothing else.
      clearIdentity(params);
      if (value) params.set(game.playerParam, value);
      params.set('game', game.key);
      root.location.search = params.toString();
    });
    return form;
  }

  /**
   * How long to wait before giving up on a request and moving down the chain.
   *
   * An API that answers slowly and an API that never answers look identical to fetch(),
   * which has no timeout of its own: without this the page sits on "Loading..." until
   * the OS gives up on the socket -- minutes, or never -- and the sample data that would
   * have rendered instantly is never reached. Twelve seconds is longer than either API
   * needs for a cold fixture read and short enough that a wedged host still leaves the
   * reader with a working page.
   */
  var TIMEOUT_MS = 12000;

  function fetchJson(url) {
    var options = { headers: { accept: 'application/json' }, cache: 'no-store' };
    var controller = typeof root.AbortController === 'function' ? new root.AbortController() : null;
    var timer = null;
    var timedOut = false;

    if (controller) {
      options.signal = controller.signal;
      timer = setTimeout(function () {
        timedOut = true;
        controller.abort();
      }, TIMEOUT_MS);
    }

    function done() {
      if (timer) { clearTimeout(timer); timer = null; }
    }

    return fetch(url, options)
      .then(function (response) { done(); return response; }, function (err) {
        done();
        if (timedOut) {
          var timeout = new Error('No answer within ' + (TIMEOUT_MS / 1000) + ' seconds.');
          timeout.timeout = true;
          throw timeout;
        }
        throw err;
      })
      .then(function (response) {
        if (!response.ok) {
          return response.text().then(function (body) {
            var message = 'The API answered ' + response.status + ' ' + response.statusText + '.';
            var remedy = null;
            try {
              // RFC 7807 ProblemDetails, plus the APIs' own "remedy" extension.
              var parsed = JSON.parse(body);
              message = parsed.message || parsed.title || parsed.detail || message;
              remedy = parsed.remedy || (parsed.detail !== message ? parsed.detail : null) || null;
            } catch (e) { /* not JSON: keep the status line */ }
            var err = new Error(message);
            err.remedy = remedy;
            err.status = response.status;
            throw err;
          });
        }
        return response.json();
      });
  }

  function loadClassicMock() {
    return new Promise(function (resolve, reject) {
      if (EET.mock) return resolve(EET.mock);
      var script = document.createElement('script');
      var settled = false;
      // A <script> pointed at a host that accepts the connection and then stalls fires
      // neither onload nor onerror, so this promise needs its own deadline too.
      var timer = setTimeout(function () { finish(new Error('mock/mock-data.js did not load within ' + (TIMEOUT_MS / 1000) + ' seconds.')); }, TIMEOUT_MS);

      function finish(err) {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        if (err) reject(err); else if (EET.mock) resolve(EET.mock);
        else reject(new Error('mock/mock-data.js loaded but defined nothing.'));
      }

      script.src = 'mock/mock-data.js';
      script.async = false;
      script.onload = function () { finish(null); };
      script.onerror = function () { finish(new Error('Could not load mock/mock-data.js.')); };
      document.head.appendChild(script);
    });
  }

  function load(game) {
    var offline = root.location.protocol === 'file:';
    var problems = [];

    function fromBundle(why) {
      if (why) problems.push(why);
      return loadClassicMock().then(function (mock) {
        var payload = mock[game.mockKey];
        if (!payload) throw new Error('No bundled sample for ' + game.label + '.');
        return { payload: payload, origin: 'bundled', problems: problems };
      }).catch(function (err) {
        // Carry everything that went wrong, not just the last thing.
        err.problems = problems.concat(['Bundled sample: ' + err.message]);
        throw err;
      });
    }

    if (offline) {
      return fromBundle('Opened from the file system, where a browser refuses every fetch. ' +
        'Serve this folder over HTTP to talk to the API.');
    }

    return fetchJson(apiUrl(game))
      .then(function (payload) { return { payload: payload, origin: 'api', problems: problems }; })
      .catch(function (err) {
        problems.push('The API did not answer: ' + err.message +
          (err.remedy ? ' ' + err.remedy : ''));
        return fetchJson(game.mock)
          .then(function (payload) {
            return { payload: payload, origin: 'mockfile', problems: problems };
          })
          .catch(function (err2) { return fromBundle('Sample file unavailable: ' + err2.message); });
      });
  }

  /* --------------------------------------------------------------- render */

  function sourceBadge(snap, origin) {
    if (origin === 'api' && !snap.isFixture) {
      return el('span', { class: 'badge badge--live', title: snap.source }, [
        el('span', { 'aria-hidden': 'true', text: '●' }), el('span', { text: 'Live data' })
      ]);
    }
    if (origin === 'api') {
      return el('span', { class: 'badge badge--sample', title: snap.source }, [
        el('span', { text: 'Fixture data from the API' })
      ]);
    }
    return el('span', { class: 'badge badge--sample' }, [
      el('span', {
        text: origin === 'mockfile'
          ? 'Sample data served beside this page'
          : 'Sample data bundled with this page'
      })
    ]);
  }

  function render(mount, game, result) {
    var snap = EET.normalize.snapshot(result.payload);
    document.title = snap.player.handle + ' - ' + snap.gameLabel + ' - EET career tracker';

    U.clear(mount);
    drawables = [];

    mount.appendChild(topbar(game));

    if (result.problems.length) {
      mount.appendChild(el('div', { class: 'section' }, el('div', { class: 'panel' },
        result.problems.map(function (p) {
          return el('div', { class: 'notice' }, [
            el('span', { class: 'notice__glyph', 'aria-hidden': 'true', text: 'ⓘ' }),
            el('span', { text: p })
          ]);
        }))));
    }

    mount.appendChild(EET.ui.identityPanel(snap, sourceBadge(snap, result.origin)));

    var kpis = EET.ui.kpiRow(snap);
    if (kpis) {
      mount.appendChild(el('section', { class: 'section' }, [
        el('div', { class: 'section__head' }, [
          el('h2', { class: 'section__title', text: 'Headline numbers' }),
          el('span', {
            class: 'section__note',
            text: 'Recent form against the stretch immediately before it, not against a lifetime average.'
          })
        ]),
        kpis.el
      ]));
      drawables.push(kpis);
    }

    var trends = EET.ui.trendsSection(snap);
    mount.appendChild(trends.el);
    drawables.push(trends);

    var breakdowns = EET.ui.breakdownsSection(snap);
    if (breakdowns) {
      mount.appendChild(breakdowns.el);
      drawables.push(breakdowns);
    }

    var matches = EET.ui.matchesSection(snap);
    if (matches) mount.appendChild(matches);

    var warnings = EET.ui.warningsSection(snap);
    if (warnings) mount.appendChild(warnings);

    mount.appendChild(el('footer', { class: 'footer' }, [
      el('span', { text: 'Source: ' + (snap.source || 'unknown') }),
      el('span', { text: 'Generated ' + (snap.generatedAt ? U.dayMonthYear(snap.generatedAt) + ' ' +
        U.dayMonthTime(snap.generatedAt).split(' ').pop() + ' UTC' : 'unknown') }),
      snap.note ? el('span', { text: 'Synthetic sample data' }) : null
    ]));

    drawAll();
  }

  function topbar(game) {
    return el('header', { class: 'topbar' }, [
      el('div', { class: 'brand' }, [
        el('span', { class: 'brand__name', text: 'EET career tracker' }),
        el('span', { class: 'brand__sub', text: game.label })
      ]),
      lookupForm(game),
      gameSwitch(game),
      themeSwitch()
    ]);
  }

  function drawAll() {
    for (var i = 0; i < drawables.length; i++) {
      try { drawables[i].draw(); }
      catch (e) { if (root.console) console.error('chart draw failed', e); }
    }
  }

  function failure(mount, game, error) {
    U.clear(mount);
    mount.appendChild(topbar(game));
    mount.appendChild(el('div', { class: 'section' }, EET.ui.emptyState(
      'No career data to show',
      'Nothing answered: neither ' + game.label + '’s API nor the sample data bundled with this page.',
      el('div', { style: 'margin-top:14px;font-size:13px;text-align:left;display:inline-block' }, [
        el('p', { text: 'What usually fixes it:' }),
        el('ul', { style: 'margin:8px 0 0;padding-left:18px' }, [
          el('li', null, ['start the ', el('code', { text: game.label }), ' API and reload -- it serves this page and ',
            el('code', { text: '/api/career' }), ' from the same origin']),
          el('li', null, ['or point this page at a running API with ', el('code', { text: '?api=http://localhost:5000' })]),
          el('li', null, ['or serve this folder over HTTP: ', el('code', { text: 'python -m http.server 8080' })])
        ]),
        el('p', { style: 'margin-top:10px;color:var(--ink-muted)', text: 'What was tried:' }),
        el('ul', { style: 'margin:4px 0 0;padding-left:18px;color:var(--ink-muted)' },
          ((error && error.problems) || [(error && error.message) || String(error)])
            .map(function (p) { return el('li', { text: p }); }))
      ])
    )));
  }

  /* ----------------------------------------------------------------- boot */

  function start() {
    var mount = document.getElementById('app');
    if (!mount) return;
    var game = currentGame();

    U.clear(mount);
    mount.appendChild(topbar(game));
    mount.appendChild(el('div', { class: 'section' }, el('div', { class: 'panel' },
      el('div', { class: 'empty', text: 'Loading ' + game.label + ' career data…' }))));

    load(game)
      .then(function (result) { render(mount, game, result); })
      .catch(function (error) {
        if (root.console) console.error(error);
        failure(mount, game, error);
      });

    var timer = null;
    var lastWidth = root.innerWidth;
    root.addEventListener('resize', function () {
      if (root.innerWidth === lastWidth) return; // ignore mobile chrome height changes
      lastWidth = root.innerWidth;
      if (timer) clearTimeout(timer);
      timer = setTimeout(drawAll, 140);
    });
  }

  EET.start = start;

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start(); // the classic-script fallback path lands here, after DOMContentLoaded
  }
}(typeof globalThis !== 'undefined' ? globalThis : this));
