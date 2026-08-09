<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useBeaconStreamStore } from '@/stores/beaconStream'
import { usePacketHubStore } from '@/stores/packetHub'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { getPacketsSince } from '@/api/stationsApi'
import {
  PacketType,
  PacketSource,
  PACKET_TYPE_LABELS,
  PACKET_TYPE_COLORS,
  parsedTypeFromString,
  packetDtoToBroadcast,
  type PacketBroadcastDto,
} from '@/types/packet'
import { timeAgo, formatUtc, formatUtcTime } from '@/utils/time'
import { useTick } from '@/composables/useTick'
import PacketInspectionDialog from '@/components/PacketInspectionDialog.vue'

const router = useRouter()
const route = useRoute()
const store = useBeaconStreamStore()
const hub = usePacketHubStore()
const selectionStore = useStationSelectionStore()

const filterFieldRef = ref<{ focus: () => void } | null>(null)
const { now } = useTick(5000)

const connectionStatus = computed(() => hub.state)

const inspectedPacketId = ref<number | null>(null)

const packetTypeOptions = [
  { label: 'All Types', value: '' },
  { label: 'Position', value: `${PacketType.Position}` },
  { label: 'Message', value: `${PacketType.Message}` },
  { label: 'Weather', value: `${PacketType.Weather}` },
  { label: 'Telemetry', value: `${PacketType.Telemetry}` },
  { label: 'Unknown', value: `${PacketType.Unknown}` },
  { label: 'Unparseable', value: `${PacketType.Unparseable}` },
]

const sourceOptions = [
  { label: 'RF + IS', value: '' },
  { label: 'RF only', value: 'rf' },
  { label: 'APRS-IS only', value: 'is' },
]

/** Click the Time header to flip relative ("12s ago") ↔ absolute UTC. */
const absoluteTime = ref(false)

function timeLabel(p: PacketBroadcastDto): string {
  return absoluteTime.value ? formatUtcTime(p.receivedAt) : timeAgo(p.receivedAt, now.value)
}

function sourceChip(p: PacketBroadcastDto): { label: string; color: string } {
  return p.source === PacketSource.AprsIs
    ? { label: 'IS', color: 'is' }
    : { label: 'RF', color: 'rf' }
}

function typeLabel(parsedType: string): string {
  const pt = parsedTypeFromString(parsedType)
  return PACKET_TYPE_LABELS[pt] ?? parsedType
}

function typeColor(parsedType: string): string {
  const pt = parsedTypeFromString(parsedType)
  return PACKET_TYPE_COLORS[pt] ?? 'grey'
}

function onCallsignClick(callsign: string) {
  selectionStore.selectStation(callsign)
  router.push('/')
}

function openInspectDialog(id: number) {
  inspectedPacketId.value = id
}

function onDialogSelectStation(callsign: string) {
  selectionStore.selectStation(callsign)
  router.push('/')
}

const seedFailed = ref(false)

async function seedFromApi() {
  try {
    const since = new Date(Date.now() - 60 * 60 * 1000).toISOString()
    const packets = await getPacketsSince(since, 200)
    // API returns newest first — matches the live unshift convention (newest at top)
    store.seedFromApi(packets.map(packetDtoToBroadcast))
    seedFailed.value = false
  } catch {
    seedFailed.value = true
  }
}

onMounted(async () => {
  // Live packets flow into the store via the shared packet hub (see
  // stores/beaconStream.ts) — this view only seeds history and renders.
  await seedFromApi()
  window.addEventListener('shortcut:focus-search', onShortcutFocusSearch)
})

onUnmounted(() => {
  window.removeEventListener('shortcut:focus-search', onShortcutFocusSearch)
})

function onShortcutFocusSearch() {
  filterFieldRef.value?.focus()
}

function openPopOut() {
  window.open('/stream-only', '_blank', 'width=1000,height=700,noopener')
}
</script>

