<script setup lang="ts">
import { computed, ref } from 'vue'
import { useDisplay } from 'vuetify'
import { useHealthStore } from '@/stores/health'
import StatusPanel from './StatusPanel.vue'
import { levelColor } from './healthUi'

/**
 * Worst-of summary plus the four lanes (LIVE / RF / IS / TX). Below 700 px it
 * collapses to the summary. Tap or click opens the status panel: a menu on
 * desktop, a bottom sheet on a phone. `compact` renders the summary only (the
 * kiosk chip).
 */
const props = defineProps<{ compact?: boolean }>()
// Fragment root (button + overlay): classes from the parent go on the button.
defineOptions({ inheritAttrs: false })

const healthStore = useHealthStore()
const { width } = useDisplay()
const open = ref(false)
const btn = ref<HTMLElement | null>(null)

const h = computed(() => healthStore.health)
const narrow = computed(() => props.compact || width.value < 700)
const phone = computed(() => width.value < 600)

const lanes = computed(() => [
  { id: 'link', lane: h.value.lanes.link, optional: true },
  { id: 'rf', lane: h.value.lanes.rf, optional: false },
  { id: 'is', lane: h.value.lanes.is, optional: true },
  { id: 'tx', lane: h.value.lanes.tx, optional: true },
])

const pulse = (lv: string) => lv === 'warn' || lv === 'err'
</script>

<template>
  <button
    ref="btn"
    v-bind="$attrs"
    type="button"
    class="status-strip"
    :class="{ 'status-strip--narrow': narrow }"
    :aria-label="`System status: ${h.label}`"
    aria-haspopup="dialog"
    :aria-expanded="open"
    @click="phone && (open = !open)"
  >
    <span class="ss-seg ss-sum" :class="`text-${levelColor(h.worst)}`">
      <span
        class="ss-dot"
        :class="[`bg-${levelColor(h.worst)}`, { 'ss-pulse': h.worst !== 'ok' }]"
      />
      <span class="ss-label">{{ h.label }}</span>
    </span>
    <template v-if="!narrow">
      <span
        v-for="l in lanes"
        :key="l.id"
        class="ss-seg"
        :class="{ 'ss-opt': l.optional, 'ss-stale': l.lane.stale }"
      >
        <span
          class="ss-dot"
          :class="[`bg-${levelColor(l.lane.level)}`, { 'ss-pulse': pulse(l.lane.level) }]"
        />
        <span class="ss-k">{{ l.lane.key }}</span>
        <span class="ss-v">{{ l.lane.value }}</span>
      </span>
    </template>
  </button>
  <v-bottom-sheet v-if="phone" v-model="open">
    <StatusPanel @close="open = false" />
  </v-bottom-sheet>
  <v-menu
    v-else
    v-model="open"
    :activator="btn ?? undefined"
    location="bottom end"
    :offset="6"
    :close-on-content-click="false"
  >
    <StatusPanel @close="open = false" />
  </v-menu>
</template>

<style scoped>
.status-strip {
  display: inline-flex;
  align-items: stretch;
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 999px;
  background: rgb(var(--v-theme-surface));
  color: rgb(var(--v-theme-on-surface));
  font: inherit;
  font-size: 0.8125rem;
  min-height: 32px;
  max-width: 100%;
  overflow: hidden;
  cursor: pointer;
  padding: 0;
}
.status-strip:focus-visible {
  outline: 2px solid rgb(var(--v-theme-primary));
  outline-offset: 2px;
}
.ss-seg {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 0 12px;
  white-space: nowrap;
  border-left: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}
.ss-seg:first-child {
  border-left: 0;
}
.ss-sum {
  font-weight: 600;
}
.ss-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: 0 0 auto;
}
.ss-pulse {
  animation: ss-pulse 1.6s ease-in-out infinite;
}
@keyframes ss-pulse {
  50% {
    opacity: 0.35;
  }
}
@media (prefers-reduced-motion: reduce) {
  .ss-pulse {
    animation: none;
  }
}
.ss-k {
  font-size: 0.6875rem;
  font-weight: 700;
  letter-spacing: 0.04em;
  color: rgba(var(--v-theme-on-surface), var(--v-medium-emphasis-opacity));
}
.ss-v {
  font-variant-numeric: tabular-nums;
}
.ss-stale .ss-v {
  opacity: 0.55;
}
/* Mid widths: keep summary and RF, drop the optional lanes first. */
@media (max-width: 1100px) {
  .ss-opt {
    display: none;
  }
}
</style>
