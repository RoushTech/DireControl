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
  [StationType.Digipeater]: 'Digi',
  [StationType.IGate]: 'IGate',
  [StationType.Gateway]: 'GW',
  [StationType.Unknown]: 'Unknown',
}

const stationTypeColor: Record<StationType, string> = {
  [StationType.Fixed]: 'blue',
  [StationType.Mobile]: 'green',
  [StationType.Weather]: 'teal',
  [StationType.Digipeater]: 'orange',
  [StationType.IGate]: 'purple',
  [StationType.Gateway]: 'deep-purple',
  [StationType.Unknown]: 'grey',
}

const heardViaLabel: Partial<Record<HeardVia, string>> = {
  [HeardVia.Direct]: 'Direct',
  [HeardVia.Digi]: 'Digi',
  [HeardVia.DirectAndDigi]: 'D+D',
  [HeardVia.IgateRf]: 'iGate',
  [HeardVia.IgateRfDigi]: 'iGate+D',
  [HeardVia.Internet]: 'Internet',
}

const heardViaColor: Partial<Record<HeardVia, string>> = {
  [HeardVia.Direct]: 'green',
  [HeardVia.Digi]: 'amber-darken-2',
  [HeardVia.DirectAndDigi]: 'teal',
  [HeardVia.IgateRf]: 'blue',
  [HeardVia.IgateRfDigi]: 'purple',
  [HeardVia.Internet]: 'grey',
}

function symbolStyle(s: StationDto) {
  const { table, code } = parseAprsSymbol(s.symbol ?? '/ ')
  return getSymbolStyle(table, code)
}

const filteredAndSorted = computed(() => {
  let list = props.stations

  const q = searchText.value.trim().toUpperCase()
  if (q) list = list.filter(s => s.callsign.toUpperCase().includes(q))

  if (showWeatherOnly.value) {
    list = list.filter(s => s.isWeatherStation)
  } else if (typeFilter.value !== null) {
    list = list.filter(s => s.stationType === typeFilter.value)
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
  if (q) list = list.filter(s => s.callsign.toUpperCase().includes(q))
  if (showWeatherOnly.value) {
    list = list.filter(s => s.isWeatherStation)
  } else if (typeFilter.value !== null) {
    list = list.filter(s => s.stationType === typeFilter.value)
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
        style="cursor:pointer"
        @click="emit('update:showStale', !showStale)"
      >
        {{ staleCount }} stale
      </v-chip>
    </div>

    <div class="sidebar-filters pa-2">
      <v-text-field
        ref="searchFieldRef"
        v-model="searchText"
        placeholder="Search callsign…"
        density="compact"
        variant="outlined"
        hide-details
        clearable
        class="mb-2"
      />
      <div class="d-flex ga-2 mb-2">
        <v-btn
          :color="showWeatherOnly ? 'teal' : 'default'"
          :variant="showWeatherOnly ? 'tonal' : 'outlined'"
          density="comfortable"
          prepend-icon="mdi-weather-partly-cloudy"
          @click="showWeatherOnly = !showWeatherOnly"
        >
          WX Only
        </v-btn>
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
        <div v-if="item.kind === 'divider'" class="stale-divider text-caption text-medium-emphasis px-3 py-1">
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
          <div :style="symbolStyle(item.station)" class="station-icon flex-shrink-0" :class="{ 'stale-icon': item.stale }" />
          <div class="station-info">
            <div class="d-flex align-center ga-1">
              <span class="text-body-2 font-weight-medium" :class="{ 'text-medium-emphasis': item.stale }">{{ item.station.callsign }}</span>
              <v-chip :color="item.stale ? 'grey' : stationTypeColor[item.station.stationType]" size="x-small" label>
                {{ stationTypeLabel[item.station.stationType] }}
              </v-chip>
              <v-chip
                v-if="!item.stale && heardViaLabel[item.station.heardVia]"
                :color="heardViaColor[item.station.heardVia]"
                size="x-small"
                label
              >
                {{ heardViaLabel[item.station.heardVia] }}
              </v-chip>
            </div>
            <div class="d-flex align-center ga-2 text-caption" :class="item.stale ? 'text-disabled' : 'text-medium-emphasis'">
              <span>{{ timeAgo(item.station.lastSeen, now) }}</span>
              <span v-if="!item.stale && packetCounts[item.station.callsign]">
                <v-icon size="10">mdi-radio-tower</v-icon> {{ packetCounts[item.station.callsign] }}
              </span>
            </div>
          </div>
        </div>
      </template>
    </v-virtual-scroll>
    <div v-else class="text-center text-medium-emphasis py-6 text-caption">
      No stations
    </div>
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

.station-info {
  flex: 1;
  min-width: 0;
}
</style>
