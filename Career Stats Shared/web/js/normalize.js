/*
 * Turns whatever /api/career actually returns into one canonical shape.
 *
 * The two game APIs are being written in parallel with this page, so nothing here
 * assumes a serialiser setting: keys may arrive camelCase or PascalCase, enums as
 * strings ("Higher") or as numbers (0), a TimeSpan as "01:02:03" or as seconds,
 * and computed members (Kpi.Improved, MatchSummary.Kd) may or may not have been
 * serialised at all -- when they are missing they are recomputed here with the
 * same rules Career.cs uses.
 *
 * Nothing in here recomputes a *trend*: Slope, SlopePerWeek and Direction are
 * significance-tested on the server and are rendered exactly as given.
 */
(function (root) {
  'use strict';

  var EET = (root.EET = root.EET || {});
  var U = EET.util;

  // Only three categorical hues are validated for this surface. The first three
  // trend series take them, in payload order, keyed by the metric -- so a colour
  // follows the metric and never its rank on screen. Anything past the third is
  // the "Other" ink rather than an invented fourth hue.
  var SLOTS = ['var(--series-1)', 'var(--series-2)', 'var(--series-3)'];
  var OTHER = 'var(--series-other)';

  var BETTER = ['Higher', 'Lower', 'Neutral'];
  var GAMES = ['HaloInfinite', 'Destiny2'];

  function pick(obj) {
    if (!obj) return undefined;
    for (var i = 1; i < arguments.length; i++) {
      var name = arguments[i];
      if (obj[name] !== undefined) return obj[name];
      var lower = name.charAt(0).toLowerCase() + name.slice(1);
      if (obj[lower] !== undefined) return obj[lower];
      var upper = name.charAt(0).toUpperCase() + name.slice(1);
      if (obj[upper] !== undefined) return obj[upper];
    }
    return undefined;
  }

  function num(v, fallback) {
    if (typeof v === 'number' && isFinite(v)) return v;
    if (typeof v === 'string' && v !== '') {
      var n = Number(v);
      if (isFinite(n)) return n;
    }
    return fallback === undefined ? null : fallback;
  }

  function str(v, fallback) {
    if (typeof v === 'string') return v;
    if (typeof v === 'number') return String(v);
    return fallback === undefined ? '' : fallback;
  }

  function bool(v) {
    if (v === true || v === false) return v;
    if (v === 'true') return true;
    if (v === 'false') return false;
    return null;
  }

  function enumValue(v, names, fallback) {
    if (typeof v === 'number' && names[v] !== undefined) return names[v];
    if (typeof v === 'string' && v) {
      for (var i = 0; i < names.length; i++) {
        if (names[i].toLowerCase() === v.toLowerCase()) return names[i];
      }
      return v;
    }
    return fallback;
  }

  function list(v) { return Array.isArray(v) ? v : []; }

  /* --------------------------------------------------------------- parts */

  function player(raw) {
    raw = raw || {};
    return {
      handle: str(pick(raw, 'Handle'), 'Unknown player'),
      id: str(pick(raw, 'Id')),
      platform: str(pick(raw, 'Platform')),
      iconUrl: str(pick(raw, 'IconUrl')) || null
    };
  }

  function kpi(raw) {
    var better = enumValue(pick(raw, 'Better'), BETTER, 'Neutral');
    var value = num(pick(raw, 'Value'), 0);
    var delta = num(pick(raw, 'Delta'), null);
    var improved = bool(pick(raw, 'Improved'));
    if (improved === null) {
      // Kpi.Improved, recomputed exactly as Career.cs defines it.
      improved = (delta === null || better === 'Neutral')
        ? null
        : (better === 'Higher' ? delta > 0 : delta < 0);
    }
    return {
      key: str(pick(raw, 'Key')),
      label: str(pick(raw, 'Label'), 'Metric'),
      value: value,
      formatted: str(pick(raw, 'Formatted')) || U.seriesValue(value, ''),
      better: better,
      delta: delta,
      deltaFormatted: str(pick(raw, 'DeltaFormatted')) || null,
      note: str(pick(raw, 'Note')) || null,
      improved: improved
    };
  }

  function trend(raw, index) {
    var unit = str(pick(raw, 'Unit'));
    var rawPoints = list(pick(raw, 'Points'));
    var points = [];
    for (var i = 0; i < rawPoints.length; i++) {
      var p = rawPoints[i];
      var date = U.toDate(pick(p, 'Date'));
      var value = num(pick(p, 'Value'), null);
      if (!date || value === null) continue;
      points.push({
        date: date,
        dateIso: U.isoDay(date),
        value: value,
        samples: Math.max(0, num(pick(p, 'Samples'), 1) || 0),
        index: points.length,
        // Where this point sat in the payload. Smoothed is a parallel array keyed on
        // THAT position, so it has to be remembered: the loop above skips any point
        // with an unparseable date or a null value, and pairing the survivors with
        // Smoothed by their new position would slide the whole EWMA line one day
        // earlier for every point dropped -- a wrong line, drawn confidently.
        sourceIndex: i
      });
    }
    points.sort(function (a, b) { return a.date - b.date; });
    for (var j = 0; j < points.length; j++) points[j].index = j;

    var smoothedRaw = list(pick(raw, 'Smoothed'));
    var smoothed = [];
    for (var k = 0; k < points.length; k++) {
      var s = num(smoothedRaw[points[k].sourceIndex], null);
      smoothed.push(s === null ? points[k].value : s);
    }

    var values = [];
    var maxSamples = 1;
    for (var m = 0; m < points.length; m++) {
      values.push(points[m].value);
      if (points[m].samples > maxSamples) maxSamples = points[m].samples;
    }
    var span = U.extent(values.concat(smoothed));
    var maxAbs = Math.max(Math.abs(span[0]), Math.abs(span[1]));

    return {
      key: str(pick(raw, 'Key'), 'series-' + index),
      label: str(pick(raw, 'Label'), 'Series ' + (index + 1)),
      unit: unit,
      better: enumValue(pick(raw, 'Better'), BETTER, 'Neutral'),
      points: points,
      smoothed: smoothed,
      slope: num(pick(raw, 'Slope'), 0),
      slopePerWeek: num(pick(raw, 'SlopePerWeek'), 0),
      direction: str(pick(raw, 'Direction'), 'steady'),
      // Presentation, derived once so charts and tables agree.
      color: index < SLOTS.length ? SLOTS[index] : OTHER,
      isOtherSlot: index >= SLOTS.length,
      scaleToPercent: U.unitIsFraction(unit, maxAbs),
      maxSamples: maxSamples,
      lowSampleCutoff: 3
    };
  }

  function match(raw) {
    var kills = num(pick(raw, 'Kills'), 0);
    var deaths = num(pick(raw, 'Deaths'), 0);
    var kd = num(pick(raw, 'Kd'), null);
    if (kd === null) kd = deaths === 0 ? kills : kills / deaths; // MatchSummary.Kd
    return {
      id: str(pick(raw, 'Id')),
      playedAt: U.toDate(pick(raw, 'PlayedAt')),
      durationSeconds: U.timeSpanSeconds(pick(raw, 'Duration')),
      mode: str(pick(raw, 'Mode'), 'Unknown'),
      map: str(pick(raw, 'Map'), 'Unknown'),
      playlist: str(pick(raw, 'Playlist')) || null,
      won: bool(pick(raw, 'Won')),
      kills: kills,
      deaths: deaths,
      assists: num(pick(raw, 'Assists'), 0),
      accuracy: num(pick(raw, 'Accuracy'), null),
      score: num(pick(raw, 'Score'), null),
      kda: num(pick(raw, 'Kda'), null),
      kd: kd,
      extra: pick(raw, 'Extra') || null
    };
  }

  function breakdown(raw) {
    var rows = list(pick(raw, 'Rows')).map(function (r) {
      var value = num(pick(r, 'Value'), 0);
      return {
        name: str(pick(r, 'Name'), '--'),
        value: value,
        formatted: str(pick(r, 'Formatted')) || U.seriesValue(value, ''),
        samples: num(pick(r, 'Samples'), 0) || 0,
        share: num(pick(r, 'Share'), null),
        iconUrl: str(pick(r, 'IconUrl')) || null
      };
    });
    return {
      key: str(pick(raw, 'Key'), 'breakdown'),
      label: str(pick(raw, 'Label'), 'Breakdown'),
      valueLabel: str(pick(raw, 'ValueLabel'), 'Value'),
      rows: rows
    };
  }

  function totals(raw) {
    raw = raw || {};
    var matches = num(pick(raw, 'Matches'), 0) || 0;
    var wins = num(pick(raw, 'Wins'), 0) || 0;
    var kills = num(pick(raw, 'Kills'), 0) || 0;
    var deaths = num(pick(raw, 'Deaths'), 0) || 0;
    var winRate = num(pick(raw, 'WinRate'), null);
    var kd = num(pick(raw, 'Kd'), null);
    return {
      matches: matches,
      wins: wins,
      losses: num(pick(raw, 'Losses'), 0) || 0,
      timePlayedSeconds: U.timeSpanSeconds(pick(raw, 'TimePlayed')),
      kills: kills,
      deaths: deaths,
      assists: num(pick(raw, 'Assists'), 0) || 0,
      winRate: winRate === null ? (matches === 0 ? 0 : wins / matches) : winRate,
      kd: kd === null ? (deaths === 0 ? kills : kills / deaths) : kd
    };
  }

  var GAME_LABELS = { HaloInfinite: 'Halo Infinite', Destiny2: 'Destiny 2' };

  function snapshot(raw) {
    if (!raw || typeof raw !== 'object') throw new Error('The career payload was not an object.');
    var game = enumValue(pick(raw, 'Game'), GAMES, 'HaloInfinite');
    var trends = list(pick(raw, 'Trends')).map(trend);

    var colorByKey = {};
    for (var i = 0; i < trends.length; i++) colorByKey[trends[i].key] = trends[i].color;

    return {
      player: player(pick(raw, 'Player')),
      game: game,
      gameLabel: GAME_LABELS[game] || game,
      generatedAt: U.toDate(pick(raw, 'GeneratedAt')),
      isFixture: bool(pick(raw, 'IsFixture')) === true,
      source: str(pick(raw, 'Source')),
      headline: list(pick(raw, 'Headline')).map(kpi),
      trends: trends,
      recent: list(pick(raw, 'Recent')).map(match),
      breakdowns: list(pick(raw, 'Breakdowns')).map(breakdown),
      totals: totals(pick(raw, 'Totals')),
      warnings: list(pick(raw, 'Warnings')).map(function (w) { return str(w); }).filter(Boolean),
      note: str(pick(raw, '_note')) || null,
      colorByKey: colorByKey,
      seriesByKey: (function () {
        var byKey = {};
        for (var j = 0; j < trends.length; j++) byKey[trends[j].key] = trends[j];
        return byKey;
      }())
    };
  }

  EET.normalize = { snapshot: snapshot, pick: pick };
}(typeof globalThis !== 'undefined' ? globalThis : this));
