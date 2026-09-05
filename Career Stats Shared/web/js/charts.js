/*
 * Hand-written inline SVG charts. No library, no CDN, no bundler.
 *
 * Three forms, chosen for the job each does:
 *   trendChart  - change over time for ONE metric. One series, one y-axis. Two
 *                 marks: the weighted daily points, and the server's EWMA line.
 *                 Days built from few matches are drawn hollow and small, because
 *                 a K/D of 4.0 over one game is noise and every other tracker
 *                 plots it the same size as fifty games.
 *   sparkline   - the stat-tile figure: 12 points, de-emphasised, accent at the end.
 *   barChart    - magnitude across nominal categories (maps, modes). One hue for
 *                 every bar; the length is the value, so hue has nothing to say.
 *
 * Never a second y-axis. Two measures of different scale get two charts.
 */
(function (root) {
  'use strict';

  var EET = (root.EET = root.EET || {});
  var U = EET.util;
  var svg = U.svg;
  var el = U.el;

  var FONT = '11px system-ui, -apple-system, "Segoe UI", sans-serif';
  var measureCanvas = null;

  function textWidth(text, font) {
    if (!measureCanvas) measureCanvas = document.createElement('canvas');
    var ctx = measureCanvas.getContext('2d');
    if (!ctx) return String(text).length * 6.2;
    ctx.font = font || FONT;
    return ctx.measureText(String(text)).width;
  }

  /** Ellipsise to fit a pixel width, so a long map name can never overrun its column. */
  function fit(text, maxWidth, font) {
    text = String(text);
    if (textWidth(text, font) <= maxWidth) return text;
    var out = text;
    while (out.length > 1 && textWidth(out + '…', font) > maxWidth) {
      out = out.slice(0, -1);
    }
    return out + '…';
  }

  /**
   * Enough decimals to write the tick STEP exactly -- a 0.25 step printed to one
   * decimal gives an axis reading 1.0 / 1.3 / 1.5 / 1.8, which is a lie about
   * where the gridlines are.
   */
  function axisDecimals(step, scaleToPercent) {
    var s = Math.abs(scaleToPercent ? step * 100 : step);
    if (!(s > 0)) return 2;
    for (var d = 0; d <= 3; d++) {
      var scaled = s * Math.pow(10, d);
      if (Math.abs(scaled - Math.round(scaled)) < 1e-9) return d;
    }
    return 3;
  }

  function makeTooltip(holder) {
    var tip = holder.querySelector('.tooltip');
    if (!tip) {
      tip = el('div', { class: 'tooltip', role: 'presentation' });
      tip.hidden = true;
      holder.appendChild(tip);
    }
    return tip;
  }

  function placeTooltip(tip, holder, x, y) {
    var width = holder.clientWidth || 320;
    var tw = tip.offsetWidth || 140;
    var th = tip.offsetHeight || 70;
    var left = U.clamp(x, tw / 2 + 4, Math.max(tw / 2 + 4, width - tw / 2 - 4));
    tip.style.left = left + 'px';
    // Sits above the mark by default; flips below when there is no room, so it is
    // never clipped by the top of the card.
    var below = y - th - 8 < 0;
    tip.className = below ? 'tooltip tooltip--below' : 'tooltip';
    tip.style.top = (below ? y + 20 : y) + 'px';
  }

  function liveRegion(holder) {
    var live = holder.querySelector('.chart-live');
    if (!live) {
      live = el('div', { class: 'chart-live visually-hidden', 'aria-live': 'polite' });
      holder.appendChild(live);
    }
    return live;
  }

  /* ===================================================== trend line chart */

  /**
   * @param {HTMLElement} holder    position:relative container
   * @param {object} series         a normalised TrendSeries
   * @param {object} opts           { height, windowDays }
   */
  function trendChart(holder, series, opts) {
    opts = opts || {};
    U.clear(holder);

    var all = series.points || [];
    var points = all;
    var smoothed = series.smoothed || [];
    if (opts.windowDays && all.length) {
      var last = all[all.length - 1].date.getTime();
      var from = last - opts.windowDays * 86400000;
      points = all.filter(function (p) { return p.date.getTime() >= from; });
    }
    if (!points.length) {
      holder.appendChild(el('div', { class: 'empty', text: 'No points in this window.' }));
      return;
    }
    var offset = points[0].index;

    var width = Math.max(260, Math.floor(holder.clientWidth || opts.width || 640));
    var compact = width < 520;
    var height = opts.height || (compact ? 220 : 300);

    /* ---- scales ------------------------------------------------------- */
    var values = [];
    for (var i = 0; i < points.length; i++) {
      values.push(points[i].value);
      var s = smoothed[points[i].index];
      if (U.isFiniteNumber(s)) values.push(s);
    }
    var span = U.extent(values);
    var pad = (span[1] - span[0]) * 0.08 || Math.max(0.05, Math.abs(span[1]) * 0.05);
    var lo = span[0] - pad;
    var hi = span[1] + pad;
    // A fraction-unit series is bounded and the padding must not pretend otherwise. A
    // win rate cannot be -25% or 125%; offering those as gridlines invites the reader to
    // place the line against values that cannot occur, and wastes a third of the plot.
    if (series.scaleToPercent && span[0] >= 0 && span[1] <= 1) {
      lo = Math.max(0, lo);
      hi = Math.min(1, hi);
    }
    var scale = U.niceTicks(lo, hi, compact ? 4 : 5);
    var decimals = axisDecimals(scale.step, series.scaleToPercent);

    var tickLabels = scale.ticks.map(function (t) {
      return U.seriesValue(t, series.unit, series.scaleToPercent, decimals);
    });
    var widest = 0;
    for (var t = 0; t < tickLabels.length; t++) {
      widest = Math.max(widest, textWidth(tickLabels[t]));
    }

    var endValue = smoothed[points[points.length - 1].index];
    // The end label is read against the picker card and the tooltip, not against the
    // axis, so it keeps the metric's natural precision rather than the tick's.
    var endLabel = U.seriesValue(endValue, series.unit, series.scaleToPercent);
    var endLabelWidth = textWidth(endLabel, '600 12px system-ui, sans-serif');

    var m = {
      top: 16,
      right: Math.min(96, Math.max(30, endLabelWidth + 18)),
      bottom: 26,
      left: Math.max(30, widest + 12)
    };
    var plotW = Math.max(40, width - m.left - m.right);
    var plotH = Math.max(60, height - m.top - m.bottom);

    var t0 = points[0].date.getTime();
    var t1 = points[points.length - 1].date.getTime();
    if (t1 === t0) { t0 -= 43200000; t1 += 43200000; }

    function xOf(date) { return m.left + ((date.getTime() - t0) / (t1 - t0)) * plotW; }
    function yOf(value) {
      var frac = (value - scale.lo) / (scale.hi - scale.lo || 1);
      return m.top + (1 - frac) * plotH;
    }

    var node = svg('svg', {
      viewBox: '0 0 ' + width + ' ' + height,
      width: width,
      height: height,
      role: 'img',
      'aria-label': chartSummary(series, points),
      preserveAspectRatio: 'xMidYMid meet'
    });

    /* ---- grid & axes (recessive, hairline, solid) ---------------------- */
    var grid = svg('g', null);
    for (var g = 0; g < scale.ticks.length; g++) {
      var gy = U.safe(yOf(scale.ticks[g]));
      if (gy < m.top - 1 || gy > m.top + plotH + 1) continue;
      grid.appendChild(svg('line', {
        x1: m.left, x2: U.safe(m.left + plotW), y1: gy, y2: gy,
        stroke: 'var(--grid)', 'stroke-width': 1, 'shape-rendering': 'crispEdges'
      }));
      grid.appendChild(svg('text', {
        x: U.safe(m.left - 8), y: gy + 3.5,
        'text-anchor': 'end', fill: 'var(--ink-muted)',
        'font-size': 11, 'font-variant-numeric': 'tabular-nums',
        text: tickLabels[g]
      }));
    }
    node.appendChild(grid);

    var axisY = U.safe(m.top + plotH);
    node.appendChild(svg('line', {
      x1: m.left, x2: U.safe(m.left + plotW), y1: axisY, y2: axisY,
      stroke: 'var(--axis)', 'stroke-width': 1, 'shape-rendering': 'crispEdges'
    }));

    var xTickCount = compact ? 3 : Math.min(6, Math.max(2, Math.floor(plotW / 92)));
    var seenX = {};
    for (var xt = 0; xt < xTickCount; xt++) {
      var frac = xTickCount === 1 ? 0.5 : xt / (xTickCount - 1);
      var when = new Date(t0 + frac * (t1 - t0));
      var label = U.dayMonth(when);
      if (seenX[label]) continue;
      seenX[label] = true;
      var tx = U.clamp(m.left + frac * plotW, m.left + textWidth(label) / 2,
        m.left + plotW - textWidth(label) / 2);
      node.appendChild(svg('text', {
        x: U.safe(tx), y: U.safe(axisY + 15),
        'text-anchor': 'middle', fill: 'var(--ink-muted)', 'font-size': 11,
        text: label
      }));
    }

    /* ---- raw daily points: size and fill carry the sample count -------- */
    var cutoff = series.lowSampleCutoff || 3;
    var maxSamples = Math.max(cutoff + 1, series.maxSamples || 1);
    var dots = svg('g', null);
    var geometry = [];
    for (var p = 0; p < points.length; p++) {
      var pt = points[p];
      var cx = U.safe(xOf(pt.date));
      var cy = U.safe(yOf(pt.value));
      var low = pt.samples < cutoff;
      var norm = U.clamp((pt.samples - cutoff) / (maxSamples - cutoff), 0, 1);
      var r = low ? 4 : 4 + 3 * Math.sqrt(norm);
      geometry.push({ point: pt, cx: cx, cy: cy, r: r, low: low });
      dots.appendChild(svg('circle', low
        ? {
          cx: cx, cy: cy, r: 4,
          fill: 'var(--surface-1)', stroke: series.color, 'stroke-width': 1.5,
          'stroke-opacity': 0.75
        }
        : {
          cx: cx, cy: cy, r: U.safe(r),
          fill: series.color, 'fill-opacity': U.safe(0.5 + 0.4 * norm),
          stroke: 'var(--surface-1)', 'stroke-width': 2
        }));
    }
    node.appendChild(dots);

    /* ---- the smoothed line -------------------------------------------- */
    var d = '';
    for (var q = 0; q < points.length; q++) {
      var sv = smoothed[points[q].index];
      if (!U.isFiniteNumber(sv)) continue;
      d += (d ? 'L' : 'M') + U.safe(xOf(points[q].date)) + ' ' + U.safe(yOf(sv));
    }
    if (d) {
      node.appendChild(svg('path', {
        d: d, fill: 'none', stroke: series.color, 'stroke-width': 2,
        'stroke-linejoin': 'round', 'stroke-linecap': 'round'
      }));
    }

    /* ---- direct end label (also the relief for the light-mode contrast
           WARN on slot 3: the series always carries a visible label) ------ */
    if (U.isFiniteNumber(endValue)) {
      var ex = U.safe(xOf(points[points.length - 1].date));
      var ey = U.safe(yOf(endValue));
      node.appendChild(svg('circle', {
        cx: ex, cy: ey, r: 4.5, fill: series.color,
        stroke: 'var(--surface-1)', 'stroke-width': 2
      }));
      node.appendChild(svg('text', {
        x: U.safe(Math.min(ex + 9, width - endLabelWidth - 2)),
        y: U.safe(U.clamp(ey + 4, m.top + 4, m.top + plotH)),
        fill: 'var(--ink-1)', 'font-size': 12, 'font-weight': 600,
        'font-variant-numeric': 'tabular-nums',
        text: endLabel
      }));
    }

    /* ---- hover layer: crosshair snapped to the nearest day ------------- */
    var hover = svg('g', { visibility: 'hidden' });
    var crosshair = svg('line', {
      y1: m.top, y2: axisY, stroke: 'var(--axis)', 'stroke-width': 1,
      'shape-rendering': 'crispEdges'
    });
    var marker = svg('circle', {
      r: 6, fill: 'none', stroke: series.color, 'stroke-width': 2
    });
    hover.appendChild(crosshair);
    hover.appendChild(marker);
    node.appendChild(hover);

    node.appendChild(svg('rect', {
      x: m.left, y: m.top, width: U.safe(plotW), height: U.safe(plotH),
      fill: 'transparent', 'pointer-events': 'all', class: 'chart-overlay'
    }));

    holder.appendChild(node);
    var tip = makeTooltip(holder);
    var live = liveRegion(holder);

    var active = -1;
    function show(index, announce) {
      if (index < 0 || index >= geometry.length) return;
      active = index;
      var gm = geometry[index];
      hover.setAttribute('visibility', 'visible');
      crosshair.setAttribute('x1', gm.cx);
      crosshair.setAttribute('x2', gm.cx);
      marker.setAttribute('cx', gm.cx);
      marker.setAttribute('cy', gm.cy);
      renderTip(tip, series, gm.point, smoothed[gm.point.index]);
      tip.hidden = false;
      placeTooltip(tip, holder, gm.cx, gm.cy - 14);
      if (announce) live.textContent = tipText(series, gm.point, smoothed[gm.point.index]);
    }
    function hide() {
      active = -1;
      hover.setAttribute('visibility', 'hidden');
      tip.hidden = true;
    }

    function nearest(clientX) {
      var rect = node.getBoundingClientRect();
      var ratio = rect.width ? width / rect.width : 1;
      var x = (clientX - rect.left) * ratio;
      var best = 0, bestD = Infinity;
      for (var i2 = 0; i2 < geometry.length; i2++) {
        var dx = Math.abs(geometry[i2].cx - x);
        if (dx < bestD) { bestD = dx; best = i2; }
      }
      return best;
    }

    node.addEventListener('pointermove', function (e) { show(nearest(e.clientX), false); });
    node.addEventListener('pointerleave', hide);
    node.addEventListener('pointerdown', function (e) { show(nearest(e.clientX), true); });

    holder.tabIndex = 0;
    holder.setAttribute('role', 'application');
    holder.setAttribute('aria-label', chartSummary(series, points) +
      ' Use the left and right arrow keys to read each day.');
    holder.addEventListener('keydown', function (e) {
      var key = e.key;
      if (key === 'ArrowRight' || key === 'ArrowLeft') {
        var next = active < 0
          ? (key === 'ArrowRight' ? 0 : geometry.length - 1)
          : U.clamp(active + (key === 'ArrowRight' ? 1 : -1), 0, geometry.length - 1);
        show(next, true);
        e.preventDefault();
      } else if (key === 'Home') { show(0, true); e.preventDefault(); }
      else if (key === 'End') { show(geometry.length - 1, true); e.preventDefault(); }
      else if (key === 'Escape') { hide(); }
    });
    holder.addEventListener('blur', hide);

    return { points: points, offset: offset };
  }

  function chartSummary(series, points) {
    var first = points[0], last = points[points.length - 1];
    return series.label + ', ' + points.length + ' days from ' +
      U.dayMonthYear(first.date) + ' to ' + U.dayMonthYear(last.date) +
      '. Server-tested direction: ' + series.direction + '.';
  }

  function tipText(series, point, smoothedValue) {
    return U.dayMonthYear(point.date) + ': ' +
      U.seriesValue(point.value, series.unit, series.scaleToPercent) + ' from ' +
      point.samples + (point.samples === 1 ? ' match' : ' matches') + ', smoothed ' +
      U.seriesValue(smoothedValue, series.unit, series.scaleToPercent) + '.';
  }

  function renderTip(tip, series, point, smoothedValue) {
    U.clear(tip);
    tip.appendChild(el('div', { class: 'tooltip__date', text: U.dayMonthYear(point.date) }));
    tip.appendChild(el('div', { class: 'tooltip__row' }, [
      el('span', {
        class: 'tooltip__key tooltip__key--hollow',
        style: 'border-color:' + series.color + ';'
      }),
      el('span', {
        class: 'tooltip__val',
        text: U.seriesValue(point.value, series.unit, series.scaleToPercent)
      }),
      el('span', { class: 'tooltip__lab', text: 'daily average' })
    ]));
    tip.appendChild(el('div', { class: 'tooltip__row' }, [
      el('span', { class: 'tooltip__key', style: 'background:' + series.color + ';' }),
      el('span', {
        class: 'tooltip__val',
        text: U.seriesValue(smoothedValue, series.unit, series.scaleToPercent)
      }),
      el('span', { class: 'tooltip__lab', text: 'smoothed' })
    ]));
    tip.appendChild(el('div', { class: 'tooltip__row' }, [
      el('span', { class: 'tooltip__key', style: 'background:transparent;' }),
      el('span', { class: 'tooltip__val', text: String(point.samples) }),
      el('span', {
        class: 'tooltip__lab',
        text: (point.samples === 1 ? 'match' : 'matches') +
          (point.samples < (series.lowSampleCutoff || 3) ? ' - thin evidence' : '')
      })
    ]));
  }

  /* ============================================================ sparkline */

  /** The stat-tile figure: de-emphasised line, accent on the current end. */
  function sparkline(holder, series, opts) {
    opts = opts || {};
    U.clear(holder);
    var pts = series.points || [];
    var smoothed = series.smoothed || [];
    if (pts.length < 2) return;

    var slice = pts;
    if (opts.windowDays && pts.length) {
      // Same window the section filter applies to the big chart, so the small
      // multiples never show a different stretch of time than the chart above them.
      var from = pts[pts.length - 1].date.getTime() - opts.windowDays * 86400000;
      slice = pts.filter(function (p) { return p.date.getTime() >= from; });
    }
    var take = opts.take || 12;
    slice = slice.slice(Math.max(0, slice.length - take));
    if (slice.length < 2) return;
    var width = Math.max(60, Math.floor(holder.clientWidth || opts.width || 160));
    var height = opts.height || 30;
    var padY = 4;

    var vals = slice.map(function (p) {
      var s = smoothed[p.index];
      return U.isFiniteNumber(s) ? s : p.value;
    });
    var span = U.extent(vals);
    var range = (span[1] - span[0]) || Math.max(0.001, Math.abs(span[1]) * 0.1);

    function x(i) { return (i / (slice.length - 1)) * (width - 6) + 3; }
    function y(v) { return padY + (1 - (v - span[0]) / range) * (height - padY * 2); }

    var node = svg('svg', {
      viewBox: '0 0 ' + width + ' ' + height, width: width, height: height,
      'aria-hidden': 'true', focusable: 'false', preserveAspectRatio: 'none'
    });

    var d = '';
    for (var i = 0; i < vals.length; i++) {
      d += (d ? 'L' : 'M') + U.safe(x(i)) + ' ' + U.safe(y(vals[i]));
    }
    node.appendChild(svg('path', {
      d: d, fill: 'none', stroke: 'var(--ink-muted)', 'stroke-width': 1.5,
      'stroke-opacity': 0.85, 'stroke-linejoin': 'round', 'stroke-linecap': 'round'
    }));

    if (vals.length >= 2) {
      var n = vals.length - 1;
      node.appendChild(svg('path', {
        d: 'M' + U.safe(x(n - 1)) + ' ' + U.safe(y(vals[n - 1])) +
          'L' + U.safe(x(n)) + ' ' + U.safe(y(vals[n])),
        fill: 'none', stroke: opts.color || series.color, 'stroke-width': 2,
        'stroke-linecap': 'round'
      }));
      node.appendChild(svg('circle', {
        cx: U.safe(x(n)), cy: U.safe(y(vals[n])), r: 3,
        fill: opts.color || series.color, stroke: 'var(--surface-1)', 'stroke-width': 1.5
      }));
    }
    holder.appendChild(node);
  }

  /* =========================================================== rank chart */

  /**
   * Magnitude across nominal categories (maps, modes). One hue for every row --
   * the position or the length is the value, so hue has nothing left to say.
   *
   * Two forms, and picking between them is the whole point:
   *
   *   BARS  when zero is a meaningful floor and the spread reaches it. Length
   *         encodes the value, so the axis MUST start at zero.
   *   DOTS  when every value sits in a narrow band far from zero -- six maps
   *         between 1.06 and 1.22 K/D. Drawn as bars from zero those are six
   *         identical full-width blocks; truncating the axis to separate them
   *         would be the classic lying bar chart. A dot plot changes the
   *         encoding from length to position, and a position scale is allowed
   *         not to start at zero, so the differences become readable honestly.
   *
   * Either way rows built from few matches are washed back or drawn hollow,
   * never silently dropped: a 3-match map must not read like form.
   */
  function barChart(holder, breakdown, opts) {
    opts = opts || {};
    U.clear(holder);
    var rows = (breakdown.rows || []).slice(0, opts.limit || 12);
    if (!rows.length) {
      holder.appendChild(el('div', { class: 'empty', text: 'Nothing to rank yet.' }));
      return;
    }

    var color = opts.color || 'var(--series-1)';
    var width = Math.max(240, Math.floor(holder.clientWidth || opts.width || 420));
    var compact = width < 360;
    var rowH = 30, barH = 16, top = 8;

    var maxSamples = 1, maxValue = -Infinity, minValue = Infinity, valueWidth = 34;
    for (var i = 0; i < rows.length; i++) {
      maxSamples = Math.max(maxSamples, rows[i].samples || 0);
      maxValue = Math.max(maxValue, rows[i].value);
      minValue = Math.min(minValue, rows[i].value);
      valueWidth = Math.max(valueWidth,
        textWidth(rows[i].formatted, '600 12px system-ui, sans-serif') + 10);
    }
    var spread = maxValue - minValue;
    var dotMode = minValue > 0 && maxValue > 0 && spread / maxValue < 0.5;
    var axisBand = dotMode ? 26 : 14;
    var height = top + rows.length * rowH + axisBand;

    var labelW = U.clamp(width * 0.34, 60, 168);
    var plotLeft = labelW + 10;
    var plotRight = Math.max(plotLeft + 40, width - valueWidth - 4);
    var plotW = plotRight - plotLeft;

    var node = svg('svg', {
      viewBox: '0 0 ' + width + ' ' + height, width: width, height: height,
      role: 'img',
      'aria-label': breakdown.label + ': ' + rows.length + ' rows measured in ' +
        breakdown.valueLabel + ', ranked. Best ' + rows[0].name + ' at ' + rows[0].formatted +
        ', lowest ' + rows[rows.length - 1].name + ' at ' + rows[rows.length - 1].formatted + '.',
      preserveAspectRatio: 'xMidYMid meet'
    });

    var lowCut = Math.max(3, Math.round(maxSamples * 0.2));
    var geometry = [];
    var scale = null;
    var xOf;

    if (dotMode) {
      var pad = spread * 0.25 || Math.abs(maxValue) * 0.05;
      var dotLo = minValue - pad;
      var dotHi = maxValue + pad;
      // Same bound as the trend chart: a win-rate row at 0.97 must not produce a 105% tick.
      if (opts.scaleToPercent && minValue >= 0 && maxValue <= 1) {
        dotLo = Math.max(0, dotLo);
        dotHi = Math.min(1, dotHi);
      }
      scale = U.niceTicks(dotLo, dotHi, compact ? 3 : 4);
      xOf = function (v) {
        return plotLeft + (v - scale.lo) / (scale.hi - scale.lo || 1) * plotW;
      };
      var decimals = axisDecimals(scale.step, opts.scaleToPercent);
      var baseY = top + rows.length * rowH;
      for (var g = 0; g < scale.ticks.length; g++) {
        var gx = U.safe(xOf(scale.ticks[g]));
        if (gx < plotLeft - 1 || gx > plotRight + 1) continue;
        node.appendChild(svg('line', {
          x1: gx, x2: gx, y1: top, y2: U.safe(baseY),
          stroke: 'var(--grid)', 'stroke-width': 1, 'shape-rendering': 'crispEdges'
        }));
        var label = opts.formatTick
          ? opts.formatTick(scale.ticks[g], decimals)
          : U.fixed(scale.ticks[g], decimals);
        node.appendChild(svg('text', {
          x: U.safe(U.clamp(gx, plotLeft + textWidth(label) / 2, plotRight)),
          y: U.safe(baseY + 16),
          'text-anchor': 'middle', fill: 'var(--ink-muted)', 'font-size': 11,
          'font-variant-numeric': 'tabular-nums',
          text: label
        }));
      }
    } else {
      var lo = Math.min(0, minValue);
      var hi = Math.max(maxValue, lo + 1e-6);
      xOf = function (v) { return plotLeft + (v - lo) / (hi - lo) * plotW; };
    }

    for (var r = 0; r < rows.length; r++) {
      var row = rows[r];
      var y = top + r * rowH;
      var cy = y + rowH / 2;
      var low = (row.samples || 0) < lowCut;

      node.appendChild(svg('text', {
        x: U.safe(labelW), y: U.safe(cy + 4), 'text-anchor': 'end',
        fill: low ? 'var(--ink-muted)' : 'var(--ink-2)', 'font-size': 12,
        text: fit(row.name, labelW - 4, '12px system-ui, sans-serif')
      }));

      var cx;
      if (dotMode) {
        cx = U.safe(xOf(row.value));
        // A hairline track carries the eye from the name to the dot.
        node.appendChild(svg('line', {
          x1: U.safe(plotLeft), x2: U.safe(plotRight), y1: U.safe(cy), y2: U.safe(cy),
          stroke: 'var(--grid)', 'stroke-width': 1, 'shape-rendering': 'crispEdges'
        }));
        node.appendChild(svg('circle', low
          ? {
            cx: cx, cy: U.safe(cy), r: 5,
            fill: 'var(--surface-1)', stroke: color, 'stroke-width': 1.5, 'stroke-opacity': 0.8
          }
          : {
            cx: cx, cy: U.safe(cy), r: 5.5,
            fill: color, stroke: 'var(--surface-1)', 'stroke-width': 2
          }));
      } else {
        var zeroX = U.safe(xOf(Math.max(0, Math.min(0, minValue))));
        var end = U.safe(xOf(row.value));
        var negative = end < zeroX;
        var barX = negative ? end : zeroX;
        var barW = Math.max(Math.abs(end - zeroX), 1.5);
        var barColor = negative ? 'var(--series-2)' : color;
        node.appendChild(svg('rect', {
          x: U.safe(barX), y: U.safe(cy - barH / 2), width: U.safe(barW), height: barH,
          rx: 4, ry: 4, fill: barColor, 'fill-opacity': low ? 0.42 : 1
        }));
        if (barW > 5) {
          // Square the baseline end; only the data end is rounded.
          node.appendChild(svg('rect', {
            x: U.safe(negative ? zeroX - 4 : zeroX), y: U.safe(cy - barH / 2),
            width: 4, height: barH, fill: barColor, 'fill-opacity': low ? 0.42 : 1
          }));
        }
        cx = negative ? (barX + barW / 2) : (zeroX + barW / 2);
      }

      // The value always has its own column, so a label can never be clipped.
      node.appendChild(svg('text', {
        x: U.safe(plotRight + 8), y: U.safe(cy + 4), 'text-anchor': 'start',
        fill: 'var(--ink-1)', 'font-size': 12, 'font-weight': 600,
        'font-variant-numeric': 'tabular-nums',
        text: row.formatted
      }));

      geometry.push({ row: row, y: y, cy: cy, low: low, tipX: cx });
      node.appendChild(svg('rect', {
        x: 0, y: U.safe(y), width: width, height: rowH,
        fill: 'transparent', 'pointer-events': 'all'
      }));
    }

    holder.appendChild(node);

    var tip = makeTooltip(holder);
    var live = liveRegion(holder);
    var hoverRect = null;
    var active = -1;

    function show(index, announce) {
      if (index < 0 || index >= geometry.length) return;
      active = index;
      var gm = geometry[index];
      if (!hoverRect) {
        hoverRect = svg('rect', { fill: 'var(--hover-wash)', rx: 6, ry: 6, 'pointer-events': 'none' });
        node.insertBefore(hoverRect, node.firstChild);
      }
      hoverRect.setAttribute('x', 0);
      hoverRect.setAttribute('y', U.safe(gm.y));
      hoverRect.setAttribute('width', width);
      hoverRect.setAttribute('height', rowH);
      hoverRect.setAttribute('visibility', 'visible');

      U.clear(tip);
      tip.appendChild(el('div', { class: 'tooltip__date', text: gm.row.name }));
      tip.appendChild(el('div', { class: 'tooltip__row' }, [
        el('span', {
          class: gm.low ? 'tooltip__key tooltip__key--hollow' : 'tooltip__key',
          style: gm.low ? 'border-color:' + color + ';' : 'background:' + color + ';'
        }),
        el('span', { class: 'tooltip__val', text: gm.row.formatted }),
        el('span', { class: 'tooltip__lab', text: breakdown.valueLabel })
      ]));
      tip.appendChild(el('div', { class: 'tooltip__row' }, [
        el('span', { class: 'tooltip__key', style: 'background:transparent;' }),
        el('span', { class: 'tooltip__val', text: U.integer(gm.row.samples) }),
        el('span', {
          class: 'tooltip__lab',
          text: (gm.row.samples === 1 ? 'match' : 'matches') + (gm.low ? ' - thin evidence' : '')
        })
      ]));
      if (gm.row.share !== null && gm.row.share !== undefined) {
        tip.appendChild(el('div', { class: 'tooltip__row' }, [
          el('span', { class: 'tooltip__key', style: 'background:transparent;' }),
          el('span', { class: 'tooltip__val', text: U.percent(gm.row.share, 1) }),
          el('span', { class: 'tooltip__lab', text: 'of matches' })
        ]));
      }
      tip.hidden = false;
      placeTooltip(tip, holder, gm.tipX, gm.cy - 10);
      if (announce) {
        live.textContent = gm.row.name + ': ' + gm.row.formatted + ' ' + breakdown.valueLabel +
          ', from ' + gm.row.samples + ' matches.';
      }
    }
    function hide() {
      active = -1;
      if (hoverRect) hoverRect.setAttribute('visibility', 'hidden');
      tip.hidden = true;
    }

    node.addEventListener('pointermove', function (e) {
      var rect = node.getBoundingClientRect();
      var ratio = rect.height ? height / rect.height : 1;
      var y = (e.clientY - rect.top) * ratio;
      var index = Math.floor((y - top) / rowH);
      if (index < 0 || index >= geometry.length) hide(); else show(index, false);
    });
    node.addEventListener('pointerleave', hide);

    holder.tabIndex = 0;
    holder.setAttribute('role', 'application');
    holder.setAttribute('aria-label', breakdown.label +
      '. Use the up and down arrow keys to read each row.');
    holder.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        var next = active < 0
          ? (e.key === 'ArrowDown' ? 0 : geometry.length - 1)
          : U.clamp(active + (e.key === 'ArrowDown' ? 1 : -1), 0, geometry.length - 1);
        show(next, true);
        e.preventDefault();
      } else if (e.key === 'Escape') hide();
    });
    holder.addEventListener('blur', hide);

    return { form: dotMode ? 'dots' : 'bars' };
  }

  EET.charts = {
    trendChart: trendChart,
    sparkline: sparkline,
    barChart: barChart,
    textWidth: textWidth,
    fit: fit
  };
}(typeof globalThis !== 'undefined' ? globalThis : this));
