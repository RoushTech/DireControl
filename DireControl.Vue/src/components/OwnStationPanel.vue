<script setup lang="ts">
import { formatSecondsAgo } from '@/utils/time'
import { ref } from 'vue'
import { useDisplay } from 'vuetify'
import { useRadiosStore } from '@/stores/radiosStore'
import { useTxStore } from '@/stores/tx'
import { useHealthStore, agoShort } from '@/stores/health'
import TxLifecycle from '@/components/tx/TxLifecycle.vue'
import BeaconHistoryModal from './BeaconHistoryModal.vue'
import { useTick } from '@/composables/useTick'
import type { RadioDto, DigiConfirmationDto } from '@/types/radio'

const radiosStore = useRadiosStore()

// On a phone the full cards would cover the map; they collapse to one chip.
const { smAndDown } = useDisplay()
const expanded = ref(false)
const txStore = useTxStore()
const healthStore = useHealthStore()

// Shared singleton tick — no extra setInterval created if already running at 1 s
const { now } = useTick(1000)

// ─── Modal state ──────────────────────────────────────────────────────────────
const historyOpen = ref(false)
const historyRadioId = ref('')

function openHistory(radioId: string) {
  historyRadioId.value = radioId
  historyOpen.value = true
}

// ─── Beacon Now ───────────────────────────────────────────────────────────────
const beaconing = ref<Record<string, boolean>>({})

async function doBeaconNow(radio: RadioDto) {
  beaconing.value[radio.id] = true
  try {
    // Queued, not sent: the lifecycle below shows when it is actually on air.
    if (await txStore.beaconNow(radio.id, radio.fullCallsign))
      void radiosStore.fetchLastBeacon(radio.id)
  } finally {
    beaconing.value[radio.id] = false
  }
}

/** Why this radio cannot beacon right now, or null. */
function txBlocked(radio: RadioDto): string | null {
  const h = healthStore.health
  const lane = h.radios.find((r) => r.radioId === radio.id)?.tx
  if (h.txBlockedReason) return h.txBlockedReason
  if (!lane) return null
  if (lane.value === 'Blocked' || lane.level === 'err' || lane.level === 'off') return lane.detail
  return null
}

/** The latest beacon frame, while it is still worth showing (5 min). */
function beaconFrame(radio: RadioDto) {
  const f = txStore.latestBeacon(radio.id)
  if (!f) return null
  return now.value - new Date(f.queuedAt).getTime() < 5 * 60_000 ? f : null
}

