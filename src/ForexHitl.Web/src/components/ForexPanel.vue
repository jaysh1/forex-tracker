<script setup lang="ts">
import type { ForexDashboardDto } from '../types'
import IndicatorCharts from './IndicatorCharts.vue'

defineProps<{ state: ForexDashboardDto; busy: boolean }>()
const emit = defineEmits<{
  decide: [id: string, action: 'approve' | 'reject']
}>()
</script>

<template>
  <section id="forex-panel" role="tabpanel" aria-labelledby="forex-tab" tabindex="0">
    <section>
      <h2>Waiting for approval</h2>
      <p v-if="!state.hasPending" class="muted">{{ state.noPendingText }}</p>
      <article v-for="proposal in state.pending" :key="proposal.id" class="card trade" :class="proposal.sideClass">
        <h3>{{ proposal.instrument }} <span class="side">{{ proposal.sideLabel }}</span></h3>
        <dl class="grid">
          <dt>Size</dt><dd>{{ proposal.sizeText }}</dd>
          <dt>Entry (approx.)</dt><dd>{{ proposal.entryText }}</dd>
          <dt>Stop loss</dt><dd>{{ proposal.stopText }}</dd>
          <dt>Take profit</dt><dd>{{ proposal.takeProfitText }}</dd>
          <dt>Max loss</dt><dd>{{ proposal.riskText }}</dd>
          <dt>Expires</dt><dd>{{ proposal.expiresText }}</dd>
        </dl>
        <p class="why">{{ proposal.rationale }}</p>
        <div class="actions">
          <button class="btn reject" :disabled="busy" @click="emit('decide', proposal.id, 'reject')">Reject</button>
          <button class="btn approve" :class="{ armed: proposal.confirmationArmed }" :disabled="busy"
            @click="emit('decide', proposal.id, 'approve')">
            {{ proposal.confirmationArmed ? 'Tap again to confirm' : 'Approve' }}
          </button>
        </div>
      </article>
    </section>

    <IndicatorCharts :has-instruments="state.hasInstruments" :instruments="state.instruments" />

    <section class="card">
      <h2>Account</h2>
      <p v-if="!state.ready" class="muted">{{ state.notReadyText }}</p>
      <template v-else>
        <dl class="grid">
          <dt>Equity</dt><dd>{{ state.equityText }}</dd>
          <dt>Today</dt><dd :class="state.todayClass">{{ state.todayText }}</dd>
          <dt>Trading</dt><dd>{{ state.tradingText }}</dd>
          <dt>Hours</dt><dd>{{ state.hoursText }}</dd>
        </dl>
        <p v-if="state.tradingWarning" class="warn">{{ state.tradingWarning }}</p>
        <p v-if="state.scannerWarning" class="warn">{{ state.scannerWarning }}</p>
      </template>
    </section>

    <section>
      <h2>Market checks</h2>
      <p class="muted scan-help">Bid and ask refresh about every 20 seconds. The entry setup is evaluated on each new completed 5-minute candle.</p>
      <p v-if="!state.hasInstruments" class="muted">{{ state.noMarketChecksText }}</p>
      <article v-for="instrument in state.instruments" :key="instrument.instrument" class="card scan-card">
        <h3>{{ instrument.instrument }}</h3>
        <dl class="grid">
          <dt>Bid</dt><dd>{{ instrument.bidText }}</dd>
          <dt>Ask</dt><dd>{{ instrument.askText }}</dd>
        </dl>
        <p class="why">{{ instrument.explanation }}</p>
        <p class="muted scan-times">Price: {{ instrument.priceTimeText }} · Reason updated: {{ instrument.reasonUpdatedText }}</p>
      </article>
    </section>

    <section>
      <h2>Activity</h2>
      <p v-if="!state.hasActivity" class="muted">{{ state.noActivityText }}</p>
      <ul class="feed">
        <li v-for="(entry, index) in state.activity" :key="`${entry.timeText}-${index}`" :class="entry.kindClass">
          <time>{{ entry.timeText }}</time><span>{{ entry.text }}</span>
        </li>
      </ul>
    </section>
  </section>
</template>
