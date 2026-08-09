<script setup lang="ts">
import { ref, reactive, computed, onMounted, onUnmounted } from 'vue'

// Named so <keep-alive include="RadioView"> in App.vue keeps this view (and its
// live SignalR connection, packet buffer, and waterfall history) mounted while
// the user navigates elsewhere.
defineOptions({ name: 'RadioView' })
import {
  getModemStatus,
  setModemTxLevel,
  sendTestTone,
  restartModem,
  TestToneKinds,
  decodeSpectrumPayload,
  ModemStates,
  modemStateLabels,
  type ModemLevelDto,
  type ModemSpectrumDto,
  type ModemStatusDto,
  type TestToneKind,
} from '@/api/modemApi'
import { usePacketHubStore } from '@/stores/packetHub'
import { useToastStore } from '@/stores/toastStore'
import { getStatus, type StatusDto } from '@/api/statusApi'
import { getSettings, getPacketsSince } from '@/api/stationsApi'
import { getRadios } from '@/api/radiosApi'
import type { RadioDto } from '@/types/radio'
import type { SettingsDto } from '@/types/station'
import {
  PACKET_TYPE_LABELS,
  PACKET_TYPE_COLORS,
  parsedTypeFromString,
  packetDtoToBroadcast,
  PacketSource,
  type PacketBroadcastDto,
} from '@/types/packet'
import WaterfallCanvas from '@/components/WaterfallCanvas.vue'
import { timeAgo, formatUtc } from '@/utils/time'
import { serverNow } from '@/utils/serverTime'
import { useTick } from '@/composables/useTick'

const MAX_FEED = 100
const { now } = useTick(5000)
const hub = usePacketHubStore()
const toastStore = useToastStore()

const status = ref<StatusDto | null>(null)
const modemStatuses = ref<ModemStatusDto[]>([])
const settings = ref<SettingsDto | null>(null)
const radios = ref<RadioDto[]>([])
const packets = ref<PacketBroadcastDto[]>([])

// Live meters per radio — updated at 10 Hz over SignalR, keyed by radio id.
const levels = ref<Record<string, ModemLevelDto>>({})

// TX audio level (gain) per radio, adjustable live. Seeded from each radio's
// persisted config; writes are debounced so dragging the slider doesn't flood
// the API, and applied live by the backend with no modem restart.
const txGain = reactive<Record<string, number>>({})
const txGainTimers: Record<string, ReturnType<typeof setTimeout>> = {}

function seedTxGain() {
  for (const r of radios.value) txGain[r.id] = r.modem.txAudioLevelPct
}

function onTxGainInput(radioId: string, value: number) {
  txGain[radioId] = value
  clearTimeout(txGainTimers[radioId])
  txGainTimers[radioId] = setTimeout(() => {
    setModemTxLevel(radioId, Math.round(value)).catch(() => {
      /* transient — the next adjustment retries */
    })
  }, 200)
}

// Which radio is currently sending a test tone (disables its buttons briefly).
const toneSending = reactive<Record<string, boolean>>({})
const TEST_TONE_MS = 2000

async function doTestTone(radioId: string, kind: TestToneKind) {
  toneSending[radioId] = true
  try {
    await sendTestTone(radioId, kind, TEST_TONE_MS)
  } catch {
    /* surfaced by modem status / logs */
  } finally {
    // Re-enable after roughly the tone duration so the buttons reflect PTT.
    setTimeout(() => {
      toneSending[radioId] = false
    }, TEST_TONE_MS)
  }
}

type WaterfallInstance = InstanceType<typeof WaterfallCanvas>
const waterfalls = new Map<string, WaterfallInstance>()

function setWaterfallRef(radioId: string, el: unknown) {
  if (el) waterfalls.set(radioId, el as WaterfallInstance)
  else waterfalls.delete(radioId)
}

let statusTimer: ReturnType<typeof setInterval> | null = null

// ── Modem restart ─────────────────────────────────────────────────────────────
const modemRestarting = ref(false)

async function doRestartModem() {
  modemRestarting.value = true
  try {
    await restartModem()
    toastStore.toast('Modem restarting — audio devices are being re-opened…', 'info')
    // Give the backend a moment, then refresh so the state chip catches up.
    setTimeout(refresh, 1500)
  } catch {
    toastStore.toast('Modem restart failed — the backend may be unreachable.', 'error')
  } finally {
    modemRestarting.value = false
  }
}

