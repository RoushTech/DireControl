<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import {
  forceUnkey,
  getModemDiagnostics,
  ModemStates,
  modemStateLabels,
  PttMethods,
  sendTestTone,
  TestToneKinds,
  type ModemDiagnosticsDto,
  type PttMethod,
} from '@/api/modemApi'
import { PttStates } from '@/api/txApi'
import { apiErrorDetail } from '@/api/axios'
import { useToastStore } from '@/stores/toastStore'
import { useTxStore } from '@/stores/tx'
import { useHealthStore, agoShort, durationShort } from '@/stores/health'
import { useRadiosStore } from '@/stores/radiosStore'
import { TxFrameStates } from '@/api/txApi'
import CaptureDiagnostics from '@/components/CaptureDiagnostics.vue'

/**
 * The RF internals that used to be hidden under the headphones button or not
 * shown at all (review 1.4, 1.18, 3.11, 3.12, 3.21, 3.24): carrier-detect duty
 * and the stuck rule, TX lanes and drops, CSMA waits and timeouts, ALSA
 * period and overruns, PTT state and watchdog, decodes per profile, plus
 * recording, auto-capture and the log pack.
 */
const toast = useToastStore()
const txStore = useTxStore()
const health = useHealthStore()
const radiosStore = useRadiosStore()

const rows = ref<ModemDiagnosticsDto[]>([])
const loaded = ref(false)
const error = ref<string | null>(null)
let timer: ReturnType<typeof setInterval> | null = null

async function refresh() {
  try {
    rows.value = await getModemDiagnostics()
    error.value = null
  } catch (e) {
    error.value = apiErrorDetail(e, 'Diagnostics unavailable')
  } finally {
    loaded.value = true
  }
}

onMounted(() => {
  void refresh()
  timer = setInterval(() => void refresh(), 2000)
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
})

const now = computed(() => health.now)

const PTT_LABELS: Record<PttMethod, string> = {
  [PttMethods.Unknown]: 'Unknown',
  [PttMethods.None]: 'VOX / none',
  [PttMethods.SerialRtsDtr]: 'Serial RTS/DTR',
  [PttMethods.Cm108]: 'CM108 GPIO',
  [PttMethods.Gpio]: 'GPIO line',
  [PttMethods.Rigctld]: 'rigctld',
}

const PTT_STATE: Record<number, { text: string; color: string }> = {
  [PttStates.Unknown]: { text: 'no TX', color: 'grey' },
  [PttStates.Released]: { text: 'Released', color: 'success' },
  [PttStates.Keyed]: { text: 'Keyed', color: 'error' },
  [PttStates.Stuck]: { text: 'STUCK', color: 'error' },
}

function dutyColor(d: ModemDiagnosticsDto): string {
  const v = d.carrierDutyLastMinute
  if (v == null) return 'grey'
  if (d.carrierStuck || v > 0.8) return 'error'
  if (v > 0.5) return 'warning'
  return 'success'
}

function carrierText(d: ModemDiagnosticsDto): string {
  if (!d.carrierDetected) return 'quiet'
  const held = d.carrierSince ? durationShort(now.value - new Date(d.carrierSince).getTime()) : ''
  return `held ${held} · ${d.preamblesSinceCarrier} preamble(s)${d.carrierStuck ? ' · STUCK' : ''}`
}

function profiles(d: ModemDiagnosticsDto): Array<[string, number]> {
  return Object.entries(d.decodedByProfile ?? {}).sort((a, b) => b[1] - a[1])
}

function ago(iso: string | null): string {
  return iso ? `${agoShort(new Date(iso).getTime(), now.value)} ago` : 'never'
}

const busy = reactive<Record<string, 'ptt' | 'unkey' | null>>({})

async function testPtt(d: ModemDiagnosticsDto) {
  busy[d.radioId] = 'ptt'
  try {
    const res = await sendTestTone(d.radioId, TestToneKinds.PttOnly, 1000)
    const f = res.txFrameIds[0] ? await txStore.waitFor(res.txFrameIds[0], 30_000) : undefined
    if (f?.state === TxFrameStates.Sent) {
      toast.toast('Keyed ✓ — PTT held for 1 s and released.', 'success')
      health.markPttTested()
    } else toast.toast(`PTT test did not key: ${f?.reason ?? 'no result'}`, 'error', 10_000)
  } catch (e) {
    toast.toast(apiErrorDetail(e, 'PTT test failed'), 'error', 10_000)
  } finally {
    busy[d.radioId] = null
  }
}

async function doForceUnkey(d: ModemDiagnosticsDto) {
  busy[d.radioId] = 'unkey'
  try {
    await forceUnkey(d.radioId)
    toast.toast('PTT released', 'success')
    await Promise.all([refresh(), radiosStore.fetchModemStatus()])
  } catch (e) {
    toast.toast(apiErrorDetail(e, 'Force unkey failed'), 'error', 10_000)
  } finally {
    busy[d.radioId] = null
  }
}
</script>

