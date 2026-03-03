<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import {
  HubConnectionBuilder,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import { useBeaconStreamStore } from '@/stores/beaconStream'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { getPacketsSince } from '@/api/stationsApi'
import {
  PacketType,
  PACKET_TYPE_LABELS,
  PACKET_TYPE_COLORS,
  parsedTypeFromString,
  type PacketBroadcastDto,
  type PacketDto,
} from '@/types/packet'
import { timeAgo, formatUtc } from '@/utils/time'
import PacketInspectionDialog from '@/components/PacketInspectionDialog.vue'

const router = useRouter()
const route = useRoute()
const store = useBeaconStreamStore()
const selectionStore = useStationSelectionStore()

const filterFieldRef = ref<{ focus: () => void } | null>(null)

let connection: HubConnection | null = null
const connectionStatus = ref<'connecting' | 'connected' | 'disconnected'>('connecting')

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

function packetDtoToStreamEntry(p: PacketDto): PacketBroadcastDto {
  return {
    id: p.id,
    callsign: p.stationCallsign,
    parsedType: PACKET_TYPE_LABELS[p.parsedType as PacketType] ?? 'Unknown',
    receivedAt: typeof p.receivedAt === 'string' ? p.receivedAt : new Date(p.receivedAt).toISOString(),
    latitude: p.latitude,
    longitude: p.longitude,
    summary: p.comment || (PACKET_TYPE_LABELS[p.parsedType as PacketType] ?? 'Unknown'),
    hopCount: p.hopCount,
    resolvedPath: p.resolvedPath,
    source: p.source,
  }
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

async function seedFromApi() {
  try {
    const since = new Date(Date.now() - 60 * 60 * 1000).toISOString()
    const packets = await getPacketsSince(since, 200)
    // API returns ascending (oldest first) — seed in that order so newest is at bottom
    store.seedFromApi(packets.map(packetDtoToStreamEntry))
  } catch {
    // ignore
  }
}

async function connectSignalR() {
  connectionStatus.value = 'connecting'
  connection = new HubConnectionBuilder()
    .withUrl('/hubs/packets')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  connection.on('packetReceived', (packet: PacketBroadcastDto) => {
    store.addPacket(packet)
  })

  connection.onreconnecting(() => { connectionStatus.value = 'connecting' })
  connection.onreconnected(() => { connectionStatus.value = 'connected' })
  connection.onclose(() => { connectionStatus.value = 'disconnected' })

  try {
    await connection.start()
    connectionStatus.value = 'connected'
  } catch {
    connectionStatus.value = 'disconnected'
  }
}

onMounted(async () => {
  await seedFromApi()
  await connectSignalR()
  window.addEventListener('shortcut:focus-search', onShortcutFocusSearch)
})

onUnmounted(async () => {
  if (connection) {
    await connection.stop()
    connection = null
  }
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
    <!-- Toolbar -->
    <div class="beacon-toolbar">
      <div class="beacon-filters">
        <div class="beacon-filter-row">
          <v-text-field
            ref="filterFieldRef"
            v-model="store.callsignFilter"
            placeholder="Callsign filter"
            density="compact"
            variant="outlined"
            hide-details
            clearable
            class="beacon-filter-input"
          />
        </div>
        <div class="beacon-filter-row">
          <v-text-field
            v-model="store.textFilter"
            placeholder="Search packets…"
            density="compact"
            variant="outlined"
            hide-details
            clearable
            class="beacon-filter-input"
          />
        </div>
        <div class="beacon-filter-row">
          <v-select
            v-model="store.typeFilter"
            :items="packetTypeOptions"
            item-title="label"
            item-value="value"
            label="Type"
            density="compact"
            variant="outlined"
            hide-details
            class="beacon-filter-input"
          />
        </div>
      </div>

      <div class="d-flex align-center ga-2">
        <v-chip
          :color="connectionStatus === 'connected' ? 'success' : connectionStatus === 'connecting' ? 'warning' : 'error'"
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
        <v-btn
          v-else
          color="green"
          size="small"
          variant="tonal"
          @click="store.unpause()"
        >
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

    <!-- Header row -->
    <div class="beacon-header">
      <span style="width: 80px" class="text-caption font-weight-medium text-medium-emphasis">Time</span>
      <span style="width: 110px" class="text-caption font-weight-medium text-medium-emphasis">Callsign</span>
      <span style="width: 90px" class="text-caption font-weight-medium text-medium-emphasis">Type</span>
      <span class="text-caption font-weight-medium text-medium-emphasis">Summary</span>
    </div>

    <v-divider />

    <!-- Packet list -->
    <div class="beacon-list">
      <div
        v-for="p in store.filteredPackets"
        :key="`${p.callsign}-${p.receivedAt}`"
        class="beacon-row"
        @click="openInspectDialog(p.id)"
      >
        <span class="beacon-cell beacon-time text-caption text-medium-emphasis" :title="formatUtc(p.receivedAt)">
          {{ timeAgo(p.receivedAt) }}
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
        <span class="beacon-cell beacon-summary text-body-2 text-truncate" :title="p.summary">
          {{ p.summary }}
        </span>
      </div>
      <div v-if="store.filteredPackets.length === 0" class="text-center text-medium-emphasis py-8">
        No packets heard yet — waiting for Direwolf…
      </div>
    </div>

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
  align-items: flex-start;
  justify-content: space-between;
  padding: 8px 12px;
  gap: 8px;
  flex-shrink: 0;
}

.beacon-filters {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.beacon-filter-row {
  width: 100%;
}

.beacon-filter-input {
  width: 100%;
  min-width: 0;
  min-height: 44px;
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
  overflow-y: auto;
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