// ── Shared hub handlers (named so they can be unregistered on unmount) ───────
function onHubModemLevel(batch: ModemLevelDto[]) {
  for (const level of batch) levels.value[level.radioId] = level
}

function onHubModemSpectrum(batch: ModemSpectrumDto[]) {
  for (const spectrum of batch)
    waterfalls.get(spectrum.radioId)?.drawRow(decodeSpectrumPayload(spectrum.bins))
}

function onHubModemStatusChanged(statuses: ModemStatusDto[]) {
  modemStatuses.value = statuses
}

function onHubPacketReceived(p: PacketBroadcastDto) {
  packets.value.unshift(p)
  if (packets.value.length > MAX_FEED) packets.value.splice(MAX_FEED)
}

function onHubPacketSourceUpgraded(upgrade: { id: number; source: PacketBroadcastDto['source'] }) {
  const entry = packets.value.find((p) => p.id === upgrade.id)
  if (entry) entry.source = upgrade.source
}

/** Radios without a modem instance — shown in the plain radio list. */
const radiosWithoutModem = computed(() =>
  radios.value.filter((r) => !modemStatuses.value.some((m) => m.radioId === r.id)),
)

function stateColor(m: ModemStatusDto): string {
  switch (m.state) {
    case ModemStates.Running:
      return 'green'
    case ModemStates.Error:
      return 'error'
    default:
      return 'grey'
  }
}

function radioFor(m: ModemStatusDto): RadioDto | undefined {
  return radios.value.find((r) => r.id === m.radioId)
}

/** Rigctld live frequency wins; manual radio frequency is the fallback. */
function frequencyLabel(m: ModemStatusDto): string | null {
  if (m.rigFrequencyHz) return `${(m.rigFrequencyHz / 1_000_000).toFixed(4)} MHz`
  const radio = radioFor(m)
  if (radio?.frequencyMhz) {
    const mode = radio.mode ? ` ${radio.mode}` : ''
    return `${radio.frequencyMhz.toFixed(3)} MHz${mode}`
  }
  return null
}

function levelFor(m: ModemStatusDto): ModemLevelDto {
  return (
    levels.value[m.radioId] ?? {
      radioId: m.radioId,
      channel: m.channel,
      audioLevel: m.audioLevel,
      carrierDetected: m.carrierDetected,
      transmitting: m.transmitting,
    }
  )
}

function levelColor(level: number): string {
  if (level > 0.9) return 'error'
  if (level > 0.05) return 'green'
  return 'grey'
}

function typeLabel(p: PacketBroadcastDto): string {
  return PACKET_TYPE_LABELS[parsedTypeFromString(p.parsedType)] ?? 'Unknown'
}

function typeColor(p: PacketBroadcastDto): string {
  return PACKET_TYPE_COLORS[parsedTypeFromString(p.parsedType)] ?? 'grey'
}

function sourceLabel(p: PacketBroadcastDto): string {
  return p.source === PacketSource.AprsIs ? 'IS' : 'RF'
}

// Staleness tracking: the page keeps showing last-known data on failure, but
// says so instead of silently presenting stale numbers as live.
const lastRefreshAt = ref<number | null>(null)
const refreshFailed = ref(false)
const radiosLoadFailed = ref(false)

const staleLabel = computed(() => {
  if (lastRefreshAt.value === null) return 'never loaded'
  return `data from ${timeAgo(new Date(lastRefreshAt.value).toISOString(), now.value)}`
})

async function refresh() {
  try {
    const [s, m] = await Promise.all([getStatus(), getModemStatus()])
    status.value = s
    modemStatuses.value = m
    lastRefreshAt.value = serverNow()
    refreshFailed.value = false
  } catch {
    refreshFailed.value = true
  }
}

