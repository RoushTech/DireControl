<script setup lang="ts">
import { ref, watch } from 'vue'
import { getBeaconHistory } from '@/api/radiosApi'
import type { OwnBeaconHistoryItemDto } from '@/types/radio'

const props = defineProps<{
  radioId: string
  radioName: string
}>()

const open = defineModel<boolean>()

const beacons = ref<OwnBeaconHistoryItemDto[]>([])
const loading = ref(false)
const expanded = ref<number[]>([])

watch(open, async (val) => {
  if (val) {
    loading.value = true
    expanded.value = []
    try {
      beacons.value = await getBeaconHistory(props.radioId, 20)
    } finally {
      loading.value = false
    }
  }
})

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

function formatCoords(lat: number | null, lon: number | null): string {
  if (lat === null || lon === null) return '—'
  return `${lat.toFixed(4)}, ${lon.toFixed(4)}`
}

function isExpanded(id: number): boolean {
  return expanded.value.includes(id)
}

function toggleExpand(id: number) {
  const idx = expanded.value.indexOf(id)
  if (idx === -1) expanded.value.push(id)
  else expanded.value.splice(idx, 1)
}

function hopLabel(hopCount: number): string {
  if (hopCount === -1) return 'Placeholder'
  if (hopCount === 0) return 'Direct'
  return `${hopCount} hop${hopCount > 1 ? 's' : ''}`
}
</script>

<template>
  <v-dialog :model-value="open" @update:model-value="open = $event" max-width="800">
    <v-card>
      <v-card-title class="d-flex align-center">
        <v-icon class="mr-2" size="20">mdi-radio-tower</v-icon>
        Beacon History — {{ radioName }}
        <v-spacer />
        <v-btn icon="mdi-close" variant="text" size="small" @click="open = false" />
      </v-card-title>

      <v-divider />

      <v-card-text style="max-height: 70vh; overflow-y: auto">
        <div v-if="loading" class="d-flex justify-center py-6">
          <v-progress-circular indeterminate />
        </div>

        <div v-else-if="beacons.length === 0" class="text-center text-medium-emphasis py-6">
          No beacon records found.
        </div>

        <v-table v-else density="compact">
          <thead>
            <tr>
              <th style="width: 28px"></th>
              <th>Time</th>
              <th>Coordinates</th>
              <th>Path</th>
              <th>Type</th>
              <th>Confirmations</th>
            </tr>
          </thead>
          <tbody>
            <template v-for="beacon in beacons" :key="beacon.id">
              <tr class="cursor-pointer" @click="toggleExpand(beacon.id)">
                <td>
                  <v-icon size="14" :class="isExpanded(beacon.id) ? 'text-primary' : 'text-medium-emphasis'">
                    {{ isExpanded(beacon.id) ? 'mdi-chevron-down' : 'mdi-chevron-right' }}
                  </v-icon>
                </td>
                <td class="text-caption">{{ formatTime(beacon.beaconedAt) }}</td>
                <td class="text-caption">{{ formatCoords(beacon.latitude, beacon.longitude) }}</td>
                <td class="text-caption">{{ beacon.pathUsed ?? '—' }}</td>
                <td>
                  <v-chip
                    size="x-small"
                    :color="beacon.hopCount === 0 ? 'blue' : beacon.hopCount === -1 ? 'grey' : 'orange'"
                    variant="tonal"
                  >
                    {{ hopLabel(beacon.hopCount) }}
                  </v-chip>
                </td>
                <td class="text-caption">
                  <span v-if="beacon.confirmations.length === 0" class="text-medium-emphasis">
                    No confirmations
                  </span>
                  <span v-else>
                    {{ beacon.confirmations.map(c => `${c.digipeater} (${c.secondsAfterBeacon}s)`).join(', ') }}
                  </span>
                </td>
              </tr>
              <tr v-if="isExpanded(beacon.id)">
                <td colspan="6" class="pa-0">
                  <div class="pa-3 bg-surface-variant">
                    <div v-if="beacon.confirmations.length === 0" class="text-caption text-medium-emphasis">
                      No confirmations recorded for this beacon.
                    </div>
                    <div v-else>
                      <div class="text-caption font-weight-medium mb-2">Confirmations</div>
                      <v-table density="compact">
                        <thead>
                          <tr>
                            <th>Digipeater</th>
                            <th>Confirmed At</th>
                            <th>+Seconds</th>
                            <th>Alias</th>
                            <th>Coordinates</th>
                          </tr>
                        </thead>
                        <tbody>
                          <tr v-for="conf in beacon.confirmations" :key="conf.digipeater + conf.confirmedAt">
                            <td class="text-caption font-weight-medium">{{ conf.digipeater }}</td>
                            <td class="text-caption">{{ formatTime(conf.confirmedAt) }}</td>
                            <td class="text-caption">+{{ conf.secondsAfterBeacon }}s</td>
                            <td class="text-caption">{{ conf.aliasUsed ?? '—' }}</td>
                            <td class="text-caption">{{ formatCoords(conf.lat, conf.lon) }}</td>
                          </tr>
                        </tbody>
                      </v-table>
                    </div>
                  </div>
                </td>
              </tr>
            </template>
          </tbody>
        </v-table>
      </v-card-text>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.cursor-pointer {
  cursor: pointer;
}
</style>