<template>
  <div class="beacon-view">
    <!-- Toolbar — one row: search + type + source, then status/actions -->
    <div class="beacon-toolbar">
      <v-text-field
        ref="filterFieldRef"
        v-model="store.searchFilter"
        placeholder="Search callsign or packet text…"
        prepend-inner-icon="mdi-magnify"
        density="compact"
        variant="outlined"
        hide-details
        clearable
        class="beacon-search"
      />
      <v-select
        v-model="store.typeFilter"
        :items="packetTypeOptions"
        item-title="label"
        item-value="value"
        density="compact"
        variant="outlined"
        hide-details
        aria-label="Packet type"
        class="beacon-select"
      />
      <v-select
        v-model="store.sourceFilter"
        :items="sourceOptions"
        item-title="label"
        item-value="value"
        density="compact"
        variant="outlined"
        hide-details
        aria-label="Packet source"
        class="beacon-select beacon-select--narrow"
      />

      <div class="d-flex align-center ga-2 ml-auto">
        <v-chip
          :color="
            connectionStatus === 'connected'
              ? 'success'
              : connectionStatus === 'connecting'
                ? 'warning'
                : 'error'
          "
          size="x-small"
          variant="flat"
          label
        >
          <v-icon start size="10">mdi-circle</v-icon>
          {{ connectionStatus }}
        </v-chip>

        <v-btn
          v-if="!store.paused"
          color="primary"
          size="small"
          variant="tonal"
          prepend-icon="mdi-pause"
          @click="store.pause()"
        >
          Pause
        </v-btn>
        <v-btn v-else color="green" size="small" variant="tonal" @click="store.unpause()">
          <v-icon start>mdi-play</v-icon>
          Unpause
          <v-badge
            v-if="store.pendingCount > 0"
            :content="store.pendingCount > 99 ? '99+' : store.pendingCount"
            color="error"
            class="ml-2"
            inline
          />
        </v-btn>

        <v-btn
          v-if="!route.meta.isPopOut"
          icon="mdi-open-in-new"
          size="small"
          variant="tonal"
          :title="'Open stream in new window'"
          @click="openPopOut"
        />
      </div>
    </div>

    <v-divider />

    <!-- Header row (cells share the row classes so widths can never drift apart) -->
    <div class="beacon-header">
      <button
        class="beacon-cell beacon-time beacon-th text-caption font-weight-medium text-medium-emphasis"
        :title="
          absoluteTime ? 'Showing UTC — click for relative' : 'Showing relative — click for UTC'
        "
        @click="absoluteTime = !absoluteTime"
      >
        Time <v-icon size="10">mdi-swap-horizontal</v-icon>
      </button>
      <span class="beacon-cell beacon-callsign text-caption font-weight-medium text-medium-emphasis"
        >Callsign</span
      >
      <span class="beacon-cell beacon-type text-caption font-weight-medium text-medium-emphasis"
        >Type</span
      >
      <span class="beacon-cell beacon-src text-caption font-weight-medium text-medium-emphasis"
        >Src</span
      >
      <span class="text-caption font-weight-medium text-medium-emphasis">Summary</span>
    </div>

    <v-divider />

    <!-- Paused banner -->
    <div v-if="store.paused" class="beacon-paused-banner">
      <v-icon size="14">mdi-pause</v-icon>
      Paused — {{ store.pendingCount }} new packet{{ store.pendingCount === 1 ? '' : 's' }} buffered
      <v-btn size="x-small" variant="text" color="primary" @click="store.unpause()">Resume</v-btn>
    </div>

    <!-- Packet list: distinct error / empty / filtered-out states -->
    <div
      v-if="seedFailed && store.displayedPackets.length === 0"
      class="text-center text-medium-emphasis py-8"
    >
      <div class="mb-2">Couldn't load packet history — the backend may be unreachable.</div>
      <div class="text-caption mb-3">
        Live packets will still appear when the connection recovers.
      </div>
      <v-btn size="small" color="primary" variant="tonal" @click="seedFromApi">Retry</v-btn>
    </div>
    <div
      v-else-if="store.displayedPackets.length === 0"
      class="text-center text-medium-emphasis py-8"
    >
      No packets heard in the last hour — waiting for traffic…
    </div>
    <div
      v-else-if="store.filteredPackets.length === 0"
      class="text-center text-medium-emphasis py-8"
    >
      <div class="mb-3">
        No packets match your filters ({{ store.displayedPackets.length }} hidden).
      </div>
      <v-btn size="small" color="primary" variant="tonal" @click="store.clearFilters()">
        Clear filters
      </v-btn>
    </div>
    <v-virtual-scroll
      v-else
      class="beacon-list"
      :class="{ 'beacon-list--paused': store.paused }"
      :items="store.filteredPackets"
      :item-height="36"
    >
      <template #default="{ item: p }">
        <div :key="p.id" class="beacon-row" @click="openInspectDialog(p.id)">
          <span
            class="beacon-cell beacon-time text-caption text-medium-emphasis"
            :title="formatUtc(p.receivedAt)"
          >
            {{ timeLabel(p) }}
          </span>
          <span class="beacon-cell beacon-callsign">
            <a class="callsign-link" @click.stop.prevent="onCallsignClick(p.callsign)">
              {{ p.callsign }}
            </a>
          </span>
          <span class="beacon-cell beacon-type">
            <v-chip :color="typeColor(p.parsedType)" size="x-small" label>
              {{ typeLabel(p.parsedType) }}
            </v-chip>
          </span>
          <span class="beacon-cell beacon-src">
            <v-chip :color="sourceChip(p).color" size="x-small" variant="tonal">
              {{ sourceChip(p).label }}
            </v-chip>
          </span>
          <span class="beacon-cell beacon-summary text-body-2 text-truncate" :title="p.summary">
            {{ p.summary }}
          </span>
        </div>
      </template>
    </v-virtual-scroll>

    <PacketInspectionDialog
      :packet-id="inspectedPacketId"
      @close="inspectedPacketId = null"
      @select-station="onDialogSelectStation"
    />
  </div>