onMounted(async () => {
  await refresh()
  try {
    settings.value = await getSettings()
  } catch {
    /* ignore */
  }
  try {
    radios.value = await getRadios()
    seedTxGain()
    radiosLoadFailed.value = false
  } catch {
    radiosLoadFailed.value = true
  }
  try {
    // Seed the feed so the page isn't blank until the next live packet arrives.
    const since = new Date(Date.now() - 60 * 60 * 1000).toISOString()
    const recent = await getPacketsSince(since, MAX_FEED)
    packets.value = recent.map(packetDtoToBroadcast)
  } catch {
    /* ignore — feed fills from live packets */
  }

  statusTimer = setInterval(refresh, 5000)

  hub.on('modemLevel', onHubModemLevel)
  hub.on('modemSpectrum', onHubModemSpectrum)
  hub.on('modemStatusChanged', onHubModemStatusChanged)
  hub.on('packetReceived', onHubPacketReceived)
  hub.on('packetSourceUpgraded', onHubPacketSourceUpgraded)
})

onUnmounted(() => {
  if (statusTimer) clearInterval(statusTimer)
  for (const t of Object.values(txGainTimers)) clearTimeout(t)
  hub.off('modemLevel', onHubModemLevel)
  hub.off('modemSpectrum', onHubModemSpectrum)
  hub.off('modemStatusChanged', onHubModemStatusChanged)
  hub.off('packetReceived', onHubPacketReceived)
  hub.off('packetSourceUpgraded', onHubPacketSourceUpgraded)
})
</script>

