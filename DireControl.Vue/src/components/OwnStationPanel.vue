<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { useRadiosStore } from '@/stores/radiosStore'
import BeaconHistoryModal from './BeaconHistoryModal.vue'

const radiosStore = useRadiosStore()

// Tick every second so the "X ago" counters update live
const now = ref(Date.now())
let ticker: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  ticker = setInterval(() => { now.value = Date.now() }, 1000)
})

onUnmounted(() => {
  if (ticker !== null) clearInterval(ticker)
})

// ─── Modal state ──────────────────────────────────────────────────────────────
const historyOpen = ref(false)
const historyRadioId = ref('')

function openHistory(radioId: string) {
  historyRadioId.value = radioId
  historyOpen.value = true
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
  const secs = secondsAgo(radioId)
  if (secs === null) return 'grey'
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
