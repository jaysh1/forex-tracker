import type {
  CfdDashboardDto,
  DashboardActionResult,
  ForexDashboardDto,
  LoginResponse,
} from './types'

const tokenKey = 'forexhitl.token'

export class SessionExpiredError extends Error {
  constructor() {
    super('Session expired')
  }
}

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message)
  }
}

async function request<T>(url: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  const token = localStorage.getItem(tokenKey)
  if (token) headers.set('Authorization', `Bearer ${token}`)
  if (init.body) headers.set('Content-Type', 'application/json')

  const response = await fetch(url, { ...init, headers })
  if (response.status === 401) {
    localStorage.removeItem(tokenKey)
    throw new SessionExpiredError()
  }
  if (!response.ok) throw new ApiError(`Request failed (HTTP ${response.status}).`, response.status)
  if (response.status === 204) return undefined as T
  return await response.json() as T
}

export async function login(password: string): Promise<string | null> {
  const response = await fetch('/api/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ password }),
  })
  if (!response.ok) {
    try {
      const body = await response.json() as { error?: string }
      if (body.error) return body.error
    } catch {
      // Use the HTTP fallback below when the server did not return JSON.
    }
    return `Login failed (HTTP ${response.status}).`
  }

  const body = await response.json() as LoginResponse
  if (!body.token) return 'Unexpected response from server.'
  localStorage.setItem(tokenKey, body.token)
  return null
}

export const getForexState = () => request<ForexDashboardDto>('/api/state')
export const getCfdState = () => request<CfdDashboardDto>('/api/cfd/state')
export const decideForex = (id: string, action: 'approve' | 'reject') =>
  request<DashboardActionResult>(`/api/proposals/${encodeURIComponent(id)}/${action}`, { method: 'POST', headers: action === 'approve' ? { 'X-Approval-Request-Id': crypto.randomUUID() } : undefined })
export const decideCfd = (id: string, action: 'approve' | 'reject') =>
  request<DashboardActionResult>(`/api/cfd/proposals/${encodeURIComponent(id)}/${action}`, { method: 'POST', headers: action === 'approve' ? { 'X-Approval-Request-Id': crypto.randomUUID() } : undefined })

export async function logout(): Promise<void> {
  try {
    await request<void>('/api/logout', { method: 'POST' })
  } catch {
    // Revoke best-effort; always clear this browser's token even while offline.
  } finally {
    localStorage.removeItem(tokenKey)
  }
}

export const hasSession = () => localStorage.getItem(tokenKey) !== null
