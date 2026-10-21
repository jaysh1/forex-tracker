export interface LoginResponse {
  token: string
}

export interface DashboardActionResult {
  succeeded: boolean
  executed: boolean
  needsConfirmation: boolean
  message: string
}

export interface ForexDashboardDto {
  modeLabel: string
  modeClass: string
  ready: boolean
  notReadyText: string
  equityText: string
  todayText: string
  todayClass: string
  tradingText: string
  hoursText: string
  tradingWarning: string | null
  scannerWarning: string | null
  hasInstruments: boolean
  instruments: MarketCheckView[]
  hasPending: boolean
  pending: ForexProposalView[]
  hasActivity: boolean
  activity: ActivityView[]
  noMarketChecksText: string
  noPendingText: string
  noActivityText: string
}

export interface MarketCheckView {
  instrument: string
  bidText: string
  askText: string
  explanation: string
  priceTimeText: string
  reasonUpdatedText: string
}

export interface ForexProposalView {
  id: string
  instrument: string
  sideLabel: string
  sideClass: string
  confirmationArmed: boolean
  sizeText: string
  entryText: string
  stopText: string
  takeProfitText: string
  riskText: string
  expiresText: string
  rationale: string
}

export interface ActivityView {
  timeText: string
  kindClass: string
  text: string
}

export interface CfdDashboardDto {
  enabled: boolean
  enabledText: string
  equityText: string
  dayPnlText: string
  reason: string | null
  hasPending: boolean
  pending: CfdProposalView[]
  noPendingText: string
}

export interface CfdProposalView {
  id: string
  instrument: string
  sideLabel: string
  sideClass: string
  confirmationArmed: boolean
  unitsText: string
  entryText: string
  stopText: string
  takeProfitText: string
  maxLossText: string
  expiresText: string
  rationale: string
}
