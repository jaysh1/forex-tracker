<script setup lang="ts">
import type { CfdDashboardDto } from '../types'

defineProps<{ state: CfdDashboardDto | null; busy: boolean }>()
const emit = defineEmits<{
  decide: [id: string, action: 'approve' | 'reject']
}>()
</script>

<template>
  <section id="cfd-panel" class="cfd-panel" role="tabpanel" aria-labelledby="cfd-tab" tabindex="0">
    <div class="card">
      <h2>CFD practice trading</h2>
      <p v-if="!state" class="muted">Connecting to the practice account…</p>
      <p v-else-if="!state.enabled" class="muted">{{ state.enabledText }}</p>
      <template v-else>
        <p>{{ state.enabledText }}</p>
        <p>Practice equity: <strong>{{ state.equityText }}</strong> · Day P&amp;L: <strong>{{ state.dayPnlText }}</strong></p>
        <p class="muted">CFD practice has its own scanner and approval queue. Account equity and the daily loss guard are shared with Forex.</p>
        <p v-if="state.reason" class="warn">{{ state.reason }}</p>
      </template>
    </div>

    <section>
      <h2>CFD proposals</h2>
      <p v-if="!state" class="muted">Loading CFD status…</p>
      <p v-else-if="!state.hasPending" class="muted">{{ state.noPendingText }}</p>
      <article v-for="proposal in state?.pending" :key="proposal.id"
        class="card trade" :class="proposal.sideClass">
        <h3>{{ proposal.instrument }} <span class="side">{{ proposal.sideLabel }}</span></h3>
        <dl class="grid">
          <dt>Units</dt><dd>{{ proposal.unitsText }}</dd>
          <dt>Entry (approx.)</dt><dd>{{ proposal.entryText }}</dd>
          <dt>Stop loss</dt><dd>{{ proposal.stopText }}</dd>
          <dt>Take profit</dt><dd>{{ proposal.takeProfitText }}</dd>
          <dt>Estimated max loss</dt><dd>{{ proposal.maxLossText }}</dd>
          <dt>Expires</dt><dd>{{ proposal.expiresText }}</dd>
        </dl>
        <p class="why">{{ proposal.rationale }}</p>
        <div class="actions">
          <button class="btn reject" :disabled="busy" @click="emit('decide', proposal.id, 'reject')">Reject</button>
          <button class="btn approve" :class="{ armed: proposal.confirmationArmed }" :disabled="busy"
            @click="emit('decide', proposal.id, 'approve')">
            {{ proposal.confirmationArmed ? 'Tap again to confirm' : 'Approve practice order' }}
          </button>
        </div>
      </article>
    </section>
    <p class="muted cfd-note">CFD financing rates vary by instrument and can change daily. See
      <a href="https://www.oanda.com/au-en/trading/financing-costs/" target="_blank" rel="noopener noreferrer">OANDA financing costs</a>.
    </p>
  </section>
</template>
