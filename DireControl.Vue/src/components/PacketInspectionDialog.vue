<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import { getPacket } from '@/api/stationsApi'
import {
  PacketType,
  PACKET_TYPE_LABELS,
  PACKET_TYPE_COLORS,
  type PacketDto,
  type ResolvedPathEntry,
} from '@/types/packet'
import { compassDir16 } from '@/utils/time'

const props = defineProps<{
  packetId: number | null
}>()

const emit = defineEmits<{
  close: []
  selectStation: [callsign: string]
}>()

const packet = ref<PacketDto | null>(null)
const loading = ref(false)
const showRaw = ref(false)
const copyFeedback = ref(false)

watch(
  () => props.packetId,
  async (id) => {
    showRaw.value = false
    if (id == null) {
      packet.value = null
      return
    }
    loading.value = true
    try {
      packet.value = await getPacket(id)
    } catch {
      packet.value = null
    } finally {
      loading.value = false
    }
  },
  { immediate: true },
)

function onClose() {
  emit('close')
}

async function copyRaw() {
  if (!packet.value) return
  try {
    await navigator.clipboard.writeText(packet.value.rawPacket)
    copyFeedback.value = true
    setTimeout(() => {
      copyFeedback.value = false
    }, 1500)
  } catch {
    // clipboard not available
  }
}

function onHopClick(callsign: string) {
  emit('selectStation', callsign)
  emit('close')
}

function toDMS(deg: number, posDir: string, negDir: string): string {
  const abs = Math.abs(deg)
  const d = Math.floor(abs)
  const mFull = (abs - d) * 60
  const m = Math.floor(mFull)
  const s = ((mFull - m) * 60).toFixed(1)
  const dir = deg >= 0 ? posDir : negDir
  return `${d}° ${m}′ ${s}″ ${dir}`
}

function fToC(f: number): number {
  return Math.round((f - 32) * (5 / 9) * 10) / 10
}

function mphToKmh(mph: number): number {
  return Math.round(mph * 1.60934 * 10) / 10
}

const typeLabel = computed(() =>
  packet.value ? (PACKET_TYPE_LABELS[packet.value.parsedType as PacketType] ?? 'Unknown') : '',
)

const typeColor = computed(() =>
  packet.value ? (PACKET_TYPE_COLORS[packet.value.parsedType as PacketType] ?? 'grey') : 'grey',
)

const orderedPath = computed((): ResolvedPathEntry[] =>
  packet.value ? [...packet.value.resolvedPath].sort((a, b) => a.hopIndex - b.hopIndex) : [],
)

const isWeather = computed(() => packet.value?.parsedType === PacketType.Weather)
const isMessage = computed(() => packet.value?.parsedType === PacketType.Message)
const isTelemetry = computed(() => packet.value?.parsedType === PacketType.Telemetry)

const isOpen = computed(() => props.packetId != null)

function formatIso(iso: string): string {
  return new Date(iso).toISOString().replace('T', ' ').replace('.000Z', 'Z')
}

function heardViaLabel(p: PacketDto): string {
  if (p.isDirectHeard) return 'Direct'
  if (p.hopCount > 0) return 'Digi'
  return '—'
}
</script>

