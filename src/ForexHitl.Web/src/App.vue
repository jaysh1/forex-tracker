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
const activePage = ref<'forex' | 'cfd' | 'learn'>('forex')
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
  const pages = ['forex', 'cfd', 'learn'] as const
  const currentIndex = pages.indexOf(activePage.value)
  const direction = event.key === 'ArrowRight' ? 1 : -1
  activePage.value = pages[(currentIndex + direction + pages.length) % pages.length]
  document.getElementById(`${activePage.value}-tab`)?.focus()
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

    <nav class="market-tabs" role="tablist" aria-label="Dashboard pages">
      <button id="forex-tab" class="market-tab" :class="{ selected: activePage === 'forex' }"
        role="tab" :aria-selected="activePage === 'forex'" aria-controls="forex-panel"
        :tabindex="activePage === 'forex' ? 0 : -1" @keydown="onTabKeydown"
        @click="activePage = 'forex'">Forex</button>
      <button id="cfd-tab" class="market-tab" :class="{ selected: activePage === 'cfd' }"
        role="tab" :aria-selected="activePage === 'cfd'" aria-controls="cfd-panel"
        :tabindex="activePage === 'cfd' ? 0 : -1" @keydown="onTabKeydown"
        @click="activePage = 'cfd'">CFDs</button>
      <button id="learn-tab" class="market-tab" :class="{ selected: activePage === 'learn' }"
        role="tab" :aria-selected="activePage === 'learn'" aria-controls="learn-panel"
        :tabindex="activePage === 'learn' ? 0 : -1" @keydown="onTabKeydown"
        @click="activePage = 'learn'">Learn</button>
    </nav>

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-if="notice" class="notice" :class="noticeOk ? 'ok' : 'bad'" role="status">{{ notice }}</p>

    <section id="cfd-panel" class="cfd-panel" role="tabpanel" aria-labelledby="cfd-tab"
      tabindex="0" :hidden="activePage !== 'cfd'">
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

    <section id="learn-panel" class="learn-panel" role="tabpanel" aria-labelledby="learn-tab"
      tabindex="0" :hidden="activePage !== 'learn'">
      <header class="learn-intro card">
        <p class="eyebrow">A friendly guide to the dashboard</p>
        <h2>How the Forex signal works</h2>
        <p>Think of the bot as a careful helper. It watches the price, waits for a few clues to line up, then asks you before placing any order.</p>
      </header>

      <article class="card lesson">
        <h3>EMA50 and EMA200: two smooth trails</h3>
        <p>An EMA is a line that smooths out price wiggles. “50” and “200” mean it reacts over different numbers of 15-minute candles. The EMA50 follows recent changes faster; EMA200 moves more slowly and shows the broader direction.</p>
        <figure class="chart-figure">
          <svg viewBox="0 0 520 220" role="img" aria-labelledby="ema-title ema-desc">
            <title id="ema-title">Illustration of price and two moving averages</title>
            <desc id="ema-desc">A wiggly price line rises. The faster EMA50 line follows it more closely and stays above the slower EMA200 line.</desc>
            <path class="chart-grid" d="M42 30H500M42 75H500M42 120H500M42 165H500M42 195H500" />
            <path class="chart-axis" d="M42 20V195H500" />
            <path class="price-line" d="M50 155L85 140L115 160L148 115L178 132L210 98L242 121L276 82L308 100L340 65L372 88L405 49L438 70L475 38" />
            <path class="ema-fast" d="M50 158C110 151 132 135 178 128S245 112 278 95S341 79 374 68S438 53 475 42" />
            <path class="ema-slow" d="M50 173C115 168 145 160 190 150S257 138 300 123S367 110 405 93S450 81 475 70" />
            <text x="48" y="214">Earlier</text><text x="443" y="214">More recent</text>
          </svg>
          <figcaption><span class="legend price-key">Price</span><span class="legend fast-key">EMA50 (faster)</span><span class="legend slow-key">EMA200 (slower)</span></figcaption>
        </figure>
        <p class="plain-note"><strong>Like explaining it to a child:</strong> imagine two dogs following a ball. The quick dog (EMA50) turns quickly; the older, slower dog (EMA200) takes longer. When the quick dog is above the slow dog, this strategy calls the trend “up.” That is a clue, not a promise that prices will rise.</p>
      </article>

      <article class="card lesson">
        <h3>RSI: a speedometer for recent price moves</h3>
        <p>RSI is a number from 0 to 100. Below 30 means prices have been falling strongly; above 70 means they have been rising strongly. This strategy waits for RSI to turn back out of those zones before considering an entry.</p>
        <figure class="rsi-figure">
          <svg viewBox="0 0 520 150" role="img" aria-labelledby="rsi-title rsi-desc">
            <title id="rsi-title">RSI scale from zero to one hundred</title>
            <desc id="rsi-desc">The buy trigger is a crossing upward through 30. The sell trigger is a crossing downward through 70. The example RSI near 52 is in the middle.</desc>
            <rect class="rsi-buy-zone" x="42" y="40" width="137" height="45" rx="8" />
            <rect class="rsi-middle-zone" x="179" y="40" width="274" height="45" />
            <rect class="rsi-sell-zone" x="453" y="40" width="45" height="45" rx="8" />
            <path class="rsi-track" d="M42 63H498" />
            <path class="rsi-marker" d="M280 34V92" />
            <circle class="rsi-dot" cx="280" cy="63" r="8" />
            <text x="37" y="112">0</text><text x="164" y="112">30</text><text x="266" y="112">52</text><text x="438" y="112">70</text><text x="485" y="112">100</text>
            <text x="58" y="30">Oversold</text><text x="414" y="30">Overbought</text>
          </svg>
          <figcaption>Buy clue: cross up through 30 · Sell clue: cross down through 70</figcaption>
        </figure>
        <p class="plain-note"><strong>Your message, translated:</strong> “RSI 52.9 → 52.3” means the speedometer stayed in the middle and dipped a little. It did not rise up through 30, so there was no buy trigger—even though the EMA trend was up.</p>
      </article>

      <article class="card lesson">
        <h3>What must happen before you see a proposal?</h3>
        <ol class="steps">
          <li><strong>Wait for a fresh, finished 5-minute candle.</strong> The scanner does not decide from a candle that is still forming.</li>
          <li><strong>Check the 15-minute direction.</strong> EMA50 above EMA200 allows a buy setup; EMA50 below EMA200 allows a sell setup.</li>
          <li><strong>Wait for the RSI crossing.</strong> Buy crosses up through 30; sell crosses down through 70.</li>
          <li><strong>Run the safety checks.</strong> Trading hours, current spread, news, open positions, account risk and order size must all pass.</li>
          <li><strong>Show you a proposal.</strong> You review it and approve it; the bot does not place the order on its own.</li>
        </ol>
        <p class="warn">No signal means no proposal. These rules can miss moves and do not predict what the market will do next.</p>
      </article>
    </section>

    <section v-if="!state" id="forex-panel" role="tabpanel" aria-labelledby="forex-tab"
      tabindex="0" :hidden="activePage !== 'forex'">
      <p class="muted center">Connecting…</p>
    </section>
    <section v-else id="forex-panel" role="tabpanel" aria-labelledby="forex-tab"
      tabindex="0" :hidden="activePage !== 'forex'">
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
