<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr'
import {
  getModemStatus,
  decodeSpectrumPayload,
  ModemStates,
  modemStateLabels,
  type ModemLevelDto,
  type ModemStatusDto,
} from '@/api/modemApi'
import { getStatus, type StatusDto } from '@/api/statusApi'
import { getSettings } from '@/api/stationsApi'
import { getRadios } from '@/api/radiosApi'
import type { RadioDto } from '@/types/radio'
import type { SettingsDto } from '@/types/station'
import {
  PACKET_TYPE_LABELS,
  PACKET_TYPE_COLORS,
  parsedTypeFromString,
  PacketSource,
  type PacketBroadcastDto,
} from '@/types/packet'
import WaterfallCanvas from '@/components/WaterfallCanvas.vue'

const MAX_FEED = 100

const status = ref<StatusDto | null>(null)
const modemStatus = ref<ModemStatusDto | null>(null)
const settings = ref<SettingsDto | null>(null)
const radios = ref<RadioDto[]>([])
const packets = ref<PacketBroadcastDto[]>([])

// Live meters — updated at 10 Hz over SignalR.
const audioLevel = ref(0)
const carrierDetected = ref(false)
const transmitting = ref(false)

const waterfall = ref<InstanceType<typeof WaterfallCanvas> | null>(null)
let connection: HubConnection | null = null
let statusTimer: ReturnType<typeof setInterval> | null = null

const modemRunning = computed(() => modemStatus.value?.state === ModemStates.Running)

const modemStateColor = computed(() => {
  switch (modemStatus.value?.state) {
    case ModemStates.Running:
      return 'green'
    case ModemStates.Error:
      return 'error'
    default:
      return 'grey'
  }
})

const rigFrequencyMhz = computed(() => {
  const hz = modemStatus.value?.rigFrequencyHz
  return hz ? (hz / 1_000_000).toFixed(4) : null
})

const audioLevelColor = computed(() => {
  if (audioLevel.value > 0.9) return 'error'
  if (audioLevel.value > 0.05) return 'green'
  return 'grey'
})

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
    modemStatus.value = m
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

  statusTimer = setInterval(refresh, 5000)

  connection = new HubConnectionBuilder().withUrl('/hubs/packets').withAutomaticReconnect().build()

  connection.on('modemLevel', (level: ModemLevelDto) => {
    audioLevel.value = level.audioLevel
    carrierDetected.value = level.carrierDetected
    transmitting.value = level.transmitting
  })

  connection.on('modemSpectrum', (bins: number[] | string) => {
    waterfall.value?.drawRow(decodeSpectrumPayload(bins))
  })

  connection.on('modemStatusChanged', (s: ModemStatusDto) => {
    modemStatus.value = s
  })

  connection.on('packetReceived', (p: PacketBroadcastDto) => {
    packets.value.unshift(p)
    if (packets.value.length > MAX_FEED) packets.value.splice(MAX_FEED)
  })

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
        <!-- Sound modem -->
        <v-card variant="outlined" class="mb-4 pa-4">
          <div class="d-flex align-center mb-2">
            <span class="text-subtitle-1 font-weight-medium">Sound Modem</span>
            <v-chip :color="modemStateColor" size="x-small" variant="tonal" class="ml-2">
              {{ modemStatus ? (modemStateLabels[modemStatus.state] ?? 'Unknown') : '…' }}
            </v-chip>
            <v-spacer />
            <span v-if="modemStatus?.captureDevice" class="text-caption text-medium-emphasis">
              {{ modemStatus.captureDevice }}
            </span>
          </div>

          <template v-if="modemRunning">
            <!-- Live meters -->
            <div class="d-flex align-center ga-3 mb-3">
              <v-chip :color="carrierDetected ? 'green' : 'grey'" size="small" variant="tonal">
                <v-icon start size="14">mdi-arrow-down-bold</v-icon>RX
              </v-chip>
              <v-chip
                v-if="modemStatus?.txEnabled"
                :color="transmitting ? 'red' : 'grey'"
                size="small"
                variant="tonal"
              >
                <v-icon start size="14">mdi-arrow-up-bold</v-icon>TX
              </v-chip>
              <span v-if="rigFrequencyMhz" class="text-body-2 font-weight-medium">
                {{ rigFrequencyMhz }} MHz
              </span>
            </div>

            <div class="d-flex align-center ga-2 mb-3">
              <span class="text-caption text-medium-emphasis" style="width: 44px">Audio</span>
              <v-progress-linear
                :model-value="Math.min(100, audioLevel * 100)"
                :color="audioLevelColor"
                height="14"
                rounded
              />
              <span
                class="text-caption text-medium-emphasis"
                style="width: 40px; text-align: right"
              >
                {{ (audioLevel * 100).toFixed(0) }}%
              </span>
            </div>

            <WaterfallCanvas ref="waterfall" :height="120" class="mb-2" />

            <div class="text-caption text-medium-emphasis">
              {{ modemStatus!.decodedFrames.toLocaleString() }} decoded ·
              {{ modemStatus!.invalidFrames.toLocaleString() }} bad CRC<template
                v-if="modemStatus!.txEnabled"
              >
                · {{ modemStatus!.transmittedFrames.toLocaleString() }} sent</template
              >
            </div>
            <div
              v-if="
                modemStatus?.decodedByProfile &&
                Object.keys(modemStatus.decodedByProfile).length > 1
              "
              class="text-caption text-medium-emphasis"
            >
              Profiles:
              <span v-for="(count, name) in modemStatus.decodedByProfile" :key="name" class="mr-2">
                {{ name }}: {{ count.toLocaleString() }}
              </span>
            </div>
          </template>
          <v-alert
            v-else-if="modemStatus?.state === ModemStates.Error && modemStatus.errorMessage"
            type="error"
            density="compact"
          >
            {{ modemStatus.errorMessage }}
          </v-alert>
          <div v-else class="text-caption text-medium-emphasis">
            Enable the sound modem in Settings to decode RF here.
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

        <!-- Radios -->
        <v-card variant="outlined" class="pa-4">
          <div class="text-subtitle-1 font-weight-medium mb-2">Radios</div>
          <div v-if="radios.length === 0" class="text-caption text-medium-emphasis">
            No radios configured
          </div>
          <div v-for="radio in radios" :key="radio.id" class="d-flex align-center ga-2 mb-1">
            <v-chip :color="radio.isActive ? 'green' : 'grey'" size="x-small" variant="tonal">
              {{ radio.fullCallsign }}
            </v-chip>
            <span class="text-body-2">{{ radio.name }}</span>
            <span class="text-caption text-medium-emphasis">ch {{ radio.channelNumber }}</span>
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
