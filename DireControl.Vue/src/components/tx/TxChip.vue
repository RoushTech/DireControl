<script setup lang="ts">
import { computed } from 'vue'
import { useTxStore } from '@/stores/tx'
import { useUiStore } from '@/stores/uiStore'
import { useHealthStore } from '@/stores/health'

/** App-bar TX chip: ON AIR / Waiting / TX with the active count; opens the queue. */
const tx = useTxStore()
const ui = useUiStore()
const health = useHealthStore()

const stuck = computed(() => health.health.lanes.tx.value === 'PTT stuck')
const onAir = computed(() => tx.onAir || health.health.lanes.tx.onAir || stuck.value)
const label = computed(() =>
  stuck.value ? 'PTT stuck' : onAir.value ? 'ON AIR' : tx.waiting ? 'Waiting' : 'TX',
)
const color = computed(() =>
  onAir.value ? 'error' : tx.waiting ? 'warning' : tx.pending.length ? 'primary' : undefined,
)
</script>

<template>
  <v-btn
    :color="color"
    :variant="onAir || tx.waiting ? 'flat' : 'tonal'"
    size="small"
    prepend-icon="mdi-radio-tower"
    class="tx-chip"
    :aria-label="`Transmit queue: ${label}, ${tx.pending.length} active`"
    @click="ui.txSheetOpen = true"
  >
    {{ label }}
    <v-badge
      v-if="tx.active.length"
      :content="tx.active.length"
      inline
      color="surface-variant"
      class="ml-1"
    />
  </v-btn>
</template>

<style scoped>
.tx-chip {
  letter-spacing: 0.02em;
}
</style>