<template>
  <div class="radio-view pa-4">
    <div class="d-flex align-center ga-3 mb-4 flex-wrap">
      <span class="text-h5 font-weight-bold">Radio</span>
      <v-chip v-if="refreshFailed" color="warning" size="small" variant="tonal">
        <v-icon start size="14">mdi-lan-disconnect</v-icon>
        Backend unreachable · {{ staleLabel }}
      </v-chip>
    </div>

    <div class="radio-layout">
      <!-- ── RF stack column ── -->
      <div class="stack-column">
        <!-- One card per radio with a modem instance -->
        <v-card v-for="m in modemStatuses" :key="m.radioId" variant="outlined" class="mb-4 pa-4">
          <div class="d-flex align-center mb-2 flex-wrap ga-1">
            <span class="text-subtitle-1 font-weight-medium">{{ m.radioName }}</span>
            <span class="text-caption text-medium-emphasis">{{ m.fullCallsign }}</span>
            <span class="text-caption text-medium-emphasis">· ch {{ m.channel }}</span>
            <v-chip :color="stateColor(m)" size="x-small" variant="tonal" class="ml-1">
              {{ modemStateLabels[m.state] ?? 'Unknown' }}
            </v-chip>
            <v-spacer />
            <span v-if="m.captureDevice" class="text-caption text-medium-emphasis">
              {{ m.captureDevice }}
            </span>
          </div>

          <template v-if="m.state === ModemStates.Running">
            <div class="d-flex align-center ga-3 mb-3">
              <v-chip
                :color="levelFor(m).carrierDetected ? 'green' : 'grey'"
                size="small"
                variant="tonal"
              >
                <v-icon start size="14">mdi-arrow-down-bold</v-icon>RX
              </v-chip>
              <v-chip
                v-if="m.txEnabled"
                :color="levelFor(m).transmitting ? 'red' : 'grey'"
                size="small"
                variant="tonal"
              >
                <v-icon start size="14">mdi-arrow-up-bold</v-icon>TX
              </v-chip>
              <span v-if="frequencyLabel(m)" class="text-body-2 font-weight-medium">
                {{ frequencyLabel(m) }}
              </span>
            </div>

            <div class="d-flex align-center ga-2 mb-2 flex-nowrap">
              <span class="text-caption text-medium-emphasis flex-shrink-0" style="width: 44px"
                >RX</span
              >
              <v-progress-linear
                :model-value="Math.min(100, levelFor(m).audioLevel * 100)"
                :color="levelColor(levelFor(m).audioLevel)"
                height="14"
                rounded
              />
              <span
                class="text-caption text-medium-emphasis flex-shrink-0"
                style="width: 48px; text-align: right; white-space: nowrap"
              >
                {{ (levelFor(m).audioLevel * 100).toFixed(0) }}%
              </span>
            </div>

            <div v-if="m.txEnabled" class="d-flex align-center ga-2 mb-3 flex-nowrap">
              <span class="text-caption text-medium-emphasis flex-shrink-0" style="width: 44px"
                >TX gain</span
              >
              <v-slider
                :model-value="txGain[m.radioId] ?? 80"
                :min="1"
                :max="100"
                :step="1"
                color="primary"
                density="compact"
                hide-details
                thumb-label
                @update:model-value="(v: number) => onTxGainInput(m.radioId, v)"
              />
              <span
                class="text-caption text-medium-emphasis flex-shrink-0"
                style="width: 48px; text-align: right; white-space: nowrap"
              >
                {{ Math.round(txGain[m.radioId] ?? 80) }}%
              </span>
            </div>

            <div v-if="m.txEnabled" class="d-flex align-center ga-2 mb-3 flex-wrap">
              <span class="text-caption text-medium-emphasis flex-shrink-0" style="width: 44px"
                >Test</span
              >
              <v-btn-group density="compact" variant="outlined" divided>
                <v-btn
                  size="x-small"
                  :loading="toneSending[m.radioId]"
                  @click="doTestTone(m.radioId, TestToneKinds.Mark)"
                >
                  Mark
                </v-btn>
                <v-btn
                  size="x-small"
                  :disabled="toneSending[m.radioId]"
                  @click="doTestTone(m.radioId, TestToneKinds.Space)"
                >
                  Space
                </v-btn>
                <v-btn
                  size="x-small"
                  :disabled="toneSending[m.radioId]"
                  @click="doTestTone(m.radioId, TestToneKinds.Alternating)"
                >
                  Alt
                </v-btn>
              </v-btn-group>
              <span class="text-caption text-medium-emphasis">keys TX ~2s</span>
            </div>

            <WaterfallCanvas
              :ref="(el) => setWaterfallRef(m.radioId, el)"
              :height="100"
              class="mb-2"
            />

            <div class="d-flex align-center ga-2 flex-wrap">
              <span class="text-caption text-medium-emphasis">
                {{ m.decodedFrames.toLocaleString() }} decoded ·
                {{ m.invalidFrames.toLocaleString() }} bad CRC<template v-if="m.txEnabled">
                  · {{ m.transmittedFrames.toLocaleString() }} sent</template
                >
              </span>
              <v-spacer />
              <v-btn
                size="x-small"
                variant="text"
                prepend-icon="mdi-restart"
                :loading="modemRestarting"
                title="Tear down and re-open the modem audio devices"
                @click="doRestartModem"
              >
                Restart
              </v-btn>
            </div>
          </template>
          <v-alert
            v-else-if="m.state === ModemStates.Error && m.errorMessage"
            type="error"
            density="compact"
          >
            <div class="d-flex align-center ga-3 flex-wrap">
              <span>{{ m.errorMessage }}</span>
              <v-btn
                size="small"
                variant="tonal"
                color="error"
                prepend-icon="mdi-restart"
                :loading="modemRestarting"
                @click="doRestartModem"
              >
                Restart modem
              </v-btn>
            </div>
          </v-alert>
        </v-card>

        <v-card v-if="modemStatuses.length === 0" variant="outlined" class="mb-4 pa-4">
          <div class="text-subtitle-1 font-weight-medium mb-1">Sound Modem</div>
          <template v-if="refreshFailed || radiosLoadFailed">
            <div class="text-caption text-medium-emphasis mb-2">
              Couldn't reach the backend — modem status is unknown.
            </div>
            <v-btn size="small" color="primary" variant="tonal" @click="refresh">Retry</v-btn>
          </template>
          <div v-else class="text-caption text-medium-emphasis">
            No radios have a modem audio feed configured — enable one in a radio's settings.
          </div>
        </v-card>

        <!-- Other RF stack services -->
        <v-card variant="outlined" class="mb-4 pa-4">
          <div class="text-subtitle-1 font-weight-medium mb-2">RF Stack</div>
          <div class="stack-grid">
            <span class="text-body-2">External TNC (KISS)</span>
            <v-chip
              :color="status?.direwolfConnected ? 'green' : 'grey'"
              size="x-small"
              variant="tonal"
            >
              {{ status?.direwolfConnected ? 'Connected' : 'Disconnected' }}
            </v-chip>

            <span class="text-body-2">Digipeater</span>
            <span class="text-caption text-medium-emphasis">
              <template v-if="settings?.digipeaterEnabled">
                On · {{ status?.digipeatedFrames?.toLocaleString() ?? 0 }} repeated
              </template>
              <template v-else>Off</template>
            </span>

            <span class="text-body-2">KISS Server</span>
            <span class="text-caption text-medium-emphasis">
              <template v-if="settings?.kissServerEnabled">
                Port {{ settings.kissServerPort }} · {{ status?.kissServerClients ?? 0 }} client{{
                  (status?.kissServerClients ?? 0) === 1 ? '' : 's'
                }}
              </template>
              <template v-else>Off</template>
            </span>

            <span class="text-body-2">APRS-IS</span>
            <span class="text-caption text-medium-emphasis">
              {{ status?.aprsIsState ?? '…' }}
              <template v-if="status?.aprsIsServerName"> · {{ status.aprsIsServerName }}</template>
            </span>

            <span class="text-body-2">iGate</span>
            <span class="text-caption text-medium-emphasis">
              <template v-if="settings?.rfToIsGatingEnabled || settings?.isToRfGatingEnabled">
                <template v-if="settings?.rfToIsGatingEnabled">
                  RF→IS ({{ status?.rfToIsGatedLines?.toLocaleString() ?? 0 }} gated)
                </template>
                <template v-if="settings?.rfToIsGatingEnabled && settings?.isToRfGatingEnabled">
                  ·
                </template>
                <template v-if="settings?.isToRfGatingEnabled">IS→RF</template>
              </template>
              <template v-else>Off</template>
            </span>
          </div>
        </v-card>

        <!-- Radios without a modem feed -->
        <v-card v-if="radiosWithoutModem.length > 0" variant="outlined" class="pa-4">
          <div class="text-subtitle-1 font-weight-medium mb-2">Other Radios</div>
          <div
            v-for="radio in radiosWithoutModem"
            :key="radio.id"
            class="d-flex align-center ga-2 mb-1 flex-wrap"
          >
            <v-chip :color="radio.isActive ? 'green' : 'grey'" size="x-small" variant="tonal">
              {{ radio.fullCallsign }}
            </v-chip>
            <span class="text-body-2">{{ radio.name }}</span>
            <span class="text-caption text-medium-emphasis">ch {{ radio.channelNumber }}</span>
            <span v-if="radio.frequencyMhz" class="text-caption text-medium-emphasis">
              · {{ radio.frequencyMhz.toFixed(3) }} MHz{{ radio.mode ? ` ${radio.mode}` : '' }}
            </span>
          </div>
        </v-card>
      </div>

      <!-- ── Live packet feed ── -->
      <v-card variant="outlined" class="feed-column pa-0">
        <div class="pa-3 pb-2 text-subtitle-1 font-weight-medium">Incoming Packets</div>
        <v-divider />
        <div class="feed-scroll">
          <div v-if="packets.length === 0" class="text-caption text-medium-emphasis pa-4">
            Waiting for packets…
          </div>
          <div v-for="p in packets" :key="p.id" class="feed-row">
            <span
              class="text-caption text-medium-emphasis feed-time"
              :title="formatUtc(p.receivedAt)"
              >{{ timeAgo(p.receivedAt, now) }}</span
            >
            <v-chip size="x-small" variant="tonal" :color="sourceLabel(p) === 'RF' ? 'rf' : 'is'">
              {{ sourceLabel(p) }}
            </v-chip>
            <span class="text-body-2 font-weight-medium">{{ p.callsign }}</span>
            <v-chip size="x-small" label :color="typeColor(p)">{{ typeLabel(p) }}</v-chip>
            <span class="text-caption text-medium-emphasis feed-summary">{{ p.summary }}</span>
          </div>
        </div>
      </v-card>
    </div>
  </div>
</template>

<style scoped>
.radio-view {
  height: 100%;
  overflow-y: auto;
}

.radio-layout {
  display: flex;
  gap: 16px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.stack-column {
  flex: 0 1 420px;
  min-width: 320px;
}

.feed-column {
  flex: 1 1 480px;
  min-width: 320px;
  display: flex;
  flex-direction: column;
  max-height: calc(100vh - 140px);
}

.feed-scroll {
  overflow-y: auto;
  flex: 1;
}

.feed-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 12px;
  border-bottom: 1px solid rgba(128, 128, 128, 0.15);
}

.feed-time {
  width: 76px;
  flex-shrink: 0;
}

.feed-summary {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.stack-grid {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 6px 16px;
  align-items: center;
}
</style>
