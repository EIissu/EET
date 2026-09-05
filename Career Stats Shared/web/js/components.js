/*
 * The dashboard's pieces.
 *
 * Two things earn the page its keep, and they are the two the owner picked:
 *   1. the top of the page answers "how am I doing" without scrolling, and
 *   2. the trend section shows change over time honestly -- the server's
 *      significance-tested Direction word rendered as given, never recomputed
 *      and never upgraded to something more flattering.
 *
 * Every chart on the page has a table view. That is an accessibility twin in its
 * own right and it is also the standing relief for the light-mode contrast WARN
 * on the third categorical slot.
 */
(function (root) {
  'use strict';

  var EET = (root.EET = root.EET || {});
  var U = EET.util;
  var el = U.el;

  var ARROW_UP = '▲';
  var ARROW_DOWN = '▼';
  var FLAT = '–';
  var WIN = '●';

  /* ------------------------------------------------------------ fragments */

  /**
   * A delta chip. Three states that must not be conflated:
   *   null delta  -> "no prior window" (there was nothing to compare against)
   *   zero delta  -> "no change" (there was, and it did not move)
   *   otherwise   -> arrow in the direction of travel, coloured by Kpi.Improved
   * The arrow is the coloured element; the words stay in text ink, so the meaning
   * never rests on hue alone.
   */
  function deltaChip(kpi) {
    if (kpi.delta === null || kpi.delta === undefined) {
      return el('span', {
        class: 'delta delta--none',
        text: 'no prior window'
      });
    }
    var zero = kpi.delta === 0;
    var cls = 'delta ' + (zero || kpi.improved === null ? 'delta--flat'
      : kpi.improved ? 'delta--good' : 'delta--bad');
    var arrow = zero ? FLAT : kpi.delta > 0 ? ARROW_UP : ARROW_DOWN;
    var word = zero ? 'no change'
      : kpi.improved === null ? 'vs previous'
        : kpi.improved ? 'better' : 'worse';
    return el('span', { class: cls }, [
      el('span', { class: 'delta__arrow', 'aria-hidden': 'true', text: arrow }),
      el('span', { class: 'delta__num', text: kpi.deltaFormatted || U.signed(kpi.delta) }),
      el('span', { class: 'delta__word', text: word })
    ]);
  }

  /** The server already decided this word. Render it, do not second-guess it. */
  function directionBadge(series) {
    var word = String(series.direction || 'steady');
    var lower = word.toLowerCase();
    var good = lower === 'improving';
    var bad = lower === 'declining';
    var glyph = lower === 'improving' || lower === 'rising' ? ARROW_UP
      : lower === 'declining' || lower === 'falling' ? ARROW_DOWN
        : FLAT;
    return el('span', {
      class: 'direction ' + (good ? 'direction--good' : bad ? 'direction--bad' : 'direction--flat'),
      title: 'Significance-tested on the server: a slope that cannot be told from noise is reported as steady.'
    }, [
      el('span', { class: 'direction__glyph', 'aria-hidden': 'true', text: glyph }),
      el('span', { text: word.charAt(0).toUpperCase() + word.slice(1) })
    ]);
  }

  function tableFrom(columns, rows, caption) {
    var thead = el('thead', null, el('tr', null, columns.map(function (c) {
      return el('th', {
        class: c.num ? 'num' : null,
        scope: 'col',
        text: c.label
      });
    })));
    var tbody = el('tbody', null, rows.map(function (r) {
      return el('tr', null, columns.map(function (c, i) {
        var value = c.get(r);
        var cell = el(i === 0 ? 'th' : 'td', {
          class: (c.num ? 'num' : '') + (c.dim ? ' dim' : ''),
          scope: i === 0 ? 'row' : null
        });
        if (value && value.nodeType) cell.appendChild(value);
        else cell.textContent = value === null || value === undefined ? '--' : String(value);
        return cell;
      }));
    }));
    var table = el('table', { class: 'data' }, [
      caption ? el('caption', { text: caption }) : null, thead, tbody
    ]);
    return table;
  }

  var toggleSeq = 0;

  /** A table-view toggle. The table is built lazily and kept after the first open. */
  function tableToggle(build, label) {
    var id = 'tableview-' + (++toggleSeq);
    var wrap = el('div', { class: 'table-wrap', id: id });
    wrap.hidden = true;
    var button = el('button', {
      class: 'btn', type: 'button', 'aria-expanded': 'false', 'aria-controls': id,
      text: label || 'Table'
    });
    var built = false;
    button.addEventListener('click', function () {
      var open = wrap.hidden;
      if (open && !built) { wrap.appendChild(build()); built = true; }
      wrap.hidden = !open;
      button.setAttribute('aria-expanded', open ? 'true' : 'false');
    });
    return { button: button, wrap: wrap, rebuild: function () { built = false; U.clear(wrap); wrap.hidden = true; button.setAttribute('aria-expanded', 'false'); } };
  }

  function emptyState(title, detail, extra) {
    return el('div', { class: 'panel' }, [
      el('div', { class: 'empty' }, [
        el('div', { class: 'empty__title', text: title }),
        el('p', { text: detail }),
        extra || null
      ])
    ]);
  }

  /* ------------------------------------------------------------ identity */

  function initials(handle) {
    var trimmed = String(handle || '?').trim();
    return trimmed ? trimmed.slice(0, 2).toUpperCase() : '?';
  }

  function identityPanel(snap, sourceLabel) {
    var totals = snap.totals;
    var hero = snap.headline.length ? snap.headline[0] : null;

    var avatar = el('div', { class: 'avatar' });
    if (snap.player.iconUrl) {
      var img = el('img', { src: snap.player.iconUrl, alt: '' });
      img.addEventListener('error', function () {
        U.clear(avatar);
        avatar.textContent = initials(snap.player.handle);
      });
      avatar.appendChild(img);
    } else {
      avatar.textContent = initials(snap.player.handle);
    }

    var meta = el('div', { class: 'meta' }, [
      el('span', { text: snap.gameLabel }),
      snap.player.platform ? el('span', { class: 'meta__dot', text: snap.player.platform }) : null,
      snap.player.id ? el('span', { class: 'meta__dot', text: 'id ' + snap.player.id }) : null
    ]);

    var totalsRow = el('div', { class: 'totals' }, [
      el('div', null, [el('b', { text: U.integer(totals.matches) }), 'matches']),
      el('div', null, [
        el('b', { text: U.integer(totals.wins) + ' - ' + U.integer(totals.losses) }), 'W - L'
      ]),
      el('div', null, [el('b', { text: U.percent(totals.winRate) }), 'win rate']),
      el('div', null, [el('b', { text: U.ratio(totals.kd) }), 'career K/D']),
      el('div', null, [el('b', { text: U.hours(totals.timePlayedSeconds) }), 'played'])
    ]);

    var left = el('div', { class: 'identity__who' }, [
      avatar,
      el('div', { style: 'min-width:0' }, [
        el('h1', { class: 'handle', text: snap.player.handle }),
        meta,
        el('div', { style: 'margin-top:10px;display:flex;flex-wrap:wrap;gap:6px' }, [
          sourceLabel
        ]),
        totalsRow
      ])
    ]);

    var heroBlock = el('div', { class: 'hero' }, hero ? [
      el('div', { class: 'hero__label', text: hero.label }),
      el('div', { class: 'hero__value', text: hero.formatted }),
      deltaChip(hero),
      hero.note ? el('div', { class: 'hero__note', text: hero.note }) : null,
      formStrip(snap.recent)
    ] : [formStrip(snap.recent)]);

    return el('section', { class: 'panel' }, [
      el('div', { class: 'identity' }, [left, heroBlock])
    ]);
  }

  /** Last twenty results, oldest on the left. Letter + colour, never colour alone. */
  function formStrip(recent) {
    if (!recent || !recent.length) return null;
    var slice = recent.slice(0, 20).slice().reverse();
    var cells = slice.map(function (m) {
      var won = m.won;
      var letter = won === true ? 'W' : won === false ? 'L' : FLAT;
      var label = (won === true ? 'Win' : won === false ? 'Loss' : 'No result') +
        ' on ' + m.map + ', ' + U.dayMonth(m.playedAt) + ', K/D ' + U.ratio(m.kd);
      return el('span', {
        class: 'form-cell ' + (won === true ? 'form-cell--w' : won === false ? 'form-cell--l' : ''),
        title: label,
        text: letter
      });
    });
    return el('div', { class: 'form-strip' }, [
      el('div', { class: 'form-strip__label', text: 'Last ' + cells.length + ' results, oldest first' }),
      el('div', { class: 'form-cells', role: 'img', 'aria-label': formSummary(slice) }, cells)
    ]);
  }

  function formSummary(matches) {
    var w = 0, l = 0;
    for (var i = 0; i < matches.length; i++) {
      if (matches[i].won === true) w++;
      else if (matches[i].won === false) l++;
    }
    return 'Recent form: ' + w + ' wins and ' + l + ' losses in the last ' + matches.length + ' matches.';
  }

  /* ------------------------------------------------------------ kpi tiles */

  function kpiRow(snap) {
    var tiles = snap.headline.slice(1); // the first is the hero figure
    if (!tiles.length) return null;
    var row = el('div', { class: 'kpi-row' });
    var sparks = [];

    tiles.forEach(function (k) {
      var series = snap.seriesByKey[k.key];
      var spark = series ? el('div', { class: 'kpi__spark' }) : null;
      row.appendChild(el('div', { class: 'kpi' }, [
        el('div', { class: 'kpi__label', text: k.label }),
        el('div', { class: 'kpi__value', text: k.formatted }),
        deltaChip(k),
        spark,
        k.note ? el('div', { class: 'kpi__note', text: k.note }) : null
      ]));
      if (spark) sparks.push({ holder: spark, series: series });
    });

    return {
      el: row,
      draw: function () {
        sparks.forEach(function (s) {
          EET.charts.sparkline(s.holder, s.series, { color: s.series.color, height: 30 });
        });
      }
    };
  }

  /* --------------------------------------------------------------- trends */

  var WINDOWS = [
    { label: 'All', days: 0 },
    { label: '90 d', days: 90 },
    { label: '60 d', days: 60 },
    { label: '30 d', days: 30 }
  ];

  function trendsSection(snap) {
    if (!snap.trends.length) {
      return {
        el: el('section', { class: 'section' }, [
          el('div', { class: 'section__head' }, el('h2', { class: 'section__title', text: 'Trends' })),
          emptyState('No trend series', 'This snapshot carried no TrendSeries, so there is nothing to plot over time yet.')
        ]),
        draw: function () { }
      };
    }

    var state = { key: snap.trends[0].key, windowDays: 0 };

    var windowGroup = el('div', { class: 'switch', role: 'group', 'aria-label': 'Time window for every trend chart' });
    var windowButtons = WINDOWS.map(function (w) {
      var b = el('button', {
        type: 'button', class: '', 'aria-pressed': String(w.days === state.windowDays), text: w.label
      });
      b.addEventListener('click', function () {
        state.windowDays = w.days;
        windowButtons.forEach(function (other, i) {
          other.setAttribute('aria-pressed', String(WINDOWS[i].days === state.windowDays));
        });
        draw();
      });
      windowGroup.appendChild(b);
      return b;
    });

    var head = el('div', { class: 'section__head' }, [
      el('h2', { class: 'section__title', text: 'Trends' }),
      el('span', {
        class: 'section__note',
        text: 'One metric per chart, one y-axis each. Direction is significance-tested on the server.'
      }),
      el('div', { class: 'section__spacer' }),
      windowGroup
    ]);

    var chartTitle = el('h3', { class: 'chart-card__title' });
    var chartSub = el('span', { class: 'chart-card__sub' });
    var badgeSlot = el('span');
    var holder = el('div', { class: 'chart-holder' });
    var keyRow = el('div', { class: 'chart-key' });
    var view = tableToggle(buildTables, 'Table view');

    var card = el('div', { class: 'panel chart-card' }, [
      el('div', { class: 'chart-card__head' }, [
        chartTitle, badgeSlot, chartSub,
        el('div', { class: 'chart-card__actions' }, view.button)
      ]),
      holder,
      keyRow,
      view.wrap
    ]);

    var picker = el('div', { class: 'picker', role: 'group', 'aria-label': 'Choose a metric to expand' });
    var pickerItems = snap.trends.map(function (series) {
      var sparkHolder = el('div', { class: 'picker__spark' });
      var latest = series.points.length ? series.smoothed[series.points.length - 1] : null;
      var item = el('button', {
        type: 'button',
        class: 'picker__item',
        'aria-pressed': String(series.key === state.key)
      }, [
        el('span', { class: 'picker__label' }, [
          el('span', { class: 'picker__swatch', style: 'background:' + series.color + ';', 'aria-hidden': 'true' }),
          el('span', { text: series.label })
        ]),
        el('span', {
          class: 'picker__value',
          text: U.seriesValue(latest, series.unit, series.scaleToPercent)
        }),
        directionBadge(series),
        sparkHolder
      ]);
      item.addEventListener('click', function () {
        state.key = series.key;
        pickerItems.forEach(function (p) {
          p.item.setAttribute('aria-pressed', String(p.series.key === state.key));
        });
        draw();
      });
      picker.appendChild(item);
      return { item: item, series: series, spark: sparkHolder };
    });

    var section = el('section', { class: 'section' }, [head, card, picker]);

    function current() {
      return snap.seriesByKey[state.key] || snap.trends[0];
    }

    /** The points the chart is actually showing, so the table matches it exactly. */
    function windowedPoints(series, windowDays) {
      if (!windowDays || !series.points.length) return series.points;
      var from = series.points[series.points.length - 1].date.getTime() - windowDays * 86400000;
      return series.points.filter(function (p) { return p.date.getTime() >= from; });
    }

    function buildTables() {
      var series = current();
      var frag = document.createDocumentFragment();
      var visible = windowedPoints(series, state.windowDays);
      var rows = visible.map(function (p) {
        return { p: p, s: series.smoothed[p.index] };
      }).slice().reverse();
      frag.appendChild(tableFrom([
        { label: 'Day', get: function (r) { return U.isoDay(r.p.date); } },
        {
          label: series.label, num: true, get: function (r) {
            return U.seriesValue(r.p.value, series.unit, series.scaleToPercent);
          }
        },
        {
          label: 'Smoothed', num: true, get: function (r) {
            return U.seriesValue(r.s, series.unit, series.scaleToPercent);
          }
        },
        { label: 'Matches', num: true, dim: true, get: function (r) { return U.integer(r.p.samples); } }
      ], rows, series.label + ' by day, newest first' +
        (state.windowDays ? ', last ' + state.windowDays + ' days' : '') +
        '. "Matches" is how many games produced each point.'));

      frag.appendChild(el('div', { style: 'height:18px' }));
      frag.appendChild(tableFrom([
        { label: 'Metric', get: function (s) { return s.label; } },
        { label: 'Direction', get: function (s) { return s.direction; } },
        {
          label: 'Per week', num: true, get: function (s) {
            return U.signed(s.scaleToPercent ? s.slopePerWeek * 100 : s.slopePerWeek, 3) +
              (s.scaleToPercent ? ' pp' : '');
          }
        },
        {
          label: 'Latest', num: true, get: function (s) {
            var last = s.smoothed.length ? s.smoothed[s.smoothed.length - 1] : null;
            return U.seriesValue(last, s.unit, s.scaleToPercent);
          }
        },
        { label: 'Days', num: true, dim: true, get: function (s) { return U.integer(s.points.length); } }
      ], snap.trends, 'Every trend series, with the direction the server tested for.'));
      return frag;
    }

    function draw() {
      var series = current();
      chartTitle.textContent = series.label + ' over time';
      U.clear(badgeSlot).appendChild(directionBadge(series));

      var perWeek = series.scaleToPercent
        ? U.signed(series.slopePerWeek * 100, 2) + ' pp / week'
        : U.signed(series.slopePerWeek, 3) + ' / week';
      var dayWord = series.points.length === 1 ? ' active day.' : ' active days.';
      var windowNote = state.windowDays
        ? ' Showing the last ' + state.windowDays + ' days; the direction is measured over all ' +
        series.points.length + (series.points.length === 1 ? ' active day.' : ' active days.')
        : '';
      chartSub.textContent = perWeek + ', over ' + series.points.length + dayWord + windowNote;

      EET.charts.trendChart(holder, series, { windowDays: state.windowDays });

      U.clear(keyRow);
      keyRow.appendChild(el('span', { class: 'chart-key__item' }, [
        el('span', { class: 'chart-key__line', style: 'background:' + series.color + ';', 'aria-hidden': 'true' }),
        el('span', { text: 'Smoothed (EWMA, weighted by matches per day)' })
      ]));
      keyRow.appendChild(el('span', { class: 'chart-key__item' }, [
        el('span', {
          class: 'chart-key__dot',
          style: 'background:' + series.color + ';', 'aria-hidden': 'true'
        }),
        el('span', { text: 'Daily average - bigger dot, more matches' })
      ]));
      keyRow.appendChild(el('span', { class: 'chart-key__item' }, [
        el('span', {
          class: 'chart-key__dot chart-key__dot--hollow',
          style: 'border-color:' + series.color + ';', 'aria-hidden': 'true'
        }),
        el('span', { text: 'Fewer than 3 matches that day' })
      ]));

      pickerItems.forEach(function (p) {
        EET.charts.sparkline(p.spark, p.series, {
          color: p.series.color, height: 34, take: 30, windowDays: state.windowDays
        });
      });
      view.rebuild();
    }

    return { el: section, draw: draw };
  }

  /* ----------------------------------------------------------- breakdowns */

  /**
   * A Breakdown carries a formatted string per row but nothing that says what the
   * axis ticks between them should look like, so infer it from the rows: if every
   * row is written as a percentage and the raw values are fractions, the axis is a
   * percentage axis too. Anything else is printed as a plain number.
   */
  function tickOptions(breakdown) {
    var rows = breakdown.rows || [];
    if (!rows.length) return {};
    var allPercent = true, maxAbs = 0;
    for (var i = 0; i < rows.length; i++) {
      if (!/%\s*$/.test(rows[i].formatted)) allPercent = false;
      maxAbs = Math.max(maxAbs, Math.abs(rows[i].value));
    }
    if (allPercent && maxAbs <= 1.5) {
      return {
        scaleToPercent: true,
        formatTick: function (v, d) { return U.percent(v, d); }
      };
    }
    if (allPercent) {
      return { formatTick: function (v, d) { return U.fixed(v, d) + '%'; } };
    }
    return {};
  }

  function breakdownsSection(snap) {
    if (!snap.breakdowns.length) return null;
    var cards = [];
    var grid = el('div', { class: 'grid-2' });

    snap.breakdowns.forEach(function (b) {
      var holder = el('div', { class: 'chart-holder' });
      var view = tableToggle(function () {
        return tableFrom([
          { label: 'Name', get: function (r) { return r.name; } },
          { label: b.valueLabel, num: true, get: function (r) { return r.formatted; } },
          { label: 'Matches', num: true, dim: true, get: function (r) { return U.integer(r.samples); } },
          {
            label: 'Share', num: true, dim: true, get: function (r) {
              return r.share === null ? '--' : U.percent(r.share, 1);
            }
          }
        ], b.rows, b.label + ', ranked. Rows with few matches are washed back in the chart.');
      }, 'Table');

      var sub = el('span', { class: 'chart-card__sub', text: b.valueLabel });
      grid.appendChild(el('div', { class: 'panel chart-card' }, [
        el('div', { class: 'chart-card__head' }, [
          el('h3', { class: 'chart-card__title', text: b.label }),
          sub,
          el('div', { class: 'chart-card__actions' }, view.button)
        ]),
        holder,
        view.wrap
      ]));
      cards.push({ holder: holder, breakdown: b, view: view, sub: sub });
    });

    return {
      el: el('section', { class: 'section' }, [
        el('div', { class: 'section__head' }, [
          el('h2', { class: 'section__title', text: 'Breakdowns' }),
          el('span', {
            class: 'section__note',
            text: 'Best and worst in one ranked list, so the bottom of the table is as visible as the top.'
          })
        ]),
        grid
      ]),
      draw: function () {
        cards.forEach(function (c) {
          var result = EET.charts.barChart(c.holder, c.breakdown, tickOptions(c.breakdown));
          var low = c.breakdown.rows.some(function (r) { return r.samples < 5; });
          c.sub.textContent = c.breakdown.valueLabel +
            (result && result.form === 'dots'
              ? ' · position scale, not zero-based'
              : ' · bar length from zero') +
            (low ? ' · faint means few matches' : '');
          c.view.rebuild();
        });
      }
    };
  }

  /* -------------------------------------------------------------- matches */

  function outcomeCell(m) {
    var won = m.won;
    var cls = won === true ? 'outcome outcome--w' : won === false ? 'outcome outcome--l' : 'outcome outcome--n';
    return el('span', { class: cls }, [
      el('span', { class: 'outcome__glyph', 'aria-hidden': 'true', text: won === null ? FLAT : WIN }),
      el('span', { text: won === true ? 'Win' : won === false ? 'Loss' : 'Unknown' })
    ]);
  }

  function matchesSection(snap) {
    if (!snap.recent.length) return null;
    var table = tableFrom([
      { label: 'When (UTC)', get: function (m) { return U.dayMonthTime(m.playedAt); } },
      { label: 'Result', get: outcomeCell },
      { label: 'Mode', get: function (m) { return m.mode; } },
      { label: 'Map', get: function (m) { return m.map; } },
      { label: 'Playlist', dim: true, get: function (m) { return m.playlist || '--'; } },
      { label: 'K', num: true, get: function (m) { return U.integer(m.kills); } },
      { label: 'D', num: true, get: function (m) { return U.integer(m.deaths); } },
      { label: 'A', num: true, get: function (m) { return U.integer(m.assists); } },
      { label: 'K/D', num: true, get: function (m) { return U.ratio(m.kd); } },
      {
        label: 'Acc', num: true, get: function (m) {
          return m.accuracy === null ? '--' : U.percent(m.accuracy, 1);
        }
      },
      {
        label: 'Score', num: true, dim: true, get: function (m) {
          return m.score === null ? '--' : U.integer(m.score);
        }
      },
      {
        label: 'Length', num: true, dim: true, get: function (m) {
          return U.shortDuration(m.durationSeconds);
        }
      }
    ], snap.recent, null);

    return el('section', { class: 'section' }, [
      el('div', { class: 'section__head' }, [
        el('h2', { class: 'section__title', text: 'Recent matches' }),
        el('span', {
          class: 'section__note',
          text: snap.recent.length + ' most recent, newest first. Times are UTC.'
        })
      ]),
      el('div', { class: 'panel' }, [
        el('div', { class: 'table-wrap table-wrap--tall' }, table)
      ])
    ]);
  }

  /* ------------------------------------------------------------- notices */

  function warningsSection(snap) {
    if (!snap.warnings.length) return null;
    return el('section', { class: 'section' }, [
      el('div', { class: 'section__head' },
        el('h2', { class: 'section__title', text: 'What this data does not tell you' })),
      el('div', { class: 'panel' }, snap.warnings.map(function (w) {
        return el('div', { class: 'notice' }, [
          el('span', { class: 'notice__glyph', 'aria-hidden': 'true', text: '⚠' }),
          el('span', { text: w })
        ]);
      }))
    ]);
  }

  EET.ui = {
    deltaChip: deltaChip,
    directionBadge: directionBadge,
    tableFrom: tableFrom,
    tableToggle: tableToggle,
    emptyState: emptyState,
    identityPanel: identityPanel,
    kpiRow: kpiRow,
    trendsSection: trendsSection,
    breakdownsSection: breakdownsSection,
    matchesSection: matchesSection,
    warningsSection: warningsSection
  };
}(typeof globalThis !== 'undefined' ? globalThis : this));
