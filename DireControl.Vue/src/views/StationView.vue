<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import StationDetailPanel from '@/components/StationDetailPanel.vue'
import { usePacketHubStore } from '@/stores/packetHub'
import { useStationSelectionStore } from '@/stores/stationSelection'
import type { PacketBroadcastDto } from '@/types/packet'

const route = useRoute()
const router = useRouter()
const hub = usePacketHubStore()
const selectionStore = useStationSelectionStore()

const callsign = computed(() => String(route.params.callsign ?? '').toUpperCase())

// Bump the panel's refresh key when this station transmits, so packets/weather/
// signal tabs stay current — same contract the map uses.
const refreshKey = ref(0)

function onHubPacketReceived(packet: PacketBroadcastDto) {
  if (packet.callsign === callsign.value) refreshKey.value++
}

function showOnMap() {
  selectionStore.selectStation(callsign.value)
  router.push('/')
}

function onHighlightPosition() {
  // Position clicks make sense on the map — jump there with the station selected.
  showOnMap()
}

function onClose() {
  if (window.history.length > 1) router.back()
  else router.push('/')
}

onMounted(() => {
  hub.on('packetReceived', onHubPacketReceived)
})

onUnmounted(() => {
  hub.off('packetReceived', onHubPacketReceived)
})
</script>

<template>
  <div class="station-page">
    <div class="station-page-bar">
      <v-btn icon="mdi-arrow-left" variant="text" size="small" aria-label="Back" @click="onClose" />
      <span class="text-subtitle-1 font-weight-bold">{{ callsign }}</span>
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
    </div>
    <v-divider />
    <div class="station-page-body">
      <StationDetailPanel
        :callsign="callsign"
        :refresh-key="refreshKey"
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

.station-page-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  flex-shrink: 0;
}

.station-page-body {
  flex: 1;
  min-height: 0;
  max-width: 960px;
  width: 100%;
  margin: 0 auto;
}
</style>
