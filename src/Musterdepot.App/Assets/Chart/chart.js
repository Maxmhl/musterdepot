/*
 * Musterdepot – Chart-Brücke für TradingView Lightweight Charts (v5, Apache-2.0).
 * Alle Werte (Kerzen, Indikatoren, Preislinien, Lücken) kommen fertig aus C# per PostWebMessageAsJson.
 * Hier wird nur gezeichnet – keine Berechnung, keine Netzwerkzugriffe.
 */
(function () {
    'use strict';

    const LWC = window.LightweightCharts;
    const host = window.chrome && window.chrome.webview;
    const post = (msg) => { if (host) host.postMessage(msg); };

    window.addEventListener('error', (e) => post({ type: 'error', message: String(e.message || e) }));

    const themes = {
        light: {
            background: '#ffffff', text: '#1b1f24', grid: '#eef0f3', border: '#dde1e6',
            up: '#0b7a55', down: '#c62828', volUp: 'rgba(11,122,85,0.35)', volDown: 'rgba(198,40,40,0.35)',
            gap: 'rgba(255,152,0,0.16)', level: '#9aa1ab'
        },
        dark: {
            background: '#1f2227', text: '#e6e8eb', grid: '#2a2e35', border: '#343941',
            up: '#3dd68c', down: '#ff6b6b', volUp: 'rgba(61,214,140,0.30)', volDown: 'rgba(255,107,107,0.30)',
            gap: 'rgba(255,199,100,0.14)', level: '#6b7280'
        }
    };

    let theme = themes.light;
    let bars = [];
    let gaps = [];
    let priceLines = [];
    let indicatorSeries = new Map();   // id -> { series, def, levels: [] }
    let currentSymbol = null;
    let rawSymbol = null;
    let notice = null;

    const pad = (n) => String(n).padStart(2, '0');
    const localDate = (t) => new Date(t * 1000);

    // Zeitachse in Ortszeit (Lightweight Charts rechnet intern in UTC).
    function tickMarkFormatter(time, tickMarkType) {
        const d = localDate(time);
        switch (tickMarkType) {
            case 0: return String(d.getFullYear());
            case 1: return d.toLocaleString('de-DE', { month: 'short' });
            case 2: return pad(d.getDate()) + '.' + pad(d.getMonth() + 1) + '.';
            case 4: return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
            default: return pad(d.getHours()) + ':' + pad(d.getMinutes());
        }
    }

    function timeFormatter(time) {
        const d = localDate(time);
        return pad(d.getDate()) + '.' + pad(d.getMonth() + 1) + '.' + d.getFullYear() + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
    }

    const chart = LWC.createChart(document.getElementById('chart'), {
        autoSize: true,
        layout: {
            background: { type: 'solid', color: theme.background },
            textColor: theme.text,
            fontFamily: '"Segoe UI Variable", "Segoe UI", sans-serif',
            // Pflicht-Hinweis auf TradingView (Lizenz: Link auf tradingview.com) – das Logo erfüllt ihn.
            attributionLogo: true
        },
        grid: { vertLines: { color: theme.grid }, horzLines: { color: theme.grid } },
        rightPriceScale: { borderColor: theme.border },
        timeScale: { borderColor: theme.border, timeVisible: true, secondsVisible: false, tickMarkFormatter: tickMarkFormatter },
        localization: { locale: 'de-DE', timeFormatter: timeFormatter },
        crosshair: { mode: LWC.CrosshairMode.Normal }
    });

    const candles = chart.addSeries(LWC.CandlestickSeries, {
        upColor: theme.up, downColor: theme.down, borderVisible: false, wickUpColor: theme.up, wickDownColor: theme.down
    });

    const volume = chart.addSeries(LWC.HistogramSeries, {
        priceFormat: { type: 'volume' }, priceScaleId: 'vol', lastValueVisible: false, priceLineVisible: false
    });
    chart.priceScale('vol').applyOptions({ scaleMargins: { top: 0.82, bottom: 0 } });

    // Lücken (Standby/Verbindungsabbruch) als farbige Fläche über die ganze Höhe.
    const gapShade = chart.addSeries(LWC.HistogramSeries, {
        priceScaleId: 'gap', lastValueVisible: false, priceLineVisible: false, color: theme.gap,
        priceFormat: { type: 'custom', formatter: () => '' }
    });
    chart.priceScale('gap').applyOptions({ scaleMargins: { top: 0, bottom: 0 }, visible: false });

    const markers = LWC.createSeriesMarkers(candles, []);

    function setStatus(text) {
        const el = document.getElementById('status');
        el.textContent = text || '';
        el.style.display = text ? 'block' : 'none';
    }

    function renderLegend() {
        const legend = document.getElementById('legend');
        legend.replaceChildren();
        if (!currentSymbol) return;
        const title = document.createElement('div');
        title.className = 'title';
        title.textContent = currentSymbol;
        if (notice) {
            const n = document.createElement('span');
            n.className = 'notice';
            n.textContent = notice;
            title.appendChild(n);
        }
        legend.appendChild(title);
        const line = document.createElement('div');
        for (const { def } of indicatorSeries.values()) {
            const item = document.createElement('span');
            item.className = 'item';
            item.style.color = def.color;
            item.textContent = '● ' + def.title;
            line.appendChild(item);
        }
        legend.appendChild(line);
    }

    const toCandle = (b) => ({ time: b.time, open: b.open, high: b.high, low: b.low, close: b.close });
    const toVolume = (b) => ({ time: b.time, value: b.volume, color: b.close >= b.open ? theme.volUp : theme.volDown });

    function inGap(time, bar) {
        if (bar && bar.backfill) return true;
        for (const g of gaps) if (time >= g.start && time <= g.end) return true;
        return false;
    }

    function renderGaps() {
        gapShade.setData(bars.map((b) => inGap(b.time, b) ? { time: b.time, value: 1 } : { time: b.time }));
        const m = [];
        for (const g of gaps) {
            const bar = bars.find((b) => b.time >= g.start);
            if (bar) m.push({ time: bar.time, position: 'aboveBar', color: '#FF9800', shape: 'arrowDown', text: g.label });
        }
        markers.setMarkers(m);
    }

    function setIndicators(defs) {
        const wanted = new Set(defs.map((d) => d.id));
        for (const [id, entry] of indicatorSeries) {
            if (!wanted.has(id)) {
                chart.removeSeries(entry.series);
                indicatorSeries.delete(id);
            }
        }
        for (const def of defs) {
            let entry = indicatorSeries.get(def.id);
            if (!entry || entry.def.pane !== def.pane) {
                if (entry) chart.removeSeries(entry.series);
                const series = chart.addSeries(LWC.LineSeries, {
                    color: def.color, lineWidth: def.lineWidth || 2, priceLineVisible: false, lastValueVisible: true,
                    crosshairMarkerVisible: false, title: ''
                }, def.pane || 0);
                entry = { series: series, def: def, levels: [] };
                indicatorSeries.set(def.id, entry);
            } else {
                entry.series.applyOptions({ color: def.color, lineWidth: def.lineWidth || 2 });
                for (const l of entry.levels) entry.series.removePriceLine(l);
                entry.levels = [];
            }
            entry.def = def;
            entry.series.setData(def.data.map((p) => ({ time: p.time, value: p.value })));
            for (const level of def.levels || []) {
                entry.levels.push(entry.series.createPriceLine({
                    price: level, color: theme.level, lineWidth: 1, lineStyle: LWC.LineStyle.Dashed, axisLabelVisible: true, title: ''
                }));
            }
        }
        // Unterfenster (RSI) kleiner halten.
        const panes = chart.panes();
        if (panes.length > 1) panes[1].setHeight(Math.max(90, Math.round(document.body.clientHeight * 0.22)));
        renderLegend();
    }

    function setPriceLines(lines) {
        for (const pl of priceLines) candles.removePriceLine(pl);
        priceLines = (lines || []).map((l) => candles.createPriceLine({
            price: l.price, color: l.color, lineWidth: l.lineWidth || 1, lineStyle: l.lineStyle,
            axisLabelVisible: true, title: l.title
        }));
    }

    function applyTheme(dark) {
        theme = dark ? themes.dark : themes.light;
        document.documentElement.classList.toggle('dark', !!dark);
        chart.applyOptions({
            layout: { background: { type: 'solid', color: theme.background }, textColor: theme.text },
            grid: { vertLines: { color: theme.grid }, horzLines: { color: theme.grid } },
            rightPriceScale: { borderColor: theme.border },
            timeScale: { borderColor: theme.border }
        });
        candles.applyOptions({ upColor: theme.up, downColor: theme.down, wickUpColor: theme.up, wickDownColor: theme.down });
        gapShade.applyOptions({ color: theme.gap });
        if (bars.length) volume.setData(bars.map(toVolume));
    }

    function load(msg) {
        const symbolChanged = msg.symbol !== rawSymbol;
        rawSymbol = msg.symbol;
        currentSymbol = msg.symbol + ' · ' + msg.timeframe;
        notice = msg.notice || null;
        bars = msg.bars || [];
        gaps = msg.gaps || [];
        const precision = msg.priceDecimals != null ? msg.priceDecimals : 2;
        candles.applyOptions({ priceFormat: { type: 'price', precision: precision, minMove: Math.pow(10, -precision) } });
        candles.setData(bars.map(toCandle));
        volume.setData(bars.map(toVolume));
        renderGaps();
        setIndicators(msg.indicators || []);
        setPriceLines(msg.priceLines);
        setStatus(bars.length ? '' : 'Noch keine Kerzen für ' + msg.symbol);
        if (symbolChanged) chart.timeScale().fitContent();
        renderLegend();
    }

    function updateBar(msg) {
        const b = msg.bar;
        if (!b) return;
        const last = bars.length ? bars[bars.length - 1] : null;
        if (last && b.time < last.time) return;  // ältere Kerze: ignorieren
        if (last && b.time === last.time) bars[bars.length - 1] = b; else bars.push(b);
        try {
            candles.update(toCandle(b));
            volume.update(toVolume(b));
            gapShade.update(inGap(b.time, b) ? { time: b.time, value: 1 } : { time: b.time });
            for (const p of msg.points || []) {
                const entry = indicatorSeries.get(p.id);
                if (entry) entry.series.update({ time: p.time, value: p.value });
            }
        } catch (err) {
            post({ type: 'error', message: 'Update: ' + err.message });
        }
        setStatus('');
    }

    function handle(msg) {
        if (!msg || typeof msg !== 'object') return;
        switch (msg.type) {
            case 'theme': applyTheme(msg.dark); break;
            case 'load': load(msg); break;
            case 'bar': updateBar(msg); break;
            case 'indicators': setIndicators(msg.indicators || []); break;
            case 'priceLines': setPriceLines(msg.lines); break;
            case 'gaps': gaps = msg.gaps || []; renderGaps(); break;
            case 'status': setStatus(msg.text); break;
        }
    }

    if (host) {
        host.addEventListener('message', (e) => {
            try { handle(e.data); } catch (err) { post({ type: 'error', message: String(err && err.message || err) }); }
        });
    }

    setStatus('Warte auf Daten …');
    post({ type: 'ready' });
})();
