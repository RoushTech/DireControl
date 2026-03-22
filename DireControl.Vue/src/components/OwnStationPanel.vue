<script setup lang="ts">
import { ref } from 'vue'
import { useRadiosStore } from '@/stores/radiosStore'
import BeaconHistoryModal from './BeaconHistoryModal.vue'
import { useTick } from '@/composables/useTick'
import { beaconNow } from '@/api/radiosApi'

const radiosStore = useRadiosStore()

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
const beaconError = ref<Record<string, string>>({})

async function doBeaconNow(radioId: string) {
  beaconing.value[radioId] = true
  beaconError.value[radioId] = ''
  try {
    await beaconNow(radioId)
  } catch {
    beaconError.value[radioId] = 'Failed'
    setTimeout(() => { beaconError.value[radioId] = '' }, 3000)
  } finally {
    beaconing.value[radioId] = false
  }
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function secondsAgo(radioId: string): number | null {
  const lb = radiosStore.getLastBeaconForRadio(radioId)
  if (!lb?.beaconedAt) return null
  return Math.max(0, Math.floor((now.value - new Date(lb.beaconedAt).getTime()) / 1000))
}

function formatSecondsAgo(secs: number | null): string {
  if (secs === null) return 'Never'
  if (secs < 60) return `${secs}s ago`
  if (secs < 3600) return `${Math.floor(secs / 60)}m ${secs % 60}s ago`
  return `${Math.floor(secs / 3600)}h ${Math.floor((secs % 3600) / 60)}m ago`
}

function dotColor(radioId: string, expectedIntervalSeconds: number): string {
  const lb = radiosStore.getLastBeaconForRadio(radioId)
  const secs = secondsAgo(radioId)
  if (secs === null) return 'grey'
  if (lb && !lb.heard) return 'yellow'
  if (secs <= expectedIntervalSeconds) return 'green'
  if (secs <= expectedIntervalSeconds * 1.5) return 'amber'
  return 'red'
}

function recentConfirmations(radioId: string) {
  const lb = radiosStore.getLastBeaconForRadio(radioId)
  return (lb?.confirmations ?? []).slice(0, 3)
}
</script>

<template>
  <div v-if="radiosStore.activeRadios.length > 0" class="own-station-panel">
    <div
      v-for="radio in radiosStore.activeRadios"
      :key="radio.id"
      class="own-station-card"
      @click="openHistory(radio.id)"
    >
      <div class="d-flex align-center ga-2">
        <v-icon :color="dotColor(radio.id, radio.expectedIntervalSeconds)" size="10">
          mdi-circle
        </v-icon>
        <span class="text-caption font-weight-bold">{{ radio.fullCallsign }}</span>
        <span class="text-caption text-medium-emphasis">{{ radio.name }}</span>
      </div>
      <div class="text-caption mt-1">
        Last beacon: {{ formatSecondsAgo(secondsAgo(radio.id)) }}
        <span
          v-if="radiosStore.getLastBeaconForRadio(radio.id) && !radiosStore.getLastBeaconForRadio(radio.id)!.heard"
          class="text-yellow font-weight-medium"
        >
          — awaiting confirmation
        </span>
      </div>
      <div v-if="recentConfirmations(radio.id).length > 0" class="d-flex flex-wrap ga-1 mt-1">
        <span
          v-for="conf in recentConfirmations(radio.id)"
          :key="conf.digipeater"
          class="text-caption text-success"
        >
          ✓ {{ conf.digipeater }} ({{ conf.secondsAfterBeacon }}s)
        </span>
      </div>
      <div class="d-flex align-center mt-2">
        <v-btn
          size="x-small"
          variant="tonal"
          color="primary"
          prepend-icon="mdi-access-point"
          :loading="beaconing[radio.id]"
          @click.stop="doBeaconNow(radio.id)"
        >
          Beacon Now
        </v-btn>
        <span v-if="beaconError[radio.id]" class="text-caption text-error ml-2">
          {{ beaconError[radio.id] }}
        </span>
      </div>
    </div>
  </div>

  <BeaconHistoryModal
    v-if="historyRadioId"
    v-model="historyOpen"
    :radio-id="historyRadioId"
    :radio-name="radiosStore.radios.find(r => r.id === historyRadioId)?.name ?? historyRadioId"
  />
</template>

<style scoped>
.own-station-panel {
  position: absolute;
  bottom: 80px;
  left: 10px;
  z-index: 1000;
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-width: 260px;
  pointer-events: auto;
}

.own-station-card {
  background: rgba(var(--v-theme-surface), 0.92);
  border: 1px solid rgba(var(--v-border-color), 0.4);
  border-radius: 6px;
  padding: 8px 10px;
  cursor: pointer;
  backdrop-filter: blur(4px);
  transition: background 0.15s;
  user-select: none;
}

.own-station-card:hover {
  background: rgba(var(--v-theme-surface), 1);
}
</style>
