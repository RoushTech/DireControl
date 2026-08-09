import axios from 'axios'

const http = axios.create({
  baseURL: '/',
  timeout: 30_000,
})

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
