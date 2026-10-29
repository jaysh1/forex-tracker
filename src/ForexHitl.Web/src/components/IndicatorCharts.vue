<script setup lang="ts">
import type { IndicatorChartView, MarketCheckView } from '../types'

defineProps<{ hasInstruments: boolean; instruments: MarketCheckView[] }>()

function valuePath(values: Array<number | null>, min: number, max: number): string {
  const valid = values.map((value, index) => value === null ? null : ({ value, index }))
    .filter((point): point is { value: number; index: number } => point !== null)
  if (valid.length < 2) return ''
  const range = max - min || 1
  return valid.map(({ value, index }) => {
    const x = 50 + index / Math.max(1, values.length - 1) * 425
    const y = 195 - (value - min) / range * 157
    return `${index === valid[0].index ? 'M' : 'L'}${x.toFixed(1)} ${y.toFixed(1)}`
  }).join(' ')
}

function emaRange(chart: IndicatorChartView) {
  const values = [...chart.ema50, ...chart.ema200].filter((value): value is number => value !== null)
  if (!values.length) return { min: 0, max: 1 }
  const low = Math.min(...values)
  const high = Math.max(...values)
  const padding = (high - low) * 0.08 || Math.abs(high) * 0.001 || 0.00001
  return { min: low - padding, max: high + padding }
}

function rsiX(value: number): number {
  return 42 + value * 4.56
}
</script>

<template>
  <section class="live-indicators">
    <h2>Live EMA &amp; RSI charts</h2>
    <p class="muted scan-help">Charts load from recent OANDA candle history on the first scanner cycle, then refresh after each completed 5-minute candle. EMA50/EMA200 use 15-minute candles; RSI(14) uses 5-minute candles.</p>
    <p v-if="!hasInstruments" class="muted">Waiting for the scanner’s first market update.</p>
    <p v-else-if="instruments.every(instrument => !instrument.indicators)" class="warn">
      No historical indicator data has reached the dashboard yet. Latest scanner status: {{ instruments[0]?.explanation }}
    </p>
    <article v-for="instrument in instruments" :key="`indicators-${instrument.instrument}`" class="card indicator-pair">
      <h3>{{ instrument.instrument }}</h3>
      <div v-if="instrument.indicators" class="indicator-charts">
        <div class="indicator-chart">
          <h4>EMA trend <span>15-minute candles</span></h4>
          <p class="indicator-values"><span>EMA50 <strong>{{ instrument.indicators.ema50Text }}</strong></span><span>EMA200 <strong>{{ instrument.indicators.ema200Text }}</strong></span></p>
          <figure class="chart-figure live-figure">
            <svg viewBox="0 0 520 220" role="img" :aria-label="`${instrument.instrument} EMA50 and EMA200 trend chart`">
              <path class="chart-grid" d="M42 30H500M42 75H500M42 120H500M42 165H500M42 195H500" />
              <path class="chart-axis" d="M42 20V195H500" />
              <path class="ema-fast" :d="valuePath(instrument.indicators.ema50, emaRange(instrument.indicators).min, emaRange(instrument.indicators).max)" />
              <path class="ema-slow" :d="valuePath(instrument.indicators.ema200, emaRange(instrument.indicators).min, emaRange(instrument.indicators).max)" />
              <text x="48" y="214">Earlier</text><text x="443" y="214">More recent</text>
            </svg>
            <figcaption><span class="legend fast-key">EMA50 (faster)</span><span class="legend slow-key">EMA200 (slower)</span></figcaption>
          </figure>
        </div>
        <div class="indicator-chart">
          <h4>RSI <span>14 · 5-minute candles</span></h4>
          <p class="indicator-values"><span>Latest RSI <strong>{{ instrument.indicators.rsiText }}</strong></span><span>Updated <strong>{{ instrument.indicators.updatedText }}</strong></span></p>
          <figure class="rsi-figure live-figure">
            <svg viewBox="0 0 520 150" role="img" :aria-label="`${instrument.instrument} latest RSI ${instrument.indicators.rsiText} on a zero-to-one-hundred scale`">
              <rect class="rsi-buy-zone" x="42" y="40" width="137" height="45" rx="8" />
              <rect class="rsi-middle-zone" x="179" y="40" width="274" height="45" />
              <rect class="rsi-sell-zone" x="453" y="40" width="45" height="45" rx="8" />
              <path class="rsi-track" d="M42 63H498" />
              <path class="rsi-marker" :d="`M${rsiX(instrument.indicators.rsi.at(-1) ?? 50)} 34V92`" />
              <circle class="rsi-dot" :cx="rsiX(instrument.indicators.rsi.at(-1) ?? 50)" cy="63" r="8" />
              <text x="37" y="112">0</text><text x="164" y="112">30</text>
              <text :x="Math.max(42, Math.min(460, rsiX(instrument.indicators.rsi.at(-1) ?? 50) - 14))" y="30">{{ instrument.indicators.rsiText }}</text>
              <text x="438" y="112">70</text><text x="485" y="112">100</text>
              <text x="58" y="30">Oversold</text><text x="414" y="30">Overbought</text>
            </svg>
            <figcaption>Latest RSI from historical 5-minute candles · Buy clue below 30 · Sell clue above 70</figcaption>
          </figure>
        </div>
      </div>
      <p v-else class="muted indicator-wait">Waiting for the scanner to publish completed candle indicators. If price checks are updating but this remains after restarting the local API, its server process may still be running older code.</p>
    </article>
  </section>
</template>
