<script setup lang="ts">
import { ref, computed } from 'vue'
import { StationType, HeardVia, type StationDto } from '@/types/station'
import { timeAgo } from '@/utils/time'
import { getSymbolStyle, parseAprsSymbol } from '@/utils/aprsIcon'
import { useTick } from '@/composables/useTick'

const props = defineProps<{
  stations: StationDto[]
  packetCounts: Record<string, number>
  selectedCallsign: string | null
  staleStations?: StationDto[]
  showStale?: boolean
}>()

const emit = defineEmits<{
  selectStation: [callsign: string]
  'update:showStale': [value: boolean]
}>()

type SortKey = 'callsign' | 'lastSeen' | 'packets'

const typeFilter = ref<StationType | null>(null)
const sortKey = ref<SortKey>('lastSeen')
const searchText = ref('')
const showWeatherOnly = ref(false)
const searchFieldRef = ref<{ focus: () => void } | null>(null)

defineExpose({
  focusSearch() {
    searchFieldRef.value?.focus()
  },
})

const { now } = useTick(5000)

const stationTypeOptions = [
  { label: 'All Types', value: null },
  { label: 'Fixed', value: StationType.Fixed },
  { label: 'Mobile', value: StationType.Mobile },
  { label: 'Weather', value: StationType.Weather },
  { label: 'Digipeater', value: StationType.Digipeater },
  { label: 'IGate', value: StationType.IGate },
  { label: 'Gateway', value: StationType.Gateway },
  { label: 'Unknown', value: StationType.Unknown },
]

const stationTypeLabel: Record<StationType, string> = {
  [StationType.Fixed]: 'Fixed',
  [StationType.Mobile]: 'Mobile',
  [StationType.Weather]: 'Weather',
  [StationType.Digipeater]: 'Digipeater',
  [StationType.IGate]: 'IGate',
  [StationType.Gateway]: 'Gateway',
  [StationType.Unknown]: 'Unknown',
}

const heardViaLabel: Partial<Record<HeardVia, string>> = {
  [HeardVia.Direct]: 'direct',
  [HeardVia.Digi]: 'via digi',
  [HeardVia.DirectAndDigi]: 'direct + digi',
  [HeardVia.IgateRf]: 'via iGate',
  [HeardVia.IgateRfDigi]: 'via iGate + digi',
  [HeardVia.Internet]: 'APRS-IS',
}

// Mock rows: one muted subtitle line ("Mobile · via digi"), not a chip salad.
function rowSubtitle(s: StationDto): string {
  const parts = [stationTypeLabel[s.stationType] ?? 'Unknown']
  const via = heardViaLabel[s.heardVia]
  if (via) parts.push(via)
  return parts.join(' · ')
}

/** Bare short age for the right edge: "2m", not "2m ago". */
function shortAgo(iso: string): string {
  return timeAgo(iso, now.value).replace(/ ago$/, '')
}

function symbolStyle(s: StationDto) {
  const { table, code } = parseAprsSymbol(s.symbol ?? '/ ')
  return getSymbolStyle(table, code)
}

const filteredAndSorted = computed(() => {
  let list = props.stations

  const q = searchText.value.trim().toUpperCase()
  if (q) list = list.filter((s) => s.callsign.toUpperCase().includes(q))

  if (showWeatherOnly.value) {
    list = list.filter((s) => s.isWeatherStation)
  } else if (typeFilter.value !== null) {
    list = list.filter((s) => s.stationType === typeFilter.value)
  }

  return [...list].sort((a, b) => {
    if (sortKey.value === 'callsign') return a.callsign.localeCompare(b.callsign)
    if (sortKey.value === 'lastSeen')
      return new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime()
    if (sortKey.value === 'packets') {
      const pa = props.packetCounts[a.callsign] ?? 0
      const pb = props.packetCounts[b.callsign] ?? 0
      return pb - pa
    }
    return 0
  })
})

const filteredStale = computed(() => {
  if (!props.showStale || !props.staleStations?.length) return []
  let list = props.staleStations
  const q = searchText.value.trim().toUpperCase()
  if (q) list = list.filter((s) => s.callsign.toUpperCase().includes(q))
  if (showWeatherOnly.value) {
    list = list.filter((s) => s.isWeatherStation)
  } else if (typeFilter.value !== null) {
    list = list.filter((s) => s.stationType === typeFilter.value)
  }
  return [...list].sort((a, b) => new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime())
})

const staleCount = computed(() => props.staleStations?.length ?? 0)

type VirtualItem =
  | { kind: 'station'; station: StationDto; stale: false }
  | { kind: 'divider' }
  | { kind: 'station'; station: StationDto; stale: true }

const virtualItems = computed<VirtualItem[]>(() => {
  const items: VirtualItem[] = filteredAndSorted.value.map((s) => ({
    kind: 'station' as const,
    station: s,
    stale: false as const,
  }))
  const stale = filteredStale.value
  if (stale.length > 0) {
    items.push({ kind: 'divider' })
    for (const s of stale) {
      items.push({ kind: 'station', station: s, stale: true })
    }
  }
  return items
})
</script>

