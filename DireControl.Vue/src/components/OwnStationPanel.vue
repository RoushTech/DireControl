<script setup lang="ts">
import { ref } from 'vue'
import { useRadiosStore } from '@/stores/radiosStore'
import { useToastStore } from '@/stores/toastStore'
import BeaconHistoryModal from './BeaconHistoryModal.vue'
import { useTick } from '@/composables/useTick'
import { beaconNow } from '@/api/radiosApi'
import type { RadioDto, DigiConfirmationDto } from '@/types/radio'

const radiosStore = useRadiosStore()
const toastStore = useToastStore()

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
    await beaconNow(radio.id)
    toastStore.toast(`Beacon sent for ${radio.fullCallsign}.`, 'success')
    void radiosStore.fetchLastBeacon(radio.id)
  } catch {
    toastStore.toast(
      `Beacon failed for ${radio.fullCallsign} — check that TX is enabled and home position is set.`,
      'error',
    )
  } finally {
    beaconing.value[radio.id] = false
  }
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function secondsAgo(radioId: string): number | null {
  const lb = radiosStore.getLastBeaconForRadio(radioId)
  if (!lb?.beaconedAt) return null
  return Math.max(0, Math.floor((now.value - new Date(lb.beaconedAt).getTime()) / 1000))
}

function formatSecondsAgo(secs: number | null): string {
  if (secs === null) return 'never'
  if (secs < 60) return `${secs}s ago`
  if (secs < 3600) return `${Math.floor(secs / 60)}m ${secs % 60}s ago`
  return `${Math.floor(secs / 3600)}h ${Math.floor((secs % 3600) / 60)}m ago`
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
  if (activity?.carrierDetected) return { label: 'RX', color: 'success' }
  if (radio.modem.modemEnabled && radio.modem.txEnabled)
    return { label: 'TX ready', color: 'success' }
  if (radio.modem.modemEnabled) return { label: 'RX only', color: 'grey' }
  return { label: 'no modem', color: 'grey' }
}
</script>

<template>
  <div v-if="radiosStore.activeRadios.length > 0" class="own-station-panel">
    <div v-for="radio in radiosStore.activeRadios" :key="radio.id" class="own-station-card">
      <!-- Header: status dot · name · callsign · live TX chip -->
      <div class="d-flex align-center ga-2 mb-1">
        <v-icon :color="dotColor(radio)" size="10">mdi-circle</v-icon>
        <span class="text-caption font-weight-bold">{{ radio.name }}</span>
        <span class="text-caption text-medium-emphasis own-callsign">{{ radio.fullCallsign }}</span>
        <v-spacer />
        <v-chip :color="txChip(radio).color" size="x-small" variant="tonal">
          {{ txChip(radio).label }}
        </v-chip>
        <v-btn
          icon="mdi-history"
          size="x-small"
          variant="text"
          density="comfortable"
          title="Beacon history"
          @click="openHistory(radio.id)"
        />
      </div>

      <!-- Labeled rows, mock-style -->
      <div class="own-row">
        <span class="own-label">Last beacon</span>
        <span class="own-value" :class="{ 'text-warning': awaitingConfirmation(radio) }">
          {{ lastBeaconLabel(radio) }}
          <template v-if="awaitingConfirmation(radio)"> · awaiting confirmation</template>
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
        <span class="own-label">Confirmed</span>
        <span class="own-value">
          {{ radio.confirmationCount.toLocaleString() }} /
          {{ radio.beaconCount.toLocaleString() }}
        </span>
      </div>

      <v-btn
        size="small"
        variant="tonal"
        color="primary"
        prepend-icon="mdi-access-point"
        block
        class="mt-2"
        :loading="beaconing[radio.id]"
        @click="doBeaconNow(radio)"
      >
        Beacon now
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
  pointer-events: auto;
}

.own-station-card {
  background: rgba(var(--v-theme-surface), 0.94);
  border: 1px solid rgba(var(--v-border-color), 0.4);
  border-radius: 10px;
  padding: 10px 12px;
  backdrop-filter: blur(4px);
  box-shadow: 0 2px 10px rgba(0, 0, 0, 0.25);
}

.own-callsign {
  font-variant-numeric: tabular-nums;
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
