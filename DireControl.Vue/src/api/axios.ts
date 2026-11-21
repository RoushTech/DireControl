import axios from 'axios'

const http = axios.create({
  baseURL: '/',
  timeout: 30_000,
})

/**
 * Like apiErrorMessage, but prefers the server-provided detail text (plain
 * string body or `{ message }` object) when the API explains the failure.
 */
export function apiErrorDetail(err: unknown, prefix?: string): string {
  const detail = serverDetail(err) ?? apiErrorMessage(err)
  return prefix ? `${prefix}: ${detail}` : detail
}

/** Plain string body, `{ message }`, or ProblemDetails `{ detail }`. */
function serverDetail(err: unknown): string | null {
  if (!axios.isAxiosError(err)) return null
  const data: unknown = err.response?.data
  if (typeof data === 'string' && data.trim()) return data
  if (data && typeof data === 'object') {
    for (const key of ['message', 'detail'] as const) {
      const v = (data as Record<string, unknown>)[key]
      if (typeof v === 'string' && v.trim()) return v
    }
  }
  return null
}

/** Human-readable message for a failed API call, for error banners and toasts. */
function apiErrorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    if (err.code === 'ECONNABORTED') return 'Request timed out — the backend may be unreachable.'
    if (!err.response) return 'Backend unreachable.'
    return `Request failed (HTTP ${err.response.status}).`
  }
  return err instanceof Error ? err.message : 'Unknown error.'
}

export default http