<template>
  <div class="sidebar-content">
    <div class="sidebar-header">
      <span class="text-subtitle-2 font-weight-bold">Stations</span>
      <v-chip size="x-small" color="primary" class="ml-1">{{ stations.length }}</v-chip>
      <v-chip
        v-if="staleCount > 0"
        size="x-small"
        :color="showStale ? 'brown-lighten-1' : 'grey'"
        class="ml-1"
        style="cursor: pointer"
        @click="emit('update:showStale', !showStale)"
      >
        {{ staleCount }} stale
      </v-chip>
    </div>

    <div class="sidebar-filters pa-2">
      <!-- Search + WX toggle share one row so the list starts sooner -->
      <div class="d-flex ga-2 mb-2 align-center">
        <v-text-field
          ref="searchFieldRef"
          v-model="searchText"
          placeholder="Search callsign…"
          density="compact"
          variant="outlined"
          hide-details
          clearable
          class="flex-1"
        />
        <v-btn
          :color="showWeatherOnly ? 'teal' : 'default'"
          :variant="showWeatherOnly ? 'tonal' : 'outlined'"
          density="comfortable"
          icon="mdi-weather-partly-cloudy"
          size="small"
          :title="showWeatherOnly ? 'Showing weather stations only' : 'Show weather stations only'"
          @click="showWeatherOnly = !showWeatherOnly"
        />
      </div>
      <div class="d-flex ga-2">
        <v-select
          v-model="typeFilter"
          :items="stationTypeOptions"
          item-title="label"
          item-value="value"
          label="Type"
          density="compact"
          variant="outlined"
          hide-details
          class="flex-1"
          :menu-props="{ minWidth: 160 }"
        />
        <v-select
          v-model="sortKey"
          :items="[
            { label: 'Last Seen', value: 'lastSeen' },
            { label: 'Callsign', value: 'callsign' },
            { label: 'Packets', value: 'packets' },
          ]"
          item-title="label"
          item-value="value"
          label="Sort"
          density="compact"
          variant="outlined"
          hide-details
          class="flex-1"
          :menu-props="{ minWidth: 140 }"
        />
      </div>
    </div>

    <v-divider />

    <v-virtual-scroll
      v-if="virtualItems.length > 0"
      :items="virtualItems"
      item-height="50"
      class="station-list"
    >
      <template #default="{ item }">
        <div
          v-if="item.kind === 'divider'"
          class="stale-divider text-caption text-medium-emphasis px-3 py-1"
        >
          <v-icon size="12" class="mr-1">mdi-clock-alert-outline</v-icon>Stale
        </div>
        <div
          v-else
          class="station-row"
          :class="{
            'station-row--selected': item.station.callsign === selectedCallsign,
            'station-row--stale': item.stale,
          }"
          @click="emit('selectStation', item.station.callsign)"
        >
          <div
            :style="symbolStyle(item.station)"
            class="station-icon flex-shrink-0"
            :class="{ 'stale-icon': item.stale }"
          />
          <!-- Mock row: symbol · callsign / muted subtitle ····· age -->
          <div class="station-info">
            <span class="station-row-callsign" :class="{ 'text-medium-emphasis': item.stale }">{{
              item.station.callsign
            }}</span>
            <div
              class="text-caption station-row-sub"
              :class="item.stale ? 'text-disabled' : 'text-medium-emphasis'"
            >
              {{ rowSubtitle(item.station) }}
              <template v-if="!item.stale && packetCounts[item.station.callsign]">
                · {{ packetCounts[item.station.callsign] }} pkts
              </template>
            </div>
          </div>
          <span
            class="station-row-ago text-caption"
            :class="item.stale ? 'text-disabled' : 'text-medium-emphasis'"
          >
            {{ shortAgo(item.station.lastSeen) }}
          </span>
        </div>
      </template>
    </v-virtual-scroll>
    <div v-else class="text-center text-medium-emphasis py-6 text-caption">No stations</div>
  </div>
</template>

<style scoped>
.sidebar-content {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: hidden;
  background: rgb(var(--v-theme-surface));
  border-right: 1px solid rgba(var(--v-theme-on-surface), 0.12);
}

.sidebar-header {
  display: flex;
  align-items: center;
  padding: 10px 12px 6px;
  flex-shrink: 0;
}

.sidebar-filters {
  flex-shrink: 0;
}

.station-list {
  flex: 1;
  min-height: 0;
}

.station-icon {
  image-rendering: pixelated;
  border-radius: 2px;
  flex-shrink: 0;
}

.station-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  cursor: pointer;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.06);
  transition: background 0.15s;
}

.station-row:hover {
  background: rgba(var(--v-theme-on-surface), 0.05);
}

.station-row--selected {
  background: rgba(var(--v-theme-primary), 0.15);
}

.station-row--stale {
  opacity: 0.6;
}

.station-info {
  flex: 1;
  min-width: 0;
}

.station-row-callsign {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-weight: 600;
  font-size: 0.85rem;
  color: rgb(var(--v-theme-primary));
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.station-row-sub {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.station-row-ago {
  flex-shrink: 0;
  font-variant-numeric: tabular-nums;
}

.stale-icon {
  filter: grayscale(80%);
}

.stale-divider {
  background: rgba(var(--v-theme-on-surface), 0.04);
  border-top: 1px solid rgba(var(--v-theme-on-surface), 0.1);
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.06);
  display: flex;
  align-items: center;
}
</style>