</template>

<style scoped>
.beacon-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

.beacon-toolbar {
  display: flex;
  align-items: center;
  padding: 8px 12px;
  gap: 8px;
  flex-shrink: 0;
  flex-wrap: wrap;
}

.beacon-search {
  flex: 1 1 240px;
  min-width: 180px;
}

.beacon-select {
  flex: 0 1 170px;
  min-width: 130px;
}

.beacon-select--narrow {
  flex-basis: 150px;
}

.beacon-th {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  background: none;
  border: none;
  padding: 0;
  cursor: pointer;
  font: inherit;
  text-align: left;
}

.beacon-th:hover {
  color: rgba(var(--v-theme-primary), 1);
}

.beacon-paused-banner {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  font-size: 0.8rem;
  font-weight: 600;
  color: rgb(var(--v-theme-warning));
  background: rgba(var(--v-theme-warning), 0.12);
  flex-shrink: 0;
}

.beacon-list--paused {
  opacity: 0.55;
}

.beacon-header {
  display: flex;
  align-items: center;
  padding: 4px 12px;
  gap: 8px;
  flex-shrink: 0;
  background: rgba(var(--v-theme-on-surface), 0.04);
}

.beacon-list {
  flex: 1;
  min-height: 0;
}

.beacon-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 12px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.06);
  min-height: 36px;
  cursor: pointer;
}

.beacon-row:hover {
  background: rgba(var(--v-theme-on-surface), 0.04);
}

.beacon-cell {
  flex-shrink: 0;
}

.beacon-time {
  width: 72px;
}

.beacon-callsign {
  width: 110px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.beacon-type {
  width: 86px;
}

.beacon-src {
  width: 44px;
}

.beacon-summary {
  flex: 1;
  min-width: 0;
  max-width: 100%;
}

.callsign-link {
  color: rgba(var(--v-theme-primary), 1);
  cursor: pointer;
  text-decoration: none;
  font-size: 0.875rem;
  font-weight: 500;
}

.callsign-link:hover {
  text-decoration: underline;
}
</style>
