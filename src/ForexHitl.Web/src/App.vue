<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import {
  SessionExpiredError,
  decideCfd,
  decideForex,
  getCfdState,
  getForexState,
  hasSession,
  login,
  logout,
} from './api'
import type { CfdDashboardDto, ForexDashboardDto } from './types'

const authenticated = ref(hasSession())
const password = ref('')
const loginError = ref<string | null>(null)
const state = ref<ForexDashboardDto | null>(null)
const cfdState = ref<CfdDashboardDto | null>(null)
const error = ref<string | null>(null)
const notice = ref<string | null>(null)
const noticeOk = ref(false)
const busy = ref(false)
const activeMarket = ref<'forex' | 'cfd'>('forex')
let pollHandle: number | undefined
let refreshing = false

function expireSession() {
  authenticated.value = false
  state.value = null
  cfdState.value = null
  stopPolling()
}

async function refresh() {
  if (refreshing || !authenticated.value) return
  refreshing = true
  try {
    const [forex, cfd] = await Promise.all([getForexState(), getCfdState()])
    state.value = forex
    cfdState.value = cfd
    error.value = null
  } catch (cause) {
    if (cause instanceof SessionExpiredError) {
      expireSession()
    } else {
      error.value = 'Connection problem — retrying…'
    }
  } finally {
    refreshing = false
  }
}

function startPolling() {
  stopPolling()
  void refresh()
  pollHandle = window.setInterval(() => void refresh(), 5000)
}

function stopPolling() {
  if (pollHandle !== undefined) window.clearInterval(pollHandle)
  pollHandle = undefined
}

async function signIn() {
  if (busy.value) return
  busy.value = true
  loginError.value = null
  try {
    loginError.value = await login(password.value)
    if (!loginError.value) {
      authenticated.value = true
      startPolling()
    }
  } catch {
    loginError.value = 'Cannot reach the server.'
  } finally {
    busy.value = false
    password.value = ''
  }
}

async function signOut() {
  stopPolling()
  await logout()
  authenticated.value = false
  state.value = null
  cfdState.value = null
  notice.value = null
}

async function decide(market: 'forex' | 'cfd', id: string, action: 'approve' | 'reject') {
  if (busy.value) return
  busy.value = true
  notice.value = null
  try {
    const result = market === 'forex'
      ? await decideForex(id, action)
      : await decideCfd(id, action)
    notice.value = result.message
    noticeOk.value = result.succeeded
  } catch (cause) {
    if (cause instanceof SessionExpiredError) {
      expireSession()
      return
    }
    notice.value = market === 'cfd'
      ? 'CFD practice request failed. Check your OANDA practice account before retrying.'
      : 'Request failed. Check your OANDA account before retrying.'
    noticeOk.value = false
  } finally {
    busy.value = false
  }
  await refresh()
}

function onTabKeydown(event: KeyboardEvent) {
  if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
  activeMarket.value = activeMarket.value === 'forex' ? 'cfd' : 'forex'
  document.getElementById(activeMarket.value === 'forex' ? 'forex-tab' : 'cfd-tab')?.focus()
}

onMounted(() => {
  if (authenticated.value) startPolling()
})
onBeforeUnmount(stopPolling)
</script>

<template>
  <main v-if="!authenticated" class="card login">
    <h1>Forex approver</h1>
    <form @submit.prevent="signIn">
      <label for="pw">Password</label>
      <input id="pw" v-model="password" type="password" autocomplete="current-password" />
      <button type="submit" class="btn approve" :disabled="busy">
        {{ busy ? 'Signing in…' : 'Sign in' }}
      </button>
    </form>
    <p v-if="loginError" class="error" role="alert">{{ loginError }}</p>
  </main>

  <template v-else>
    <header class="top">
      <h1>Trading dashboard</h1>
      <span v-if="state" class="badge" :class="state.modeClass">{{ state.modeLabel }}</span>
      <button class="btn small" @click="signOut">Sign out</button>
    </header>

    <nav class="market-tabs" role="tablist" aria-label="Market type">
      <button id="forex-tab" class="market-tab" :class="{ selected: activeMarket === 'forex' }"
        role="tab" :aria-selected="activeMarket === 'forex'" aria-controls="forex-panel"
        :tabindex="activeMarket === 'forex' ? 0 : -1" @keydown="onTabKeydown"
        @click="activeMarket = 'forex'">Forex</button>
      <button id="cfd-tab" class="market-tab" :class="{ selected: activeMarket === 'cfd' }"
        role="tab" :aria-selected="activeMarket === 'cfd'" aria-controls="cfd-panel"
        :tabindex="activeMarket === 'cfd' ? 0 : -1" @keydown="onTabKeydown"
        @click="activeMarket = 'cfd'">CFDs</button>
    </nav>

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-if="notice" class="notice" :class="noticeOk ? 'ok' : 'bad'" role="status">{{ notice }}</p>

    <section id="cfd-panel" class="cfd-panel" role="tabpanel" aria-labelledby="cfd-tab"
      tabindex="0" :hidden="activeMarket !== 'cfd'">
      <div class="card">
        <h2>CFD practice trading</h2>
        <p v-if="!cfdState" class="muted">Connecting to the practice account…</p>
        <p v-else-if="!cfdState.enabled" class="muted">{{ cfdState.enabledText }}</p>
        <template v-else>
          <p>{{ cfdState.enabledText }}</p>
          <p>Practice equity: <strong>{{ cfdState.equityText }}</strong> · Day P&amp;L: <strong>{{ cfdState.dayPnlText }}</strong></p>
          <p class="muted">CFD practice has its own scanner and approval queue. Account equity and the daily loss guard are shared with Forex.</p>
          <p v-if="cfdState.reason" class="warn">{{ cfdState.reason }}</p>
        </template>
      </div>

      <section>
        <h2>CFD proposals</h2>
        <p v-if="!cfdState" class="muted">Loading CFD status…</p>
        <p v-else-if="!cfdState.hasPending" class="muted">{{ cfdState.noPendingText }}</p>
        <article v-for="proposal in cfdState?.pending" :key="proposal.id"
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
            <button class="btn reject" :disabled="busy" @click="decide('cfd', proposal.id, 'reject')">Reject</button>
            <button class="btn approve" :class="{ armed: proposal.confirmationArmed }" :disabled="busy"
              @click="decide('cfd', proposal.id, 'approve')">
              {{ proposal.confirmationArmed ? 'Tap again to confirm' : 'Approve practice order' }}
            </button>
          </div>
        </article>
      </section>
      <p class="muted cfd-note">CFD financing rates vary by instrument and can change daily. See
        <a href="https://www.oanda.com/au-en/trading/financing-costs/" target="_blank" rel="noopener noreferrer">OANDA financing costs</a>.
      </p>
    </section>

    <section v-if="!state" id="forex-panel" role="tabpanel" aria-labelledby="forex-tab"
      tabindex="0" :hidden="activeMarket !== 'forex'">
      <p class="muted center">Connecting…</p>
    </section>
    <section v-else id="forex-panel" role="tabpanel" aria-labelledby="forex-tab"
      tabindex="0" :hidden="activeMarket !== 'forex'">
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
            <button class="btn reject" :disabled="busy" @click="decide('forex', proposal.id, 'reject')">Reject</button>
            <button class="btn approve" :class="{ armed: proposal.confirmationArmed }" :disabled="busy"
              @click="decide('forex', proposal.id, 'approve')">
              {{ proposal.confirmationArmed ? 'Tap again to confirm' : 'Approve' }}
            </button>
          </div>
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
</template>