function lastRfLabel(radio: RadioDto): string {
  const m = radiosStore.getModemStatusForRadio(radio.id)
  if (!radio.modem.modemEnabled) return 'external TNC'
  if (!m) return '—'
  return m.lastDecodedAt
    ? `${agoShort(new Date(m.lastDecodedAt).getTime(), now.value)} ago`
    : 'none yet'
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function secondsAgo(radioId: string): number | null {
  const lb = radiosStore.getLastBeaconForRadio(radioId)
  if (!lb?.beaconedAt) return null
  return Math.max(0, Math.floor((now.value - new Date(lb.beaconedAt).getTime()) / 1000))
}

function lastBeaconLabel(radio: RadioDto): string {
  const lb = radiosStore.getLastBeaconForRadio(radio.id)
  const ago = formatSecondsAgo(secondsAgo(radio.id))
  if (!lb?.beaconedAt) return ago
  return lb.heard ? `${ago} · heard ✓` : ago
}

function awaitingConfirmation(radio: RadioDto): boolean {
  const lb = radiosStore.getLastBeaconForRadio(radio.id)
  return !!lb?.beaconedAt && !lb.heard
}

/** Fastest digipeater that repeated the current beacon (confirmations are sorted). */
function firstConfirmation(radioId: string): DigiConfirmationDto | null {
  const lb = radiosStore.getLastBeaconForRadio(radioId)
  return lb?.confirmations[0] ?? null
}

function extraConfirmations(radioId: string): number {
  const lb = radiosStore.getLastBeaconForRadio(radioId)
  return Math.max(0, (lb?.confirmations.length ?? 0) - 1)
}

/** Countdown to the next scheduled auto-beacon, from the last beacon time. */
function nextAutoBeaconLabel(radio: RadioDto): string | null {
  if (!radio.autoBeaconEnabled) return null
  const lb = radiosStore.getLastBeaconForRadio(radio.id)
  if (!lb?.beaconedAt) return 'soon'
  const dueAt = new Date(lb.beaconedAt).getTime() + radio.autoBeaconIntervalSeconds * 1000
  const secs = Math.floor((dueAt - now.value) / 1000)
  if (secs <= 0) return 'due now'
  if (secs < 60) return `in ${secs}s`
  return `in ${Math.floor(secs / 60)}m ${secs % 60}s`
}

// When auto-beaconing is on, "overdue" is judged against the auto-beacon
// interval; otherwise it falls back to the expected (monitoring) interval.
function effectiveInterval(radio: RadioDto): number {
  return radio.autoBeaconEnabled ? radio.autoBeaconIntervalSeconds : radio.expectedIntervalSeconds
}

function dotColor(radio: RadioDto): string {
  const lb = radiosStore.getLastBeaconForRadio(radio.id)
  const secs = secondsAgo(radio.id)
  if (secs === null) return 'grey'
  if (lb && !lb.heard) return 'warning'
  const interval = effectiveInterval(radio)
  if (secs <= interval) return 'success'
  if (secs <= interval * 1.5) return 'warning'
  return 'error'
}

function txChip(radio: RadioDto): { label: string; color: string } {
  const activity = radiosStore.getActivityForRadio(radio.id)
  if (activity?.transmitting) return { label: 'TX', color: 'error' }
  const held = radiosStore.carrierHeldSeconds(radio.id)
  if (held !== null) return { label: `Carrier held ${held} s`, color: 'warning' }
  if (activity?.carrierDetected) return { label: 'RX', color: 'success' }
  // Ready means the modem is actually running with TX — not merely configured.
  if (radio.modem.modemEnabled && radio.modem.txEnabled)
    return radiosStore.isTxLive(radio.id)
      ? { label: 'TX ready', color: 'success' }
      : { label: 'TX down', color: 'warning' }
  if (radio.modem.modemEnabled) return { label: 'RX only', color: 'grey' }
  return { label: 'no modem', color: 'grey' }
}
</script>

<template>
  <div
    v-if="radiosStore.activeRadios.length > 0 && smAndDown && !expanded"
    class="own-station-panel own-station-panel--compact"
  >
    <v-chip
      v-for="radio in radiosStore.activeRadios"
      :key="radio.id"
      :color="txChip(radio).color"
      size="small"
      variant="elevated"
      prepend-icon="mdi-radio-handheld"
      :aria-label="`${radio.fullCallsign}: ${txChip(radio).label} — show station panel`"
      @click="expanded = true"
    >
      {{ radio.fullCallsign }} · {{ txChip(radio).label }}
    </v-chip>
  </div>
  <div v-else-if="radiosStore.activeRadios.length > 0" class="own-station-panel">
    <v-btn
      v-if="smAndDown"
      size="x-small"
      variant="tonal"
      prepend-icon="mdi-chevron-down"
      class="align-self-start"
      @click="expanded = false"
    >
      Collapse
    </v-btn>
    <div v-for="radio in radiosStore.activeRadios" :key="radio.id" class="own-station-card">
      <!-- Header: status dot · name · callsign · live TX chip -->
      <div class="own-head mb-1">
        <v-icon :color="dotColor(radio)" size="10" class="flex-shrink-0">mdi-circle</v-icon>
        <span class="text-caption font-weight-bold flex-shrink-0">{{ radio.name }}</span>
        <v-chip size="x-small" variant="tonal" class="own-callsign">
          {{ radio.fullCallsign }}
        </v-chip>
        <v-chip
          :color="txChip(radio).color"
          size="x-small"
          variant="tonal"
          class="own-tx-chip flex-shrink-0 ml-auto"
        >
          {{ txChip(radio).label }}
        </v-chip>
      </div>

      <!-- Labeled rows -->
      <div class="own-row">
        <span class="own-label">Last RF decode</span>
        <span
          class="own-value"
          :class="{
            'text-warning':
              healthStore.health.radios.find((r) => r.radioId === radio.id)?.rf.level === 'warn',
          }"
          >{{ lastRfLabel(radio) }}</span
        >
      </div>
      <div class="own-row">
        <span class="own-label">Last beacon</span>
        <span class="own-value" :class="{ 'text-warning': awaitingConfirmation(radio) }">
          {{ lastBeaconLabel(radio) }}
          <template v-if="awaitingConfirmation(radio)"> · unconfirmed</template>
        </span>
      </div>
      <div v-if="firstConfirmation(radio.id)" class="own-row">
        <span class="own-label">Repeated by</span>
        <span class="own-value">
          {{ firstConfirmation(radio.id)!.digipeater }} ·
          {{ firstConfirmation(radio.id)!.secondsAfterBeacon.toFixed(1) }}s<template
            v-if="extraConfirmations(radio.id) > 0"
          >
            +{{ extraConfirmations(radio.id) }}</template
          >
        </span>
      </div>
      <div v-if="nextAutoBeaconLabel(radio)" class="own-row">
        <span class="own-label">Next auto-beacon</span>
        <span class="own-value">{{ nextAutoBeaconLabel(radio) }}</span>
      </div>
      <div class="own-row">
        <span class="own-label">Beacon reach</span>
        <!-- Reach figures live on Network only (one place, deaf-aware). -->
        <router-link class="own-value own-link" to="/network?tab=reach">on Network ›</router-link>
      </div>

      <TxLifecycle v-if="beaconFrame(radio)" :item="beaconFrame(radio)!" compact class="mt-2" />

      <div class="d-flex align-center ga-1 mt-2">
        <v-btn
          size="small"
          variant="tonal"
          color="primary"
          prepend-icon="mdi-access-point"
          class="flex-grow-1"
          :loading="beaconing[radio.id]"
          :disabled="!!txBlocked(radio)"
          @click="doBeaconNow(radio)"
        >
          Beacon now
        </v-btn>
        <v-btn
          icon="mdi-history"
          size="small"
          variant="text"
          density="comfortable"
          class="flex-shrink-0"
          title="Beacon history"
          aria-label="Beacon history"
          @click="openHistory(radio.id)"
        />
      </div>
      <!-- TX blocked: keep the button visible and say why (review 1.2). -->
      <div v-if="txBlocked(radio)" class="own-blocked text-warning">{{ txBlocked(radio) }}</div>
    </div>
  </div>
  <!-- No radio yet: say so, rather than the panel silently not existing. -->
  <div v-else-if="radiosStore.loaded" class="own-station-panel">
    <div class="own-station-card">
      <div class="text-body-2 mb-2">
        No radio set up yet — DireControl has nothing to beacon with.
      </div>
      <v-btn size="small" variant="tonal" color="primary" to="/settings/radios">
        Add a radio
      </v-btn>
    </div>
  </div>

  <BeaconHistoryModal
    v-if="historyRadioId"
    v-model="historyOpen"
    :radio-id="historyRadioId"
    :radio-name="radiosStore.radios.find((r) => r.id === historyRadioId)?.name ?? historyRadioId"
  />
