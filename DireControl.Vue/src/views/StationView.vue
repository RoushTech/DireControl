<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import StationDetailPanel from '@/components/StationDetailPanel.vue'
import { usePacketHubStore } from '@/stores/packetHub'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { useUiStore } from '@/stores/uiStore'
import { useToastStore } from '@/stores/toastStore'
import {
  getSettings,
  getStation,
  getStationPackets,
  getStationSignal,
  getStationStats,
  toggleWatch,
} from '@/api/stationsApi'
import { HeardVia, StationType, type StationDto, type StationStatisticDto } from '@/types/station'
import {
  PacketSource,
  type PacketBroadcastDto,
  type PacketDto,
  type ResolvedPathEntry,
  type SignalPointDto,
} from '@/types/packet'
import { getSymbolStyle, parseAprsSymbol } from '@/utils/aprsIcon'
import { timeAgo, formatUtc, compassDir } from '@/utils/time'
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

// ─── Info-tab data (mock two-column layout) ──────────────────────────────────
const stats = ref<StationStatisticDto | null>(null)
const signalPoints = ref<SignalPointDto[]>([])
const home = ref<{ lat: number; lon: number } | null>(null)

async function loadInfoData() {
  try {
    stats.value = await getStationStats(callsign.value)
  } catch {
    stats.value = null
  }
  try {
    signalPoints.value = await getStationSignal(callsign.value, 24)
  } catch {
    signalPoints.value = []
  }
}

function haversineMiles(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const rad = Math.PI / 180
  const dLat = (lat2 - lat1) * rad
  const dLon = (lon2 - lon1) * rad
  const a =
    Math.sin(dLat / 2) ** 2 + Math.cos(lat1 * rad) * Math.cos(lat2 * rad) * Math.sin(dLon / 2) ** 2
  return 3958.8 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a))
}

function bearingDeg(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const rad = Math.PI / 180
  const y = Math.sin((lon2 - lon1) * rad) * Math.cos(lat2 * rad)
  const x =
    Math.cos(lat1 * rad) * Math.sin(lat2 * rad) -
    Math.sin(lat1 * rad) * Math.cos(lat2 * rad) * Math.cos((lon2 - lon1) * rad)
  return (Math.atan2(y, x) / rad + 360) % 360
}

/** "19.4 mi NW of home" — needs both a home position and a station fix. */
const distanceLabel = computed(() => {
  const s = station.value
  const h = home.value
  if (!s || !h || s.lastLat == null || s.lastLon == null) return null
  const mi = haversineMiles(h.lat, h.lon, s.lastLat, s.lastLon)
  const dir = compassDir(bearingDeg(h.lat, h.lon, s.lastLat, s.lastLon))
  return `${mi.toFixed(1)} mi ${dir} of home`
})

// Mock meta: "Mobile · heard 2m ago via WE4MB-3 · 19.4 mi NW of home · 1 hop"
const metaLabel = computed(() => {
  const s = station.value
  if (!s) return ''
  const parts = [STATION_TYPE_LABELS[s.stationType] ?? 'Unknown']
  let heard = `heard ${timeAgo(s.lastSeen, now.value)}`
  const p = latestPacket.value
  const firstDigi = p && p.hopCount > 0 ? p.resolvedPath[1]?.callsign : null
  if (firstDigi) heard += ` via ${firstDigi}`
  parts.push(heard)
  if (distanceLabel.value) parts.push(distanceLabel.value)
  if (p && p.source !== PacketSource.AprsIs)
    parts.push(p.hopCount === 0 ? 'direct' : `${p.hopCount} hop${p.hopCount === 1 ? '' : 's'}`)
  return parts.join(' · ')
})

const avgHops = computed(() => {
  const pts = signalPoints.value
  if (pts.length === 0) return null
  return pts.reduce((sum, p) => sum + p.hopCount, 0) / pts.length
})

const avgAudio = computed(() => {
  const pts = signalPoints.value.filter((p) => p.audioLevel != null)
  if (pts.length === 0) return null
  return pts.reduce((sum, p) => sum + (p.audioLevel ?? 0), 0) / pts.length
})

const firstHeardLabel = computed(() => {
  if (!station.value) return '—'
  const d = new Date(station.value.firstSeen)
  const opts: Intl.DateTimeFormatOptions = { month: 'short', day: 'numeric' }
  if (d.getFullYear() !== new Date(now.value).getFullYear()) opts.year = '2-digit'
  return d.toLocaleDateString([], opts)
})

