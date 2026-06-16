import axios from 'axios'

/** Returns the backend's 400 message (a plain string body) when present, else the fallback. */
export function apiErrorText(error: unknown, fallback: string): string {
  if (
    axios.isAxiosError(error) &&
    error.response?.status === 400 &&
    typeof error.response.data === 'string' &&
    error.response.data
  ) {
    return error.response.data
  }
  return fallback
}