</template>

<style scoped>
.own-station-panel {
  position: absolute;
  bottom: 24px;
  left: 10px;
  z-index: 1000;
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: 248px;
  max-width: calc(100vw - 20px);
  pointer-events: auto;
}

.own-station-panel--compact {
  width: auto;
  align-items: flex-start;
}

.own-station-card {
  background: rgba(var(--v-theme-surface), 0.94);
  border: 1px solid rgba(var(--v-border-color), 0.4);
  border-radius: 10px;
  padding: 10px 12px;
  backdrop-filter: blur(4px);
  box-shadow: 0 2px 10px rgba(0, 0, 0, 0.25);
}

.own-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: nowrap;
  min-width: 0;
}

/* The callsign is the only element allowed to shrink — chips and buttons keep
   their intrinsic size so labels never get crushed. */
.own-callsign {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-weight: 600;
  flex: 0 1 auto;
  min-width: 0;
  overflow: hidden;
}

.own-tx-chip {
  max-width: none;
}

.own-link {
  color: rgb(var(--v-theme-primary));
  text-decoration: none;
}

.own-blocked {
  font-size: 0.72rem;
  margin-top: 4px;
}

.own-row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  padding: 1px 0;
}

.own-label {
  font-size: 0.72rem;
  color: rgba(var(--v-theme-on-surface), 0.55);
  flex-shrink: 0;
}

.own-value {
  font-size: 0.74rem;
  text-align: right;
  font-variant-numeric: tabular-nums;
  min-width: 0;
}
</style>