<template>
  <div class="diag-view">
    <div class="diag-inner">
      <header class="d-flex align-center flex-wrap ga-2 mb-4">
        <v-btn
          icon="mdi-arrow-left"
          variant="text"
          size="small"
          to="/radio"
          aria-label="Back to Radio"
        />
        <div>
          <h2 class="text-h6">Diagnostics</h2>
          <div class="text-body-2 text-medium-emphasis">
            Receive and transmit internals, refreshed every 2 s.
          </div>
        </div>
      </header>

      <v-alert v-if="error" type="error" variant="tonal" density="compact" class="mb-4">
        {{ error }}
      </v-alert>
      <v-alert
        v-else-if="loaded && rows.length === 0"
        type="info"
        variant="tonal"
        density="compact"
        class="mb-4"
      >
        No sound-modem radio is running. These figures come from the built-in modem; an external TNC
        keeps its own.
        <template #append>
          <v-btn variant="text" to="/settings/radios">Radios</v-btn>
        </template>
      </v-alert>

      <section v-for="d in rows" :key="d.radioId" class="mb-6" :aria-label="d.radioName">
        <div class="d-flex align-center ga-2 mb-2">
          <h3 class="text-subtitle-1 font-weight-bold">{{ d.radioName }}</h3>
          <v-chip
            size="small"
            variant="tonal"
            :color="
              d.state === ModemStates.Running
                ? 'success'
                : d.state === ModemStates.Error
                  ? 'error'
                  : 'grey'
            "
          >
            {{ modemStateLabels[d.state] }}
          </v-chip>
        </div>

        <div class="diag-grid">
          <!-- Carrier detect -->
          <v-card variant="outlined">
            <v-card-title class="text-subtitle-2">Carrier detect</v-card-title>
            <v-card-text>
              <div class="d-flex justify-space-between text-body-2 mb-1">
                <span>Duty, last minute</span>
                <strong>{{
                  d.carrierDutyLastMinute == null
                    ? '—'
                    : `${Math.round(d.carrierDutyLastMinute * 100)} %`
                }}</strong>
              </div>
              <v-progress-linear
                :model-value="(d.carrierDutyLastMinute ?? 0) * 100"
                :color="dutyColor(d)"
                height="8"
                rounded
                aria-label="Carrier-detect duty"
              />
              <v-table density="compact" class="mt-2">
                <tbody>
                  <tr>
                    <td>Now</td>
                    <td class="text-right" :class="{ 'text-error': d.carrierStuck }">
                      {{ carrierText(d) }}
                    </td>
                  </tr>
                  <tr>
                    <td>Stuck rule</td>
                    <td class="text-right">
                      held {{ d.carrierStuckSeconds }} s with no preamble → ignored by CSMA
                    </td>
                  </tr>
                </tbody>
              </v-table>
              <div class="text-caption text-medium-emphasis mt-2">
                A busy channel sits around 10–40 %. Near 100 % with no decodes is open squelch or
                noise: lower the RX level or raise squelch.
              </div>
            </v-card-text>
          </v-card>

          <!-- Decoder -->
          <v-card variant="outlined">
            <v-card-title class="text-subtitle-2">Decoder</v-card-title>
            <v-card-text>
              <v-table density="compact">
                <tbody>
                  <tr>
                    <td>Decoded / bad CRC</td>
                    <td class="text-right">
                      {{ d.decodedFrames.toLocaleString() }} /
                      {{ d.invalidFrames.toLocaleString() }}
                    </td>
                  </tr>
                  <tr v-for="[name, n] in profiles(d)" :key="name">
                    <td class="text-medium-emphasis">Profile {{ name }}</td>
                    <td class="text-right">{{ n.toLocaleString() }}</td>
                  </tr>
                  <tr>
                    <td>Ingest drops</td>
                    <td class="text-right" :class="{ 'text-warning': d.ingestDropped > 0 }">
                      {{ d.ingestDropped }}
                    </td>
                  </tr>
                </tbody>
              </v-table>
            </v-card-text>
          </v-card>

          <!-- Audio -->
          <v-card variant="outlined">
            <v-card-title class="text-subtitle-2">Audio capture</v-card-title>
            <v-card-text>
              <v-table density="compact">
                <tbody>
                  <tr>
                    <td>Device</td>
                    <td class="text-right font-mono">{{ d.captureDevice ?? '—' }}</td>
                  </tr>
                  <tr>
                    <td>ALSA period</td>
                    <td class="text-right">
                      {{
                        d.capturePeriodMs > 0
                          ? `${d.capturePeriodMs} ms${d.captureExplicitPeriod ? '' : ' requested (refused)'}`
                          : 'driver default'
                      }}
                    </td>
                  </tr>
                  <tr>
                    <td>Overruns (xruns)</td>
                    <td class="text-right" :class="{ 'text-warning': d.captureOverruns > 0 }">
                      {{ d.captureOverruns.toLocaleString() }}
                    </td>
                  </tr>
                  <tr>
                    <td>Last audio block</td>
                    <td class="text-right">{{ ago(d.levelUpdatedAt) }}</td>
                  </tr>
                </tbody>
              </v-table>
            </v-card-text>
          </v-card>

          <!-- Transmit -->
          <v-card variant="outlined">
            <v-card-title class="text-subtitle-2">Transmit</v-card-title>
            <v-card-text>
              <div v-if="!d.txEnabled" class="text-body-2 text-medium-emphasis">
                Receive only — TX is not enabled for this radio.
              </div>
              <template v-else>
                <v-table density="compact">
                  <thead>
                    <tr>
                      <th>Lane</th>
                      <th class="text-right">Queued</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td>Session (LAPB)</td>
                      <td class="text-right">{{ d.sessionLaneDepth }}</td>
                    </tr>
                    <tr>
                      <td>Digipeat</td>
                      <td class="text-right">{{ d.digipeatLaneDepth }}</td>
                    </tr>
                    <tr>
                      <td>Normal</td>
                      <td class="text-right">{{ d.normalLaneDepth }}</td>
                    </tr>
                    <tr>
                      <td>Dropped (lane full)</td>
                      <td class="text-right" :class="{ 'text-warning': d.txDropped > 0 }">
                        {{ d.txDropped }}
                      </td>
                    </tr>
                    <tr>
                      <td>CSMA waiting</td>
                      <td class="text-right" :class="{ 'text-warning': !!d.csmaWaitingSince }">
                        {{
                          d.csmaWaitingSince
                            ? durationShort(now - new Date(d.csmaWaitingSince).getTime())
                            : '—'
                        }}
                      </td>
                    </tr>
                    <tr>
                      <td>Channel timeouts</td>
                      <td class="text-right" :class="{ 'text-warning': d.csmaTimeouts > 0 }">
                        {{ d.csmaTimeouts }} ·
                        {{ d.csmaTimeoutAction === 2 ? 'sent anyway' : 'discarded' }} after
                        {{ d.csmaMaxWaitSeconds }} s
                      </td>
                    </tr>
                    <tr>
                      <td>Sent / last TX</td>
                      <td class="text-right">
                        {{ d.transmittedFrames.toLocaleString() }} · {{ ago(d.lastTxAt) }}
                      </td>
                    </tr>
                  </tbody>
                </v-table>

                <div class="d-flex align-center flex-wrap ga-2 mt-3">
                  <span class="text-body-2">PTT {{ PTT_LABELS[d.pttMethod] }}</span>
                  <v-chip size="small" variant="tonal" :color="PTT_STATE[d.pttState]?.color">
                    {{ PTT_STATE[d.pttState]?.text }}
                  </v-chip>
                  <span class="text-caption text-medium-emphasis">
                    watchdog trips {{ d.pttWatchdogTrips }}
                  </span>
                </div>
                <div v-if="d.txError" class="text-caption text-error mt-1">{{ d.txError }}</div>
                <div class="d-flex flex-wrap ga-2 mt-3">
                  <v-btn
                    size="small"
                    variant="tonal"
                    prepend-icon="mdi-radio-handheld"
                    :loading="busy[d.radioId] === 'ptt'"
                    :disabled="d.state !== ModemStates.Running || d.pttState === PttStates.Stuck"
                    @click="testPtt(d)"
                  >
                    Test PTT (1 s)
                  </v-btn>
                  <v-btn
                    size="small"
                    :variant="d.pttState === PttStates.Stuck ? 'flat' : 'outlined'"
                    color="error"
                    prepend-icon="mdi-radio-off"
                    :loading="busy[d.radioId] === 'unkey'"
                    :disabled="d.state === ModemStates.Running"
                    @click="doForceUnkey(d)"
                  >
                    Force unkey
                  </v-btn>
                </div>
                <div class="text-caption text-medium-emphasis mt-1">
                  Force unkey works once the modem session has stopped (it owns PTT while running).
                </div>
              </template>
            </v-card-text>
          </v-card>
        </div>
      </section>

      <!-- Recording, auto-capture and the log pack (moved from the headphones menu) -->
      <v-card variant="outlined" class="mb-6">
        <v-card-title class="text-subtitle-2">Audio capture &amp; log pack</v-card-title>
        <v-card-text>
          <CaptureDiagnostics />
        </v-card-text>
      </v-card>
    </div>
  </div>
</template>

<style scoped>
.diag-view {
  height: 100%;
  overflow-y: auto;
}
.diag-inner {
  max-width: 1200px;
  margin: 0 auto;
  padding: 16px;
}
.diag-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(100%, 340px), 1fr));
  gap: 16px;
}
.font-mono {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  font-size: 0.8125rem;
  overflow-wrap: anywhere;
}
</style>
