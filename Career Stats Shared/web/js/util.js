/*
 * Formatting, DOM and maths helpers.
 *
 * Every number and date on this page is formatted here, by hand, with no
 * toLocaleString anywhere: a K/D rendered "1,42" by a German browser would break
 * every chart axis. Invariant means invariant, in the browser too.
 *
 * Loaded as an ES module over HTTP and as a classic script from file:// -- so no
 * import/export syntax, and everything hangs off one namespace object.
 */
(function (root) {
  'use strict';

  var EET = (root.EET = root.EET || {});

  var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
    'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

  /* ------------------------------------------------------------- numbers */

  function group(text) {
    var negative = text.charAt(0) === '-';
    if (negative) text = text.slice(1);
    var parts = text.split('.');
    var whole = parts[0].replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    return (negative ? '-' : '') + whole + (parts[1] ? '.' + parts[1] : '');
  }

  // toFixed is locale-independent by spec, which is exactly why it is used here.
  function fixed(value, decimals) {
    if (!isFiniteNumber(value)) return '--';
    return Number(value).toFixed(decimals);
  }

  function isFiniteNumber(v) {
    return typeof v === 'number' && isFinite(v);
  }

  function ratio(v) { return fixed(v, 2); }

  function integer(v) {
    if (!isFiniteNumber(v)) return '--';
    return group(String(Math.round(v)));
  }

  function percent(fraction, decimals) {
    if (!isFiniteNumber(fraction)) return '--';
    return (fraction * 100).toFixed(decimals === undefined ? 1 : decimals) + '%';
  }

  function signed(v, decimals) {
    if (!isFiniteNumber(v)) return '--';
    var d = decimals === undefined ? 2 : decimals;
    var body = Math.abs(v).toFixed(d);
    return v > 0 ? '+' + body : v < 0 ? '-' + body : body;
  }

  /**
   * A trend series carries a Unit string. "%" means the values are fractions --
   * Format.Percent on the server takes a fraction too -- but a server that has
   * already multiplied by 100 should not be shown as 4530%, so values above 1.5
   * are treated as already-scaled.
   */
  function unitIsFraction(unit, maxAbs) {
    return isPercentUnit(unit) && maxAbs <= 1.5;
  }

  /**
   * Units that mean "this value is a fraction of one, so print it as a percentage".
   *
   * The list is what the two APIs actually put on the wire, not what this page would
   * have preferred them to send. Halo's HaloCareerSource builds its winRate and
   * accuracy series with the unit "fraction"; Destiny's DestinyMapper builds its
   * winrate series with "%". Both mean the same thing -- a value in [0,1] that
   * Format.Percent would multiply by 100 -- and a unit string this page fails to
   * recognise is the difference between an axis reading 20% / 40% / 60% and the same
   * axis reading 0.2 / 0.4 / 0.6 beside a stat tile that says "55.8%".
   */
  var FRACTION_UNITS = {
    '%': 1,
    fraction: 1,
    percent: 1,
    percentage: 1,
    pct: 1,
    proportion: 1,
    share: 1,
    rate: 1
  };

  function isPercentUnit(unit) {
    if (!unit) return false;
    var u = String(unit).toLowerCase().trim();
    // hasOwnProperty, not `in`: a unit called "constructor" must not match.
    return Object.prototype.hasOwnProperty.call(FRACTION_UNITS, u) || u.indexOf('percent') >= 0;
  }

  /** Format a value for a series, given the series unit and its display scale. */
  function seriesValue(value, unit, scaleToPercent, decimals) {
    if (!isFiniteNumber(value)) return '--';
    if (scaleToPercent) return percent(value, decimals === undefined ? 1 : decimals);
    if (isPercentUnit(unit)) return fixed(value, decimals === undefined ? 1 : decimals) + '%';
    var abs = Math.abs(value);
    if (decimals !== undefined) return group(fixed(value, decimals));
    if (abs >= 1000) return integer(value);
    if (abs >= 100) return group(fixed(value, 1));
    return fixed(value, 2);
  }

  /* --------------------------------------------------------------- dates */

  /** "2026-06-14" or an ISO instant -> a UTC Date, or null. */
  function toDate(value) {
    if (value instanceof Date) return isNaN(value.getTime()) ? null : value;
    if (value && typeof value === 'object') {
      // A DateOnly serialised as an object by a non-default converter.
      var y = value.year, m = value.month, d = value.day;
      if (isFiniteNumber(y) && isFiniteNumber(m) && isFiniteNumber(d)) {
        return new Date(Date.UTC(y, m - 1, d));
      }
      return null;
    }
    if (typeof value !== 'string' || !value) return null;
    var text = value;
    if (/^\d{4}-\d{2}-\d{2}$/.test(text)) text += 'T00:00:00Z';
    var parsed = new Date(text);
    return isNaN(parsed.getTime()) ? null : parsed;
  }

  function isoDay(date) {
    if (!date) return '--';
    return date.getUTCFullYear() + '-' +
      pad2(date.getUTCMonth() + 1) + '-' + pad2(date.getUTCDate());
  }

  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  /** "14 Jun" -- month names hardcoded so no locale can reorder them. */
  function dayMonth(date) {
    if (!date) return '--';
    return date.getUTCDate() + ' ' + MONTHS[date.getUTCMonth()];
  }

  function dayMonthYear(date) {
    if (!date) return '--';
    return dayMonth(date) + ' ' + date.getUTCFullYear();
  }

  /** "14 Jun 20:10" -- always UTC, and every caller says so in the header. */
  function dayMonthTime(date) {
    if (!date) return '--';
    return dayMonth(date) + ' ' + pad2(date.getUTCHours()) + ':' + pad2(date.getUTCMinutes());
  }

  function daysBetween(a, b) {
    return Math.round((b.getTime() - a.getTime()) / 86400000);
  }

  /* ------------------------------------------------------------ timespan */

  /**
   * System.Text.Json writes a TimeSpan in the "c" format: [-][d.]hh:mm:ss[.fffffff].
   * Numbers (seconds) and { ticks } objects are accepted too, because nobody has
   * pinned the serialiser down yet.
   */
  function timeSpanSeconds(value) {
    if (value === null || value === undefined) return null;
    if (isFiniteNumber(value)) return value;
    if (typeof value === 'object') {
      if (isFiniteNumber(value.totalSeconds)) return value.totalSeconds;
      if (isFiniteNumber(value.ticks)) return value.ticks / 1e7;
      return null;
    }
    var text = String(value).trim();
    var negative = text.charAt(0) === '-';
    if (negative) text = text.slice(1);
    var days = 0;
    var dot = text.indexOf('.');
    var firstColon = text.indexOf(':');
    if (dot > -1 && (firstColon === -1 || dot < firstColon)) {
      days = parseInt(text.slice(0, dot), 10) || 0;
      text = text.slice(dot + 1);
    }
    var parts = text.split(':');
    if (parts.length < 2) return null;
    var h = parseFloat(parts[0]) || 0;
    var m = parseFloat(parts[1]) || 0;
    var s = parts.length > 2 ? parseFloat(parts[2]) || 0 : 0;
    var total = days * 86400 + h * 3600 + m * 60 + s;
    return negative ? -total : total;
  }

  /** Format.Hours: "142h 30m". */
  function hours(totalSeconds) {
    if (!isFiniteNumber(totalSeconds)) return '--';
    var h = Math.floor(totalSeconds / 3600);
    var m = Math.floor((totalSeconds % 3600) / 60);
    return h >= 1 ? h + 'h ' + m + 'm' : m + 'm';
  }

  /** A single match: "13m 15s". */
  function shortDuration(totalSeconds) {
    if (!isFiniteNumber(totalSeconds)) return '--';
    var m = Math.floor(totalSeconds / 60);
    var s = Math.round(totalSeconds % 60);
    if (m >= 60) return hours(totalSeconds);
    return m + 'm ' + pad2(s) + 's';
  }

  /* ----------------------------------------------------------------- DOM */

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    applyAttrs(node, attrs);
    append(node, children);
    return node;
  }

  var SVG_NS = 'http://www.w3.org/2000/svg';

  function svg(tag, attrs, children) {
    var node = document.createElementNS(SVG_NS, tag);
    if (attrs) {
      for (var k in attrs) {
        if (!Object.prototype.hasOwnProperty.call(attrs, k)) continue;
        var v = attrs[k];
        if (v === null || v === undefined || v === false) continue;
        // "text" is content, not an attribute -- and it is always API data, so it
        // goes in as a text node and never as markup.
        if (k === 'text') node.textContent = String(v);
        else node.setAttribute(k, String(v));
      }
    }
    append(node, children);
    return node;
  }

  function applyAttrs(node, attrs) {
    if (!attrs) return;
    for (var k in attrs) {
      if (!Object.prototype.hasOwnProperty.call(attrs, k)) continue;
      var v = attrs[k];
      if (v === null || v === undefined || v === false) continue;
      if (k === 'class') node.className = v;
      else if (k === 'text') node.textContent = String(v); // never innerHTML: labels are API data
      else if (k === 'style') node.setAttribute('style', v);
      else if (k === 'hidden') node.hidden = !!v;
      else if (k.slice(0, 2) === 'on' && typeof v === 'function') {
        node.addEventListener(k.slice(2), v);
      } else node.setAttribute(k, String(v));
    }
  }

  function append(node, children) {
    if (children === null || children === undefined) return;
    if (!Array.isArray(children)) children = [children];
    for (var i = 0; i < children.length; i++) {
      var c = children[i];
      if (c === null || c === undefined || c === false) continue;
      node.appendChild(typeof c === 'string' || typeof c === 'number'
        ? document.createTextNode(String(c))
        : c);
    }
  }

  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
    return node;
  }

  /* --------------------------------------------------------------- maths */

  function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }

  function extent(values) {
    var lo = Infinity, hi = -Infinity;
    for (var i = 0; i < values.length; i++) {
      var v = values[i];
      if (!isFiniteNumber(v)) continue;
      if (v < lo) lo = v;
      if (v > hi) hi = v;
    }
    return isFinite(lo) ? [lo, hi] : [0, 1];
  }

  /** Axis ticks on 1 / 2 / 2.5 / 5 x 10^n boundaries, so labels stay round. */
  function niceTicks(lo, hi, target) {
    var count = target || 5;
    if (!isFiniteNumber(lo) || !isFiniteNumber(hi)) return { lo: 0, hi: 1, ticks: [0, 1], step: 1 };
    if (hi - lo < 1e-9) {
      var pad = Math.abs(hi) > 1e-9 ? Math.abs(hi) * 0.1 : 0.5;
      lo -= pad; hi += pad;
    }
    var raw = (hi - lo) / count;
    var mag = Math.pow(10, Math.floor(Math.log(raw) / Math.LN10));
    var norm = raw / mag;
    var step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10) * mag;
    var start = Math.floor(lo / step) * step;
    var end = Math.ceil(hi / step) * step;
    var ticks = [];
    // Guard the loop: floating point plus a pathological range must not hang a tab.
    for (var v = start, guard = 0; v <= end + step * 0.5 && guard < 200; v += step, guard++) {
      ticks.push(Math.abs(v) < step * 1e-9 ? 0 : v);
    }
    return { lo: start, hi: end, ticks: ticks, step: step };
  }

  /** Every SVG coordinate goes through this: one NaN silently voids a whole path. */
  function safe(n, fallback) {
    return isFiniteNumber(n) ? Math.round(n * 100) / 100 : (fallback === undefined ? 0 : fallback);
  }

  EET.util = {
    MONTHS: MONTHS,
    group: group,
    fixed: fixed,
    isFiniteNumber: isFiniteNumber,
    ratio: ratio,
    integer: integer,
    percent: percent,
    signed: signed,
    isPercentUnit: isPercentUnit,
    unitIsFraction: unitIsFraction,
    seriesValue: seriesValue,
    toDate: toDate,
    isoDay: isoDay,
    dayMonth: dayMonth,
    dayMonthYear: dayMonthYear,
    dayMonthTime: dayMonthTime,
    daysBetween: daysBetween,
    timeSpanSeconds: timeSpanSeconds,
    hours: hours,
    shortDuration: shortDuration,
    el: el,
    svg: svg,
    clear: clear,
    append: append,
    clamp: clamp,
    extent: extent,
    niceTicks: niceTicks,
    safe: safe
  };
}(typeof globalThis !== 'undefined' ? globalThis : this));
