<script setup lang="ts">
import { ref, watch } from 'vue'
import { useUnits } from '@/composables/useUnits'

const expanded = ref(true)
const { distanceUnit } = useUnits()
const showRings = defineModel<boolean>('showRings', { required: true })
// distances are always stored internally in km
const distances = defineModel<number[]>('distances', { required: true })

// Convert internal km value to the current display unit for editing
function toDisplay(km: number): string {
  return distanceUnit.value === 'mi'
    ? (km * 0.621371).toFixed(1)
    : km.toFixed(1)
}

// Editable string versions of distances in the current display unit
const editValues = ref<string[]>(distances.value.map(toDisplay))

watch(
  () => distances.value,
  (vals) => {
    editValues.value = vals.map(toDisplay)
  },
)

// When the user switches units, refresh the displayed values without changing stored km values
watch(distanceUnit, () => {
  editValues.value = distances.value.map(toDisplay)
})

function commitEdit(index: number) {
  const v = parseFloat(editValues.value[index] ?? '')
  if (!isNaN(v) && v > 0) {
    const km = distanceUnit.value === 'mi' ? v / 0.621371 : v
    const next = [...distances.value]
    next[index] = km
    distances.value = next.sort((a, b) => a - b)
  } else {
    editValues.value[index] = toDisplay(distances.value[index]!)
  }
}

function addRing() {
  const last = distances.value[distances.value.length - 1] ?? 40
  distances.value = [...distances.value, last + 40].sort((a, b) => a - b)
}

function removeRing(index: number) {
  distances.value = distances.value.filter((_, i) => i !== index)
}
</script>

<template>
  <div class="range-rings-panel">
    <div class="rr-header" @click="expanded = !expanded">
      <v-icon size="16" class="rr-icon">mdi-radius-outline</v-icon>
      <span class="rr-title">Range Rings</span>
      <v-icon size="16" class="rr-chevron">
        {{ expanded ? 'mdi-chevron-up' : 'mdi-chevron-down' }}
      </v-icon>
    </div>

    <div v-if="expanded" class="rr-body">
      <div class="rr-toggle-row">
        <span class="rr-label">Show rings</span>
        <v-switch
          v-model="showRings"
          density="compact"
          hide-details
          color="primary"
          class="rr-switch"
        />
      </div>

      <div v-for="(dist, i) in distances" :key="i" class="rr-row">
        <v-text-field
          v-model="editValues[i]"
          density="compact"
          variant="outlined"
          hide-details
          :suffix="distanceUnit"
          class="rr-input"
          @blur="commitEdit(i)"
          @keydown.enter="commitEdit(i)"
        />
        <v-btn
          icon="mdi-close"
          size="x-small"
          variant="text"
          :disabled="distances.length <= 1"
          @click="removeRing(i)"
        />
      </div>

      <v-btn
        size="x-small"
        variant="text"
        prepend-icon="mdi-plus"
        class="rr-add-btn"
        @click="addRing"
      >
        Add ring
      </v-btn>
    </div>
  </div>
</template>

<style scoped>
.range-rings-panel {
  background: rgba(30, 30, 40, 0.88);
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 6px;
  min-width: 160px;
  max-width: 200px;
  backdrop-filter: blur(4px);
  color: #e0e0e0;
  font-size: 12px;
  user-select: none;
}

.rr-header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  cursor: pointer;
  border-radius: 6px 6px 0 0;
}

.rr-header:hover {
  background: rgba(255, 255, 255, 0.07);
}

.rr-icon {
  opacity: 0.75;
}

.rr-title {
  flex: 1;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.5px;
  text-transform: uppercase;
  opacity: 0.85;
}

.rr-chevron {
  opacity: 0.6;
}

.rr-body {
  padding: 4px 8px 8px;
  border-top: 1px solid rgba(255, 255, 255, 0.08);
}

.rr-toggle-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 4px;
}

.rr-label {
  font-size: 11px;
  opacity: 0.8;
}

.rr-switch {
  flex-shrink: 0;
}

.rr-row {
  display: flex;
  align-items: center;
  gap: 2px;
  margin-bottom: 2px;
}

.rr-input {
  font-size: 11px;
}

.rr-add-btn {
  margin-top: 4px;
  font-size: 10px;
  opacity: 0.75;
}
</style>
