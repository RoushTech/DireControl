<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import StationDetailPanel from '@/components/StationDetailPanel.vue'
import { usePacketHubStore } from '@/stores/packetHub'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { useUiStore } from '@/stores/uiStore'
import { useToastStore } from '@/stores/toastStore'
import { getStation, toggleWatch } from '@/api/stationsApi'
import { StationType, type StationDto } from '@/types/station'
import type { PacketBroadcastDto } from '@/types/packet'
import { getSymbolStyle, parseAprsSymbol } from '@/utils/aprsIcon'
import { timeAgo, formatUtc } from '@/utils/time'
import { useTick } from '@/composables/useTick'

const route = useRoute()
const router = useRouter()
const hub = usePacketHubStore()
const selectionStore = useStationSelectionStore()
const uiStore = useUiStore()
const toastStore = useToastStore()
const { now } = useTick(5000)

const callsign = computed(() => String(route.params.callsign ?? '').toUpperCase())

// Page-header station data — the panel fetches its own copy for tab content.
const station = ref<StationDto | null>(null)
const watchLoading = ref(false)

const STATION_TYPE_LABELS: Record<StationType, string> = {
  [StationType.Fixed]: 'Fixed',
  [StationType.Mobile]: 'Mobile',
  [StationType.Weather]: 'Weather',
  [StationType.Digipeater]: 'Digipeater',
  [StationType.IGate]: 'IGate',
  [StationType.Unknown]: 'Unknown',
  [StationType.Gateway]: 'Gateway',
}

const symbolStyle = computed(() => {
  if (!station.value?.symbol) return {}
  const { table, code } = parseAprsSymbol(station.value.symbol)
  return getSymbolStyle(table, code)
})

const metaLabel = computed(() => {
  const s = station.value
  if (!s) return ''
  const parts = [STATION_TYPE_LABELS[s.stationType] ?? 'Unknown']
  parts.push(`heard ${timeAgo(s.lastSeen, now.value)}`)
  if (s.gridSquare) parts.push(`grid ${s.gridSquare}`)
  return parts.join(' · ')
})

async function loadStation() {
  try {
    station.value = await getStation(callsign.value)
  } catch {
    station.value = null
  }
}

async function toggleWatchStatus() {
  watchLoading.value = true
  try {
    await toggleWatch(callsign.value)
    await loadStation()
    toastStore.toast(
      station.value?.isOnWatchList
        ? `${callsign.value} added to watchlist`
        : `${callsign.value} removed from watchlist`,
      'info',
    )
  } catch {
    toastStore.toast(`Couldn't update the watchlist — the backend may be unreachable.`, 'error')
  } finally {
    watchLoading.value = false
  }
}

// Bump the panel's refresh key when this station transmits, so packets/weather/
// signal tabs stay current — same contract the map uses.
const refreshKey = ref(0)

function onHubPacketReceived(packet: PacketBroadcastDto) {
  if (packet.callsign === callsign.value) {
    refreshKey.value++
    void loadStation()
  }
}

function showOnMap() {
  selectionStore.selectStation(callsign.value)
  router.push('/')
}

function messageStation() {
  uiStore.triggerCompose()
  router.push('/messages')
}

function onHighlightPosition() {
  // Position clicks make sense on the map — jump there with the station selected.
  showOnMap()
}

function onClose() {
  if (window.history.length > 1) router.back()
  else router.push('/')
}

watch(callsign, () => void loadStation(), { immediate: false })

onMounted(() => {
  void loadStation()
  hub.on('packetReceived', onHubPacketReceived)
})

onUnmounted(() => {
  hub.off('packetReceived', onHubPacketReceived)
})
</script>

<template>
  <div class="station-page">
    <!-- Page header — mock: symbol · callsign + star · meta · actions -->
    <div class="station-page-head">
      <v-btn icon="mdi-arrow-left" variant="text" size="small" aria-label="Back" @click="onClose" />
      <div class="station-symbol">
        <div :style="symbolStyle" />
      </div>
      <div class="min-width-0">
        <div class="d-flex align-center ga-1">
          <span class="text-h5 font-weight-bold station-callsign">{{ callsign }}</span>
          <v-btn
            :icon="station?.isOnWatchList ? 'mdi-star' : 'mdi-star-outline'"
            :color="station?.isOnWatchList ? 'amber' : 'default'"
            variant="text"
            size="small"
            :loading="watchLoading"
            :disabled="!station"
            title="Toggle watch list"
            @click="toggleWatchStatus"
          />
          <v-chip
            v-if="station?.isWeatherStation"
            color="teal"
            size="x-small"
            variant="tonal"
            class="ml-1"
          >
            WX
          </v-chip>
        </div>
        <div
          class="text-caption text-medium-emphasis"
          :title="station ? formatUtc(station.lastSeen) : undefined"
        >
          {{ metaLabel }}
        </div>
      </div>
      <v-spacer />
      <v-btn
        size="small"
        variant="tonal"
        color="primary"
        prepend-icon="mdi-map-marker"
        @click="showOnMap"
      >
        Show on map
      </v-btn>
      <v-btn
        size="small"
        variant="outlined"
        prepend-icon="mdi-message-text-outline"
        @click="messageStation"
      >
        Message
      </v-btn>
    </div>
    <v-divider />

    <!-- Tabbed content — the detail panel in page mode (horizontal tabs) -->
    <div class="station-page-body">
      <StationDetailPanel
        :callsign="callsign"
        :refresh-key="refreshKey"
        page-variant
        @close="onClose"
        @highlight-position="onHighlightPosition"
      />
    </div>
  </div>
</template>

<style scoped>
.station-page {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.station-page-head {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  flex-shrink: 0;
  flex-wrap: wrap;
}

.station-symbol {
  width: 48px;
  height: 48px;
  border-radius: 12px;
  background: rgba(var(--v-theme-on-surface), 0.06);
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  image-rendering: pixelated;
}

.station-callsign {
  font-variant-numeric: tabular-nums;
}

.min-width-0 {
  min-width: 0;
}

.station-page-body {
  flex: 1;
  min-height: 0;
  max-width: 1160px;
  width: 100%;
  margin: 0 auto;
  padding: 12px 16px 16px;
}

.station-page-body :deep(.detail-panel-content) {
  border: 1px solid rgba(var(--v-theme-on-surface), 0.12);
  border-radius: 12px;
}
</style>