<template>
  <v-dialog
    :model-value="isOpen"
    max-width="680"
    scrollable
    @update:model-value="if (!$event) onClose()"
    @keydown.esc="onClose()"
  >
    <v-card style="max-height: 80vh; display: flex; flex-direction: column;">
      <!-- Header -->
      <div class="dialog-header">
        <div class="d-flex align-center ga-2 flex-wrap">
          <span class="text-h6 font-weight-bold">{{ packet?.stationCallsign ?? '…' }}</span>
          <v-chip v-if="packet" :color="typeColor" size="x-small" label>{{ typeLabel }}</v-chip>
          <span v-if="packet" class="text-caption text-medium-emphasis">
            {{ formatIso(packet.receivedAt) }}
          </span>
        </div>
        <div class="d-flex align-center ga-1">
          <v-btn
            size="x-small"
            variant="tonal"
            :color="copyFeedback ? 'success' : 'default'"
            :disabled="!packet"
            @click="copyRaw"
          >
            {{ copyFeedback ? 'Copied ✓' : 'Copy Raw' }}
          </v-btn>
          <v-btn icon="mdi-close" size="small" variant="text" @click="onClose" />
        </div>
      </div>

      <v-divider />

      <!-- Body -->
      <v-card-text class="pa-0" style="overflow-y: auto; flex: 1;">
        <v-progress-linear v-if="loading" indeterminate color="primary" />

        <template v-if="packet && !loading">

          <!-- ── Position ── -->
          <template v-if="packet.latitude != null || packet.gridSquare">
            <div class="section-label">Position</div>
            <div class="field-grid">
              <template v-if="packet.latitude != null">
                <div class="field-key">Latitude</div>
                <div class="field-val">
                  {{ packet.latitude.toFixed(6) }}°
                  <span class="text-medium-emphasis ml-1">{{ toDMS(packet.latitude, 'N', 'S') }}</span>
                </div>
                <div class="field-key">Longitude</div>
                <div class="field-val">
                  {{ packet.longitude!.toFixed(6) }}°
                  <span class="text-medium-emphasis ml-1">{{ toDMS(packet.longitude!, 'E', 'W') }}</span>
                </div>
              </template>
              <template v-if="packet.gridSquare">
                <div class="field-key">Grid Square</div>
                <div class="field-val">{{ packet.gridSquare }}</div>
              </template>
            </div>
          </template>

          <!-- ── Path ── -->
          <template v-if="packet.path">
            <div class="section-label">Path</div>
            <div class="field-grid">
              <div class="field-key">Raw Path</div>
              <div class="field-val"><code>{{ packet.path }}</code></div>

              <div class="field-key">Hop Count</div>
              <div class="field-val">{{ packet.hopCount }}</div>

              <div class="field-key">Heard Via</div>
              <div class="field-val">{{ heardViaLabel(packet) }}</div>
            </div>

            <!-- Hop chain -->
            <template v-if="orderedPath.length > 0">
              <div class="field-grid">
                <div class="field-key">Hops</div>
                <div class="field-val">
                  <div class="hop-chain">
                    <!-- Source station -->
                    <span
                      class="hop-node hop-node--source"
                      :title="`Select ${packet.stationCallsign}`"
                      @click="onHopClick(packet.stationCallsign)"
                    >{{ packet.stationCallsign }}</span>

                    <template v-for="entry in orderedPath" :key="entry.callsign">
                      <span class="hop-arrow">──→</span>
                      <span
                        v-if="entry.known"
                        class="hop-node hop-node--known"
                        :title="`Select ${entry.callsign}`"
                        @click="onHopClick(entry.callsign)"
                      >{{ entry.callsign }} ✓</span>
                      <span v-else class="hop-node hop-node--unknown">{{ entry.callsign }} ?</span>
                    </template>
                  </div>
                </div>
              </div>
            </template>
          </template>

          <!-- ── Comment / Status ── -->
          <template v-if="packet.comment">
            <div class="section-label">Comment</div>
            <div class="px-3 pb-3">
              <pre class="comment-block">{{ packet.comment }}</pre>
            </div>
          </template>

          <!-- ── Weather ── -->
          <template v-if="isWeather && packet.weatherData">
            <div class="section-label">Weather</div>
            <div class="field-grid">
              <template v-if="packet.weatherData.temperatureF != null">
                <div class="field-key">Temperature</div>
                <div class="field-val">
                  {{ packet.weatherData.temperatureF.toFixed(1) }}°F
                  <span class="text-medium-emphasis">({{ fToC(packet.weatherData.temperatureF) }}°C)</span>
                </div>
              </template>
              <template v-if="packet.weatherData.humidityPercent != null">
                <div class="field-key">Humidity</div>
                <div class="field-val">{{ packet.weatherData.humidityPercent }}%</div>
              </template>
              <template v-if="packet.weatherData.windSpeedMph != null || packet.weatherData.windDirectionDeg != null">
                <div class="field-key">Wind</div>
                <div class="field-val">
                  <template v-if="packet.weatherData.windSpeedMph != null">
                    {{ packet.weatherData.windSpeedMph.toFixed(1) }} mph
                    <span class="text-medium-emphasis">({{ mphToKmh(packet.weatherData.windSpeedMph) }} km/h)</span>
                  </template>
                  <template v-if="packet.weatherData.windDirectionDeg != null">
                    <span class="ml-1">from {{ packet.weatherData.windDirectionDeg }}°
                      ({{ compassDir16(packet.weatherData.windDirectionDeg) }})
                    </span>
                  </template>
                </div>
              </template>
              <template v-if="packet.weatherData.windGustMph != null">
                <div class="field-key">Wind Gust</div>
                <div class="field-val">{{ packet.weatherData.windGustMph.toFixed(1) }} mph</div>
              </template>
              <template v-if="packet.weatherData.pressureMbar != null">
                <div class="field-key">Pressure</div>
                <div class="field-val">{{ packet.weatherData.pressureMbar.toFixed(1) }} mbar</div>
              </template>
              <template v-if="packet.weatherData.rainfallLastHourIn != null">
                <div class="field-key">Rain (1h)</div>
                <div class="field-val">{{ packet.weatherData.rainfallLastHourIn.toFixed(2) }} in</div>
              </template>
              <template v-if="packet.weatherData.rainfallLast24hIn != null">
                <div class="field-key">Rain (24h)</div>
                <div class="field-val">{{ packet.weatherData.rainfallLast24hIn.toFixed(2) }} in</div>
              </template>
              <template v-if="packet.weatherData.rainfallSinceMidnightIn != null">
                <div class="field-key">Rain (midnight)</div>
                <div class="field-val">{{ packet.weatherData.rainfallSinceMidnightIn.toFixed(2) }} in</div>
              </template>
            </div>
          </template>

          <!-- ── Message ── -->
          <template v-if="isMessage && packet.messageData">
            <div class="section-label">Message</div>
            <div class="field-grid">
              <template v-if="packet.messageData.addressee">
                <div class="field-key">To</div>
                <div class="field-val">{{ packet.messageData.addressee }}</div>
              </template>
              <template v-if="packet.messageData.text">
                <div class="field-key">Message</div>
                <div class="field-val">{{ packet.messageData.text }}</div>
              </template>
              <template v-if="packet.messageData.messageId">
                <div class="field-key">Message ID</div>
                <div class="field-val">{{ packet.messageData.messageId }}</div>
              </template>
            </div>
          </template>

          <!-- ── Telemetry ── -->
          <template v-if="isTelemetry && packet.telemetryData">
            <div class="section-label">Telemetry</div>
            <div class="px-3 pb-3">
              <pre class="raw-block">{{ JSON.stringify(packet.telemetryData, null, 2) }}</pre>
            </div>
          </template>

          <!-- ── Signal ── -->
          <template v-if="packet.signalData && (packet.signalData.decodeQuality != null || packet.signalData.frequencyOffsetHz != null)">
            <div class="section-label">Signal</div>
            <div class="field-grid">
              <template v-if="packet.signalData.decodeQuality != null">
                <div class="field-key">Decode Quality</div>
                <div class="field-val">{{ packet.signalData.decodeQuality }}</div>
              </template>
              <template v-if="packet.signalData.frequencyOffsetHz != null">
                <div class="field-key">Frequency Offset</div>
                <div class="field-val">
                  {{ packet.signalData.frequencyOffsetHz > 0 ? '+' : '' }}{{ packet.signalData.frequencyOffsetHz.toFixed(0) }} Hz
                </div>
              </template>
            </div>
          </template>

          <!-- ── Raw Packet (toggle) ── -->
          <div class="raw-toggle-row">
            <v-btn
              size="x-small"
              variant="tonal"
              @click="showRaw = !showRaw"
            >
              {{ showRaw ? 'Hide Raw' : 'Show Raw' }}
            </v-btn>
          </div>
          <div v-if="showRaw" class="px-3 pb-4">
            <pre class="raw-block">{{ packet.rawPacket }}</pre>
          </div>

        </template>

        <div v-else-if="!loading" class="text-center text-medium-emphasis py-8">
          Could not load packet.
        </div>
      </v-card-text>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.dialog-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  gap: 8px;
  flex-wrap: wrap;
  flex-shrink: 0;
}

