<script setup lang="ts">
import { ref } from 'vue'
import { toggleWatch } from '@/api/stationsApi'
import type { StationDto } from '@/types/station'
import { formatDateTime, timeAgo } from '@/utils/time'
import { useToastStore } from '@/stores/toastStore'

defineProps<{
  watchlist: StationDto[]
  now: number
}>()

const emit = defineEmits<{ removed: [callsign: string] }>()

const toastStore = useToastStore()
const unwatching = ref<Record<string, boolean>>({})

async function unwatch(station: StationDto) {
  unwatching.value[station.callsign] = true
  try {
    await toggleWatch(station.callsign)
    emit('removed', station.callsign)
    toastStore.toast(`${station.callsign} removed from the watch list`, 'info')
  } catch {
    toastStore.toast(`Couldn't update the watch list — the backend may be unreachable.`, 'error')
  } finally {
    unwatching.value[station.callsign] = false
  }
}

const stationTo = (callsign: string) => `/stations/${encodeURIComponent(callsign)}`
</script>

<template>
  <v-card variant="outlined">
    <v-card-title class="card-title d-flex align-center ga-2">
      Watch list
      <v-chip size="small" variant="tonal" color="primary">{{ watchlist.length }}</v-chip>
    </v-card-title>
    <v-card-text v-if="watchlist.length === 0" class="text-medium-emphasis">
      No watched stations. Star a station from its page to get an alert when it comes back on the
      air.
    </v-card-text>
    <v-list v-else density="compact">
      <v-list-item v-for="s in watchlist" :key="s.callsign">
        <template #prepend>
          <v-icon color="amber" size="18">mdi-star</v-icon>
        </template>
        <v-list-item-title>
          <router-link class="callsign-link" :to="stationTo(s.callsign)">
            {{ s.callsign }}
          </router-link>
        </v-list-item-title>
        <v-list-item-subtitle>
          heard {{ timeAgo(s.lastSeen, now) }} · {{ formatDateTime(s.lastSeen) }}
        </v-list-item-subtitle>
        <template #append>
          <v-chip color="success" size="small" variant="tonal" class="mr-2">alerting</v-chip>
          <v-btn
            icon="mdi-star-off"
            size="small"
            variant="text"
            :aria-label="`Remove ${s.callsign} from watch list`"
            :loading="unwatching[s.callsign]"
            @click="unwatch(s)"
          />
        </template>
      </v-list-item>
    </v-list>
  </v-card>
</template>

<style scoped>
.card-title {
  font-size: 14px;
  font-weight: 650;
}

.callsign-link {
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
  font-weight: 500;
}
</style>