// Mock stat tiles: packets heard · avg hops · audio level · first heard
const statTiles = computed(() => [
  { value: stats.value?.packetsAllTime.toLocaleString() ?? '—', label: 'packets heard' },
  { value: avgHops.value != null ? avgHops.value.toFixed(1) : '—', label: 'avg hops' },
  { value: avgAudio.value != null ? avgAudio.value.toFixed(2) : '—', label: 'audio level' },
  { value: firstHeardLabel.value, label: 'first heard' },
])

// Mock activity spark — packetsPerHour is oldest→newest, 24 buckets.
const sparkBars = computed(() => {
  const hours = stats.value?.packetsPerHour ?? []
  const max = Math.max(1, ...hours)
  return hours.map((count) => ({ count, pct: Math.max(2, (count / max) * 100) }))
})

const HEARD_VIA_LABELS: Record<HeardVia, string> = {
  [HeardVia.Unknown]: 'Unknown',
  [HeardVia.Direct]: 'Direct',
  [HeardVia.Digi]: 'Via digipeater',
  [HeardVia.DirectAndDigi]: 'Direct & digi',
  [HeardVia.Internet]: 'APRS-IS',
  [HeardVia.IgateRf]: 'IGate (RF)',
  [HeardVia.IgateRfDigi]: 'IGate RF + digi',
}

const heardViaLabel = computed(() =>
  station.value ? (HEARD_VIA_LABELS[station.value.heardVia] ?? 'Unknown') : null,
)

const courseLabel = computed(() => {
  const s = station.value
  if (!s || s.lastHeading == null) return null
  const heading = `${Math.round(s.lastHeading)}°`
  return s.lastSpeed != null ? `${heading} @ ${Math.round(s.lastSpeed)} mph` : heading
})

/** QRZ link uses the base callsign — QRZ has no SSID pages. */
const qrzUrl = computed(
  () => `https://www.qrz.com/db/${encodeURIComponent(callsign.value.split('-')[0] ?? '')}`,
)

async function loadStation() {
  try {
    station.value = await getStation(callsign.value)
  } catch {
    station.value = null
  }
}

// ─── Page tabs (mock order/style; drives the panel via v-model:tab) ──────────
type PageTab = 'info' | 'packets' | 'weather' | 'stats' | 'signal'
const pageTab = ref<PageTab>('info')
const packetCount = ref<number | null>(null)

const pageTabs = computed(() => [
  { value: 'info' as const, label: 'Info' },
  { value: 'packets' as const, label: 'Packets', count: packetCount.value },
  ...(station.value?.isWeatherStation ? [{ value: 'weather' as const, label: 'Weather' }] : []),
  { value: 'stats' as const, label: 'Stats' },
  { value: 'signal' as const, label: 'Signal' },
])

// ─── "How it reached us" — the latest packet's resolved RF path ──────────────
const latestPacket = ref<PacketDto | null>(null)

const pathHops = computed<ResolvedPathEntry[]>(() => {
  const path = latestPacket.value?.resolvedPath
  if (!path || path.length < 2) return []
  return path
})

function hopRole(hop: ResolvedPathEntry, index: number): string {
  if (index === 0) return 'Origin'
  if (index === pathHops.value.length - 1) return 'Heard by'
  return hop.aliasUsed ? `Digipeater · ${hop.aliasUsed}` : 'Digipeater'
}

function hopClass(hop: ResolvedPathEntry, index: number): string {
  if (index === 0) return ''
  if (index === pathHops.value.length - 1) return 'hop--us'
  return 'hop--digi'
}

const latestPacketSource = computed(() =>
  latestPacket.value?.source === PacketSource.AprsIs
    ? { label: 'IS', color: 'is' }
    : { label: 'RF', color: 'rf' },
)