.section-label {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.07em;
  color: rgba(var(--v-theme-on-surface), 0.5);
  padding: 10px 16px 4px;
}

.field-grid {
  display: grid;
  grid-template-columns: 130px 1fr;
  gap: 4px 12px;
  padding: 0 16px 8px;
  align-items: baseline;
}

.field-key {
  font-size: 0.75rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
  font-weight: 500;
  white-space: nowrap;
  padding-top: 2px;
}

.field-val {
  font-size: 0.875rem;
  word-break: break-word;
}

.hop-chain {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px;
  font-size: 0.8rem;
}

.hop-node {
  display: inline-flex;
  align-items: center;
  padding: 2px 6px;
  border-radius: 4px;
  font-family: monospace;
  font-size: 0.8rem;
  font-weight: 500;
  border: 1px solid rgba(var(--v-theme-on-surface), 0.18);
  background: rgba(var(--v-theme-on-surface), 0.04);
}

.hop-node--source,
.hop-node--known {
  color: rgba(var(--v-theme-primary), 1);
  border-color: rgba(var(--v-theme-primary), 0.4);
  background: rgba(var(--v-theme-primary), 0.06);
  cursor: pointer;
}

.hop-node--source:hover,
.hop-node--known:hover {
  background: rgba(var(--v-theme-primary), 0.14);
}

.hop-node--unknown {
  color: rgba(var(--v-theme-on-surface), 0.55);
}

.hop-arrow {
  color: rgba(var(--v-theme-on-surface), 0.35);
  font-size: 0.75rem;
  user-select: none;
}

.comment-block {
  font-family: monospace;
  font-size: 0.8125rem;
  white-space: pre-wrap;
  word-break: break-all;
  background: rgba(var(--v-theme-on-surface), 0.04);
  border: 1px solid rgba(var(--v-theme-on-surface), 0.1);
  border-radius: 4px;
  padding: 8px 10px;
  margin: 0;
}

.raw-block {
  font-family: monospace;
  font-size: 0.8125rem;
  white-space: pre;
  overflow-x: auto;
  background: rgba(var(--v-theme-on-surface), 0.04);
  border: 1px solid rgba(var(--v-theme-on-surface), 0.1);
  border-radius: 4px;
  padding: 8px 10px;
  margin: 0;
}

.raw-toggle-row {
  padding: 4px 16px 8px;
}
</style>
