<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { getSettings } from '@/api/stationsApi'
import { getRadios } from '@/api/radiosApi'
import type { SettingsDto } from '@/types/station'
import type { RadioDto } from '@/types/radio'

/**
 * First-run guidance: what still needs setting up before DireControl is a
 * working station, each step with a button straight to its settings tab.
 * Derived live from settings, radios and APRS-IS state; dismissible.
 */
const props = defineProps<{ aprsIsState: string }>()

const DISMISS_KEY = 'direcontrol.setupChecklistDismissed'

function readDismissed(): boolean {
  try {
    return localStorage.getItem(DISMISS_KEY) === '1'
  } catch {
    return false
  }
}

const dismissed = ref(readDismissed())
const settings = ref<SettingsDto | null>(null)
const radios = ref<RadioDto[] | null>(null)

async function load() {
  try {
    ;[settings.value, radios.value] = await Promise.all([getSettings(), getRadios()])
  } catch {
    // Unreachable backend is already announced by the offline banner.
  }
}

// Re-check after the operator leaves Settings, so ticked steps disappear.
const route = useRoute()
watch(
  () => route.path,
  (path, prev) => {
    if (prev?.startsWith('/settings')) void load()
  },
)
onMounted(load)

interface Step {
  key: string
  label: string
  done: boolean
  to: string
  action: string
}

const steps = computed<Step[]>(() => {
  const s = settings.value
  const r = radios.value ?? []
  const audioRadio = r.find((x) => x.isActive && x.modem.modemEnabled)
  return [
    {
      key: 'callsign',
      label: 'Set your callsign',
      done: !!s && !!s.ourCallsign && !s.ourCallsign.toUpperCase().startsWith('N0CALL'),
      to: '/settings?tab=station',
      action: 'Station',
    },
    {
      key: 'home',
      label: 'Set your home position',
      done: !!s?.homePosition,
      to: '/settings?tab=station',
      action: 'Station',
    },
    {
      key: 'radio',
      label: 'Add a radio with sound-card audio',
      done: !!audioRadio,
      to: '/settings?tab=radios',
      action: 'Radios',
    },
    {
      key: 'ptt',
      label: 'Enable TX and test PTT with a test tone',
      done: !!audioRadio?.modem.txEnabled,
      to: '/settings?tab=radios',
      action: 'Radios',
    },
    {
      key: 'aprsis',
      label: 'Connect to APRS-IS (optional)',
      done: props.aprsIsState === 'Connected',
      to: '/settings?tab=aprsis',
      action: 'APRS-IS',
    },
  ]
})

const remaining = computed(() => steps.value.filter((s) => !s.done))
const visible = computed(
  () => !dismissed.value && settings.value !== null && remaining.value.length > 0,
)

function dismiss() {
  dismissed.value = true
  try {
    localStorage.setItem(DISMISS_KEY, '1')
  } catch {
    // Private window etc. — dismissal lasts for this session only.
  }
}
</script>

<template>
  <v-banner v-if="visible" color="info" density="compact" icon="mdi-clipboard-check-outline">
    <v-banner-text>
      <div class="text-body-2 font-weight-medium mb-1">
        Finish setting up — {{ steps.length - remaining.length }} of {{ steps.length }} done
      </div>
      <div class="d-flex flex-wrap ga-2">
        <v-chip
          v-for="step in steps"
          :key="step.key"
          :color="step.done ? 'success' : undefined"
          :prepend-icon="step.done ? 'mdi-check' : 'mdi-circle-outline'"
          :to="step.done ? undefined : step.to"
          size="small"
          variant="tonal"
        >
          {{ step.label }}
        </v-chip>
      </div>
    </v-banner-text>
    <template #actions>
      <v-btn variant="text" :to="remaining[0]?.to">{{ remaining[0]?.action }}</v-btn>
      <v-btn variant="text" @click="dismiss">Dismiss</v-btn>
    </template>
  </v-banner>
</template>