async function loadLatestPacket() {
  try {
    const { items, totalCount } = await getStationPackets(callsign.value, 1, 1)
    latestPacket.value = items[0] ?? null
    packetCount.value = totalCount
  } catch {
    latestPacket.value = null
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
    void loadLatestPacket()
    void loadInfoData()
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

watch(
  callsign,
  () => {
    void loadStation()
    void loadLatestPacket()
    void loadInfoData()
  },
  { immediate: false },
)

onMounted(async () => {
  void loadStation()
  void loadLatestPacket()
  void loadInfoData()
  hub.on('packetReceived', onHubPacketReceived)
  try {
    home.value = (await getSettings()).homePosition
  } catch {
    home.value = null
  }
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

    <!-- Mock-style underline tabs, page-level -->
    <div class="station-tabs">
      <button
        v-for="t in pageTabs"
        :key="t.value"
        class="station-tab"
        :class="{ 'station-tab--active': pageTab === t.value }"
        role="tab"
        :aria-selected="pageTab === t.value"
        @click="pageTab = t.value"
      >
        {{ t.label }}
        <v-chip v-if="t.count != null" size="x-small" variant="tonal" class="ml-1">
          {{ t.count.toLocaleString() }}
        </v-chip>
      </button>
    </div>
    <v-divider />

    <!-- Tabbed content. Info is the mock's two-column layout, built here;
         the other tabs render the detail panel in page mode. -->
    <div class="station-page-body">
      <div v-if="pageTab === 'info'" class="info-grid">
        <div class="info-col">
          <!-- Mock headline: the latest packet's resolved RF path, hop by hop -->
          <v-card v-if="pathHops.length > 0" variant="outlined">
            <div class="d-flex align-center ga-2 card-head">
              <span class="card-title">Last packet — how it reached us</span>
              <v-chip size="x-small" variant="tonal" :color="latestPacketSource.color">
                {{ latestPacketSource.label }}
              </v-chip>
              <v-spacer />
              <span
                v-if="latestPacket"
                class="text-caption text-medium-emphasis"
                :title="formatUtc(latestPacket.receivedAt)"
              >
                {{ timeAgo(latestPacket.receivedAt, now) }}
              </span>
            </div>
            <div class="px-4 pb-3">
              <div class="path-viz">
                <template v-for="(hop, i) in pathHops" :key="`${hop.callsign}-${i}`">
                  <v-icon v-if="i > 0" size="16" class="path-arrow">mdi-arrow-right</v-icon>
                  <div class="hop" :class="hopClass(hop, i)">
                    <div class="hop-role">{{ hopRole(hop, i) }}</div>
                    <div class="hop-callsign">{{ hop.callsign }}</div>
                    <div v-if="hop.latitude != null && hop.longitude != null" class="hop-coord">
                      {{ hop.latitude.toFixed(4) }}, {{ hop.longitude.toFixed(4) }}
                    </div>
                    <div v-else class="hop-coord">position unknown</div>
                  </div>
                </template>
              </div>
              <div v-if="latestPacket" class="raw-line" :title="latestPacket.rawPacket">
                {{ latestPacket.rawPacket }}
              </div>
            </div>
          </v-card>

          <!-- Mock: hourly packet spark -->
          <v-card variant="outlined">
            <div class="d-flex align-center card-head">
              <span class="card-title">Activity — last 24h</span>
              <v-spacer />
              <span v-if="stats" class="text-caption text-medium-emphasis">
                {{ stats.packetsToday.toLocaleString() }} today
              </span>
            </div>
            <div class="px-4 pb-3 pt-3">
              <div class="spark" aria-hidden="true">
                <i
                  v-for="(bar, i) in sparkBars"
                  :key="i"
                  :style="{ height: `${bar.pct}%` }"
                  :title="`${bar.count} packets`"
                />
              </div>
              <div class="spark-axis"><span>−24h</span><span>−12h</span><span>now</span></div>
            </div>
          </v-card>
        </div>

        <div class="info-col">
          <!-- Mock: stat tiles -->
          <v-card variant="outlined" class="pa-3">
            <div class="stat-grid">
              <div v-for="tile in statTiles" :key="tile.label" class="stat-tile">
                <div class="stat-value">{{ tile.value }}</div>
                <div class="stat-label">{{ tile.label }}</div>
              </div>
            </div>
          </v-card>

          <!-- Mock: details kv -->
          <v-card variant="outlined">
            <div class="d-flex align-center card-head">
              <span class="card-title">Details</span>
            </div>
            <dl class="details-kv px-4 pb-3 pt-1">
              <dt>Symbol</dt>
              <dd>
                <span class="details-symbol" :style="symbolStyle" />
                <span class="ml-1">{{
                  STATION_TYPE_LABELS[station?.stationType ?? StationType.Unknown]
                }}</span>
              </dd>
              <template v-if="station?.gridSquare">
                <dt>Grid</dt>
                <dd class="mono">{{ station.gridSquare }}</dd>
              </template>
              <template v-if="heardViaLabel">
                <dt>Heard via</dt>
                <dd>{{ heardViaLabel }}</dd>
              </template>
              <template v-if="courseLabel">
                <dt>Last course</dt>
                <dd class="mono">{{ courseLabel }}</dd>
              </template>
              <template v-if="station?.lastAltitude != null">
                <dt>Altitude</dt>
                <dd class="mono">{{ Math.round(station.lastAltitude).toLocaleString() }} ft</dd>
              </template>
              <template v-if="station?.lastFrequencyMhz">
                <dt>Frequency</dt>
                <dd class="mono">
                  {{ station.lastFrequencyMhz }}{{ station.lastMode ? ` ${station.lastMode}` : '' }}
                </dd>
              </template>
              <template v-if="station?.status">
                <dt>Status</dt>
                <dd>{{ station.status }}</dd>
              </template>
              <dt>QRZ</dt>
              <dd>
                <a :href="qrzUrl" target="_blank" rel="noopener" class="qrz-link">look up ↗</a>
              </dd>
            </dl>
          </v-card>
        </div>
      </div>

      <StationDetailPanel
        v-if="pageTab !== 'info'"
        v-model:tab="pageTab"
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

.station-tabs {
  display: flex;
  gap: 4px;
  padding: 0 16px;
  overflow-x: auto;
  flex-shrink: 0;
}

.station-tab {
  display: inline-flex;
  align-items: center;
  padding: 10px 14px;
  font-size: 0.85rem;
  font-weight: 600;
  color: rgba(var(--v-theme-on-surface), 0.6);
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  cursor: pointer;
  white-space: nowrap;
}

.station-tab:hover {
  color: rgba(var(--v-theme-on-surface), 0.9);
}

.station-tab--active {
  color: rgb(var(--v-theme-primary));
  border-bottom-color: rgb(var(--v-theme-primary));
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

/* ── Info tab: mock two-column grid ── */
.info-grid {
  display: grid;
  grid-template-columns: 2fr 1fr;
  gap: 16px;
  align-items: start;
}

@media (max-width: 800px) {
  .info-grid {
    grid-template-columns: 1fr;
  }
}

.info-col {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
}

.card-head {
  padding: 12px 16px;
}

.card-title {
  font-size: 14px;
  font-weight: 650;
}

.spark {
  display: flex;
  align-items: flex-end;
  gap: 2px;
  height: 72px;
}

.spark i {
  flex: 1;
  min-width: 0;
  border-radius: 2px 2px 0 0;
  background: rgba(var(--v-theme-primary), 0.55);
}

.spark-axis {
  display: flex;
  justify-content: space-between;
  font-size: 10.5px;
  color: rgba(var(--v-theme-on-surface), 0.55);
  margin-top: 3px;
}

.stat-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}

.stat-tile {
  background: rgba(var(--v-theme-on-surface), 0.04);
  border-radius: 9px;
  padding: 10px 12px;
}

.stat-value {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 18px;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
}

.stat-label {
  font-size: 11.5px;
  color: rgba(var(--v-theme-on-surface), 0.55);
}

.details-kv {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 5px 14px;
  align-items: center;
  margin: 0;
  font-size: 12.5px;
}

.details-kv dt {
  color: rgba(var(--v-theme-on-surface), 0.55);
  white-space: nowrap;
}

.details-kv dd {
  margin: 0;
  text-align: right;
  min-width: 0;
  overflow-wrap: anywhere;
}

.details-kv .mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-variant-numeric: tabular-nums;
}

.details-symbol {
  display: inline-block;
  vertical-align: middle;
  image-rendering: pixelated;
}

.qrz-link {
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
}

.qrz-link:hover {
  text-decoration: underline;
}

/* ── Resolved-path hop chain ── */
.path-viz {
  display: flex;
  align-items: stretch;
  gap: 8px;
  overflow-x: auto;
  padding: 4px 0;
}

.path-arrow {
  align-self: center;
  color: rgba(var(--v-theme-on-surface), 0.4);
  flex-shrink: 0;
}

.hop {
  flex: 1 1 150px;
  min-width: 140px;
  background: rgba(var(--v-theme-on-surface), 0.04);
  border: 1px solid rgba(var(--v-theme-on-surface), 0.12);
  border-radius: 10px;
  padding: 8px 12px;
}

.hop--digi {
  border-color: rgba(var(--v-theme-rf), 0.6);
}

.hop--us {
  border-color: rgba(var(--v-theme-success), 0.6);
}

.hop-role {
  font-size: 0.65rem;
  text-transform: uppercase;
  letter-spacing: 0.07em;
  font-weight: 700;
  color: rgba(var(--v-theme-on-surface), 0.5);
}

.hop-callsign {
  font-weight: 650;
  font-variant-numeric: tabular-nums;
  margin: 2px 0;
}

.hop-coord {
  font-size: 0.7rem;
  color: rgba(var(--v-theme-on-surface), 0.55);
  font-variant-numeric: tabular-nums;
}

.raw-line {
  margin-top: 8px;
  font-family: monospace;
  font-size: 0.72rem;
  color: rgba(var(--v-theme-on-surface), 0.55);
  background: rgba(var(--v-theme-on-surface), 0.05);
  border-radius: 6px;
  padding: 6px 10px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
