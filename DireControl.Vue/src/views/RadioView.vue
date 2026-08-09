<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr'
import {
  getModemStatus,
  decodeSpectrumPayload,
  ModemStates,
  modemStateLabels,
  type ModemLevelDto,
  type ModemSpectrumDto,
  type ModemStatusDto,
} from '@/api/modemApi'
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

const MAX_FEED = 100

const status = ref<StatusDto | null>(null)
const modemStatuses = ref<ModemStatusDto[]>([])
const settings = ref<SettingsDto | null>(null)
const radios = ref<RadioDto[]>([])
const packets = ref<PacketBroadcastDto[]>([])

// Live meters per radio — updated at 10 Hz over SignalR, keyed by radio id.
const levels = ref<Record<string, ModemLevelDto>>({})

type WaterfallInstance = InstanceType<typeof WaterfallCanvas>
const waterfalls = new Map<string, WaterfallInstance>()

function setWaterfallRef(radioId: string, el: unknown) {
  if (el) waterfalls.set(radioId, el as WaterfallInstance)
  else waterfalls.delete(radioId)
}

let connection: HubConnection | null = null
let statusTimer: ReturnType<typeof setInterval> | null = null

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

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString()
}

async function refresh() {
  try {
    const [s, m] = await Promise.all([getStatus(), getModemStatus()])
    status.value = s
    modemStatuses.value = m
  } catch {
    /* ignore — page shows last known state */
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
  } catch {
    /* ignore */
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

  connection = new HubConnectionBuilder().withUrl('/hubs/packets').withAutomaticReconnect().build()

  connection.on('modemLevel', (batch: ModemLevelDto[]) => {
    for (const level of batch) levels.value[level.radioId] = level
  })

  connection.on('modemSpectrum', (batch: ModemSpectrumDto[]) => {
    for (const spectrum of batch)
      waterfalls.get(spectrum.radioId)?.drawRow(decodeSpectrumPayload(spectrum.bins))
  })

  connection.on('modemStatusChanged', (statuses: ModemStatusDto[]) => {
    modemStatuses.value = statuses
  })

  connection.on('packetReceived', (p: PacketBroadcastDto) => {
    packets.value.unshift(p)
    if (packets.value.length > MAX_FEED) packets.value.splice(MAX_FEED)
  })

  connection.on(
    'packetSourceUpgraded',
    (upgrade: { id: number; source: PacketBroadcastDto['source'] }) => {
      const entry = packets.value.find((p) => p.id === upgrade.id)
      if (entry) entry.source = upgrade.source
    },
  )

  try {
    await connection.start()
  } catch {
    /* automatic reconnect keeps trying */
  }
})

onUnmounted(() => {
  if (statusTimer) clearInterval(statusTimer)
  connection?.stop()
  connection = null
})
</script>

<template>
  <div class="radio-view pa-4">
    <div class="text-h5 font-weight-bold mb-4">Radio</div>

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

            <div class="d-flex align-center ga-2 mb-3 flex-nowrap">
              <span class="text-caption text-medium-emphasis flex-shrink-0" style="width: 44px"
                >Audio</span
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

            <WaterfallCanvas
              :ref="(el) => setWaterfallRef(m.radioId, el)"
              :height="100"
              class="mb-2"
            />

            <div class="text-caption text-medium-emphasis">
              {{ m.decodedFrames.toLocaleString() }} decoded ·
              {{ m.invalidFrames.toLocaleString() }} bad CRC<template v-if="m.txEnabled">
                · {{ m.transmittedFrames.toLocaleString() }} sent</template
              >
            </div>
          </template>
          <v-alert
            v-else-if="m.state === ModemStates.Error && m.errorMessage"
            type="error"
            density="compact"
          >
            {{ m.errorMessage }}
          </v-alert>
        </v-card>

        <v-card v-if="modemStatuses.length === 0" variant="outlined" class="mb-4 pa-4">
          <div class="text-subtitle-1 font-weight-medium mb-1">Sound Modem</div>
          <div class="text-caption text-medium-emphasis">
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
            <span class="text-caption text-medium-emphasis feed-time">{{
              formatTime(p.receivedAt)
            }}</span>
            <v-chip
              size="x-small"
              variant="tonal"
              :color="sourceLabel(p) === 'RF' ? 'green' : 'blue'"
            >
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
