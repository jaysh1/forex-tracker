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
import CfdPanel from './components/CfdPanel.vue'
import DashboardHeader from './components/DashboardHeader.vue'
import DashboardTabs from './components/DashboardTabs.vue'
import ForexPanel from './components/ForexPanel.vue'
import LearnPanel from './components/LearnPanel.vue'
import LoginPanel from './components/LoginPanel.vue'
import type { CfdDashboardDto, ForexDashboardDto } from './types'

type Page = 'forex' | 'cfd' | 'learn'

const authenticated = ref(hasSession())
const loginError = ref<string | null>(null)
const state = ref<ForexDashboardDto | null>(null)
const cfdState = ref<CfdDashboardDto | null>(null)
const error = ref<string | null>(null)
const notice = ref<string | null>(null)
const noticeOk = ref(false)
const busy = ref(false)
const activePage = ref<Page>('forex')
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

async function signIn(password: string) {
  if (busy.value) return
  busy.value = true
  loginError.value = null
  try {
    loginError.value = await login(password)
    if (!loginError.value) {
      authenticated.value = true
      startPolling()
    }
  } catch {
    loginError.value = 'Cannot reach the server.'
  } finally {
    busy.value = false
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

onMounted(() => {
  if (authenticated.value) startPolling()
})
onBeforeUnmount(stopPolling)
</script>

<template>
  <LoginPanel v-if="!authenticated" :busy="busy" :error="loginError" @submit="signIn" />

  <template v-else>
    <DashboardHeader :mode-label="state?.modeLabel" :mode-class="state?.modeClass" @sign-out="signOut" />
    <DashboardTabs v-model="activePage" />

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-if="notice" class="notice" :class="noticeOk ? 'ok' : 'bad'" role="status">{{ notice }}</p>

    <CfdPanel :hidden="activePage !== 'cfd'" :state="cfdState" :busy="busy"
      @decide="(id, action) => decide('cfd', id, action)" />
    <section v-if="!state" id="forex-panel" role="tabpanel" aria-labelledby="forex-tab"
      tabindex="0" :hidden="activePage !== 'forex'">
      <p class="muted center">Connecting…</p>
    </section>
    <ForexPanel v-else :hidden="activePage !== 'forex'" :state="state" :busy="busy"
      @decide="(id, action) => decide('forex', id, action)" />
    <LearnPanel :hidden="activePage !== 'learn'" />
  </template>
</template>
