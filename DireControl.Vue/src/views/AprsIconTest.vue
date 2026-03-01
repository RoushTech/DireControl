<script setup lang="ts">
import { createAprsIcon } from '@/utils/aprsIcon'

const SYMBOL_SIZE = 24

// ASCII 33 (!) through 126 (~) = 94 symbol codes
const symbolCodes: string[] = []
for (let i = 33; i <= 126; i++) {
  symbolCodes.push(String.fromCharCode(i))
}

// Sample overlay characters for the alternate table demo
const overlayChars = ['0', '1', '5', '9', 'A', 'E', 'L', 'S', 'Z']

function iconHtml(table: string, code: string, heading?: number | null): string {
  const icon = createAprsIcon(table, code, heading)
  return icon.options.html as string
}
</script>

<template>
  <v-container fluid>
    <v-row>
      <v-col cols="12">
        <h2 class="text-h5 mb-2">APRS Icon Test Grid</h2>
        <p class="text-body-2 text-medium-emphasis mb-4">
          Visual verification of sprite mapping for all APRS symbol positions.
        </p>
      </v-col>
    </v-row>

    <!-- Primary Table (/) -->
    <v-row>
      <v-col cols="12">
        <h3 class="text-h6 mb-2">Primary Table ( / )</h3>
        <div class="icon-grid">
          <div
            v-for="code in symbolCodes"
            :key="'primary-' + code"
            class="icon-cell"
            :title="'/ ' + code + ' (0x' + code.charCodeAt(0).toString(16) + ')'"
          >
            <div class="icon-wrapper" v-html="iconHtml('/', code)" />
            <span class="icon-label">{{ code }}</span>
          </div>
        </div>
      </v-col>
    </v-row>

    <!-- Alternate Table (\) -->
    <v-row>
      <v-col cols="12">
        <h3 class="text-h6 mb-2 mt-4">Alternate Table ( \ )</h3>
        <div class="icon-grid">
          <div
            v-for="code in symbolCodes"
            :key="'alt-' + code"
            class="icon-cell"
            :title="'\\ ' + code + ' (0x' + code.charCodeAt(0).toString(16) + ')'"
          >
            <div class="icon-wrapper" v-html="iconHtml('\\', code)" />
            <span class="icon-label">{{ code }}</span>
          </div>
        </div>
      </v-col>
    </v-row>

    <!-- Alternate Table with Overlays -->
    <v-row>
      <v-col cols="12">
        <h3 class="text-h6 mb-2 mt-4">Alternate Table with Overlay Characters</h3>
        <div v-for="overlay in overlayChars" :key="'overlay-' + overlay" class="mb-4">
          <h4 class="text-subtitle-1 mb-1">Overlay: {{ overlay }}</h4>
          <div class="icon-grid">
            <div
              v-for="code in symbolCodes.slice(0, 16)"
              :key="'overlay-' + overlay + '-' + code"
              class="icon-cell"
              :title="overlay + ' ' + code"
            >
              <div class="icon-wrapper" v-html="iconHtml(overlay, code)" />
              <span class="icon-label">{{ code }}</span>
            </div>
          </div>
        </div>
      </v-col>
    </v-row>

    <!-- Heading Rotation Demo -->
    <v-row>
      <v-col cols="12">
        <h3 class="text-h6 mb-2 mt-4">Heading Rotation (Mobile Station)</h3>
        <div class="icon-grid">
          <div
            v-for="deg in [0, 45, 90, 135, 180, 225, 270, 315]"
            :key="'heading-' + deg"
            class="icon-cell"
            :title="deg + '°'"
          >
            <div class="icon-wrapper" v-html="iconHtml('/', '>', deg)" />
            <span class="icon-label">{{ deg }}°</span>
          </div>
        </div>
      </v-col>
    </v-row>
  </v-container>
</template>

<style scoped>
.icon-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.icon-cell {
  display: flex;
  flex-direction: column;
  align-items: center;
  width: 40px;
  padding: 4px 2px;
  border: 1px solid rgba(128, 128, 128, 0.2);
  border-radius: 4px;
}

.icon-wrapper {
  width: v-bind('SYMBOL_SIZE + "px"');
  height: v-bind('SYMBOL_SIZE + "px"');
  display: flex;
  align-items: center;
  justify-content: center;
}

.icon-label {
  font-size: 10px;
  font-family: monospace;
  margin-top: 2px;
  color: rgba(255, 255, 255, 0.6);
}
</style>

<style>
.icon-wrapper .aprs-icon-container {
  background: transparent !important;
  border: none !important;
}
</style>
