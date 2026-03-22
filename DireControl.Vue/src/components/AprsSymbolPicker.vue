<script setup lang="ts">
import { ref, computed } from 'vue'
import { getSymbolStyle } from '@/utils/aprsIcon'

const model = defineModel<string>({ default: '' })

const pickerOpen = ref(false)
const selectedTable = ref<'/' | '\\'>('/')

// ASCII 33 (!) through 126 (~) = 94 symbol codes
const symbolCodes: string[] = []
for (let i = 33; i <= 126; i++) {
  symbolCodes.push(String.fromCharCode(i))
}

// Common symbols with friendly names for quick reference
const commonSymbols: { table: string; code: string; label: string }[] = [
  { table: '/', code: '-', label: 'House' },
  { table: '/', code: '>', label: 'Car' },
  { table: '/', code: 'k', label: 'Truck' },
  { table: '/', code: 'y', label: 'House (yagi)' },
  { table: '/', code: '^', label: 'Aircraft' },
  { table: '/', code: 'Y', label: 'Sailboat' },
  { table: '/', code: 's', label: 'Boat' },
  { table: '/', code: 'b', label: 'Bicycle' },
  { table: '/', code: 'R', label: 'Rec vehicle' },
  { table: '/', code: 'j', label: 'Jeep' },
  { table: '/', code: 'U', label: 'Bus' },
  { table: '/', code: '`', label: 'Dish antenna' },
  { table: '/', code: 'r', label: 'Antenna' },
  { table: '/', code: '#', label: 'Digipeater' },
  { table: '/', code: '&', label: 'HF gateway' },
  { table: '/', code: 'I', label: 'TCP/IP' },
  { table: '/', code: '_', label: 'Weather stn' },
  { table: '\\', code: '_', label: 'Weather stn (alt)' },
]

const previewStyle = computed(() => {
  if (model.value.length < 2) return null
  return getSymbolStyle(model.value[0]!, model.value[1]!)
})

function selectSymbol(table: string, code: string) {
  model.value = table + code
  pickerOpen.value = false
}
</script>

<template>
  <div>
    <div class="d-flex align-center ga-2">
      <div
        class="symbol-preview"
        :class="{ 'symbol-preview--empty': !previewStyle }"
        style="align-self: start; margin-top: 4px"
        @click="pickerOpen = true"
      >
        <div v-if="previewStyle" :style="previewStyle" class="symbol-sprite" />
        <v-icon v-else size="18" color="medium-emphasis">mdi-help</v-icon>
      </div>
      <v-text-field
        :model-value="model"
        @update:model-value="model = $event"
        label="Symbol"
        density="compact"
        placeholder="e.g. /-"
        hint="Table+code — click the icon to browse"
        persistent-hint
        maxlength="2"
        style="flex: 1"
      />
      <v-btn
        icon="mdi-grid"
        size="small"
        variant="tonal"
        style="flex-shrink: 0; align-self: start; margin-top: 4px"
        @click="pickerOpen = true"
        title="Browse symbols"
      />
    </div>

    <v-dialog v-model="pickerOpen" max-width="640" scrollable>
      <v-card>
        <v-card-title class="d-flex align-center">
          <span>Select APRS Symbol</span>
          <v-spacer />
          <v-btn icon="mdi-close" variant="text" size="small" @click="pickerOpen = false" />
        </v-card-title>
        <v-divider />

        <v-card-text style="max-height: 70vh; overflow-y: auto;">
          <!-- Common symbols -->
          <div class="text-subtitle-2 font-weight-medium mb-2">Common</div>
          <div class="symbol-grid mb-4">
            <div
              v-for="sym in commonSymbols"
              :key="sym.table + sym.code"
              class="symbol-cell"
              :class="{ 'symbol-cell--selected': model === sym.table + sym.code }"
              :title="sym.label + ' (' + sym.table + sym.code + ')'"
              @click="selectSymbol(sym.table, sym.code)"
            >
              <div :style="getSymbolStyle(sym.table, sym.code)" class="symbol-sprite" />
              <span class="symbol-label">{{ sym.label }}</span>
            </div>
          </div>

          <!-- Full grid -->
          <div class="d-flex align-center ga-2 mb-2">
            <div class="text-subtitle-2 font-weight-medium">All Symbols</div>
            <v-btn-toggle v-model="selectedTable" mandatory density="compact" variant="outlined">
              <v-btn value="/" size="small">Primary /</v-btn>
              <v-btn value="\\" size="small">Alternate \</v-btn>
            </v-btn-toggle>
          </div>
          <div class="symbol-grid">
            <div
              v-for="code in symbolCodes"
              :key="selectedTable + code"
              class="symbol-cell symbol-cell--compact"
              :class="{ 'symbol-cell--selected': model === selectedTable + code }"
              :title="selectedTable + code + ' (0x' + code.charCodeAt(0).toString(16) + ')'"
              @click="selectSymbol(selectedTable, code)"
            >
              <div :style="getSymbolStyle(selectedTable, code)" class="symbol-sprite" />
              <span class="symbol-code">{{ code }}</span>
            </div>
          </div>
        </v-card-text>
      </v-card>
    </v-dialog>
  </div>
</template>

<style scoped>
.symbol-preview {
  width: 36px;
  height: 36px;
  border: 1px solid rgba(var(--v-border-color), 0.4);
  border-radius: 6px;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  flex-shrink: 0;
  transition: border-color 0.15s;
}

.symbol-preview:hover {
  border-color: rgb(var(--v-theme-primary));
}

.symbol-preview--empty {
  background: rgba(var(--v-border-color), 0.08);
}

.symbol-sprite {
  image-rendering: pixelated;
}

.symbol-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.symbol-cell {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 6px 4px;
  border: 1px solid rgba(var(--v-border-color), 0.2);
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.1s, border-color 0.1s;
  min-width: 60px;
}

.symbol-cell:hover {
  background: rgba(var(--v-theme-primary), 0.08);
  border-color: rgba(var(--v-theme-primary), 0.4);
}

.symbol-cell--selected {
  background: rgba(var(--v-theme-primary), 0.15);
  border-color: rgb(var(--v-theme-primary));
}

.symbol-cell--compact {
  min-width: 40px;
  padding: 4px 2px;
}

.symbol-label {
  font-size: 10px;
  margin-top: 2px;
  text-align: center;
  line-height: 1.2;
  max-width: 56px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.symbol-code {
  font-size: 10px;
  font-family: monospace;
  margin-top: 2px;
  opacity: 0.6;
}
</style>
