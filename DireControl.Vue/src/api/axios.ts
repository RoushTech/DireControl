import axios from 'axios'

const http = axios.create({
  baseURL: '/',
  timeout: 30_000,
})

/**
 * Like apiErrorMessage, but prefers the server-provided detail text (plain
 * string body or `{ message }` object) when the API explains the failure.
 */
export function apiErrorDetail(err: unknown): string {
  if (axios.isAxiosError(err)) {
    const data: unknown = err.response?.data
    if (typeof data === 'string' && data.trim()) return data
    if (data && typeof data === 'object') {
      const message = (data as { message?: unknown }).message
      if (typeof message === 'string' && message.trim()) return message
    }
  }
  return apiErrorMessage(err)
}

/** Human-readable message for a failed API call, for error banners and toasts. */
export function apiErrorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    if (err.code === 'ECONNABORTED') return 'Request timed out — the backend may be unreachable.'
    if (!err.response) return 'Backend unreachable.'
    return `Request failed (HTTP ${err.response.status}).`
  }
  return err instanceof Error ? err.message : 'Unknown error.'
}

export default http
