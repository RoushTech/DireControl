import { ref } from 'vue'
import { getSettings } from '@/api/stationsApi'

// Shared across every component: fetched once per page load.
const ownCallsign = ref<string | null>(null)
let loading: Promise<void> | null = null

/** Our station callsign (from settings), for telling own packets apart. */
export function useOwnCallsign() {
  if (!loading) {
    loading = getSettings()
      .then((s) => {
        ownCallsign.value = s.ourCallsign
      })
      .catch(() => {
        loading = null // try again next time a component asks
      })
  }
  return { ownCallsign }
}
