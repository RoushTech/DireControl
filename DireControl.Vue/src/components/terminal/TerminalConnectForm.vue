<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRadiosStore } from '@/stores/radiosStore'
import {
  listPresets,
  type OpenTerminalSessionRequest,
  type TerminalPresetDto,
} from '@/api/terminalApi'

const props = withDefaults(
  defineProps<{
    loading?: boolean
    error?: string | null
    pmsLoading?: boolean
  }>(),
  {
    loading: false,
    error: null,
    pmsLoading: false,
  },
)

const emit = defineEmits<{
  (e: 'open', request: OpenTerminalSessionRequest): void
  (e: 'open-pms'): void
}>()

const radiosStore = useRadiosStore()

// ── Form state ───────────────────────────────────────────────────────────────
const selectedRadioId = ref<string | null>(null)
const remoteCallsign = ref('')
const digiPath = ref('')
const localCallsign = ref('')
const advancedOpen = ref<number[]>([])
const mod128 = ref(false)
const pacLen = ref<number | null>(null)
const windowSize = ref<number | null>(null)
const maxRetries = ref<number | null>(null)
const t1Seconds = ref<number | null>(null)

const radioItems = computed(() =>
  radiosStore.activeRadios.map((r) => ({
    title: `${r.name} — ch ${r.channelNumber} (${r.fullCallsign})`,
    value: r.id,
  })),
)

const selectedRadio = computed(
  () => radiosStore.radios.find((r) => r.id === selectedRadioId.value) ?? null,
)

const CALLSIGN_REGEX = /^[A-Z0-9]{1,6}(-(\d|1[0-5]))?$/i

const remoteCallsignError = computed(() => {
  const cs = remoteCallsign.value.trim()
  if (!cs) return ''
  return CALLSIGN_REGEX.test(cs) ? '' : 'Use BASE or BASE-SSID, e.g. W3UWU-1'
})

const canConnect = computed(
  () =>
    selectedRadio.value !== null &&
    remoteCallsign.value.trim().length > 0 &&
    !remoteCallsignError.value,
)

// ── Presets ──────────────────────────────────────────────────────────────────
const presets = ref<TerminalPresetDto[]>([])
const selectedPresetId = ref<number | null>(null)

const presetItems = computed(() =>
  [...presets.value]
    .sort((a, b) => {
      if (a.isPinned !== b.isPinned) return a.isPinned ? -1 : 1
      return (b.lastUsedAt ?? '').localeCompare(a.lastUsedAt ?? '')
    })
    .map((p) => ({
      title: `${p.isPinned ? '★ ' : ''}${p.name?.trim() || p.remoteCallsign}`,
      value: p.id,
    })),
)

function presetSubtitle(id: unknown): string {
  const preset = presets.value.find((p) => p.id === id)
  if (!preset) return ''
  return `${preset.remoteCallsign} · ch ${preset.channel}${preset.digiPath ? ` via ${preset.digiPath}` : ''}`
}

function applyPreset(id: number | null) {
  const preset = presets.value.find((p) => p.id === id)
  if (!preset) return
  remoteCallsign.value = preset.remoteCallsign
  digiPath.value = preset.digiPath ?? ''
  localCallsign.value = preset.localCallsign ?? ''
  const radio = radiosStore.radios.find((r) => r.channelNumber === preset.channel)
  if (radio) selectedRadioId.value = radio.id
  mod128.value = preset.mod128 ?? false
  pacLen.value = preset.pacLen
  windowSize.value = preset.windowSize
  maxRetries.value = preset.maxRetries
  t1Seconds.value = preset.t1Seconds
  if (preset.mod128 !== null || preset.pacLen !== null || preset.windowSize !== null) {
    advancedOpen.value = [0]
  }
}

// ── Connect ──────────────────────────────────────────────────────────────────

/** Blank number inputs mean "use the backend default" — omit them entirely. */
function num(value: number | string | null): number | undefined {
  if (value === null || value === '') return undefined
  const n = Number(value)
  return Number.isFinite(n) ? n : undefined
}

function connect() {
  const radio = selectedRadio.value
  if (!radio || !canConnect.value) return
  const request: OpenTerminalSessionRequest = {
    channel: radio.channelNumber,
    remoteCallsign: remoteCallsign.value.trim().toUpperCase(),
  }
  const local = localCallsign.value.trim().toUpperCase()
  if (local) request.localCallsign = local
  const path = digiPath.value.trim().toUpperCase()
  if (path) request.digiPath = path
  if (mod128.value) request.mod128 = true
  const pl = num(pacLen.value)
  if (pl !== undefined) request.pacLen = pl
  const ws = num(windowSize.value)
  if (ws !== undefined) request.windowSize = ws
  const mr = num(maxRetries.value)
  if (mr !== undefined) request.maxRetries = mr
  const t1 = num(t1Seconds.value)
  if (t1 !== undefined) request.t1Seconds = t1
  emit('open', request)
}

onMounted(async () => {
  if (radiosStore.radios.length === 0) {
    try {
      await radiosStore.fetchRadios()
    } catch {
      /* select stays empty; the hint below explains */
    }
  }
  if (selectedRadioId.value === null && radiosStore.activeRadios.length > 0) {
    selectedRadioId.value = radiosStore.activeRadios[0]!.id
  }
  try {
    presets.value = await listPresets()
  } catch {
    /* presets are a convenience — the form works without them */
  }
})
</script>

<template>
  <v-card variant="outlined" class="connect-form pa-4" max-width="480">
    <div class="text-h6 mb-1">New Connection</div>
    <div class="text-caption text-medium-emphasis mb-4">
      Open a connected-mode (AX.25) session to a remote station, node, or BBS.
    </div>

    <v-select
      v-if="presetItems.length > 0"
      v-model="selectedPresetId"
      :items="presetItems"
      label="Preset"
      clearable
      hide-details
      class="mb-3"
      prepend-inner-icon="mdi-star-outline"
      @update:model-value="applyPreset"
    >
      <template #item="{ item, props: itemProps }">
        <v-list-item v-bind="itemProps" :subtitle="presetSubtitle(item.value)" />
      </template>
    </v-select>

    <v-select
      v-model="selectedRadioId"
      :items="radioItems"
      label="Radio / channel"
      :hint="selectedRadio ? `Local callsign defaults to ${selectedRadio.fullCallsign}` : undefined"
      persistent-hint
      :no-data-text="'No active radios — add one under Settings → Radios'"
      class="mb-3"
    />

    <v-text-field
      v-model="remoteCallsign"
      label="Remote callsign"
      placeholder="e.g. W3UWU-1"
      class="callsign-input mb-3"
      :error-messages="remoteCallsignError || undefined"
      hide-details="auto"
      @keydown.enter="connect"
    />

    <v-text-field
      v-model="digiPath"
      label="Digi path (optional)"
      placeholder="e.g. WIDE1-1 or a node alias"
      hide-details
      class="callsign-input mb-3"
    />

    <v-expansion-panels v-model="advancedOpen" class="mb-3">
      <v-expansion-panel elevation="0" class="advanced-panel">
        <v-expansion-panel-title class="text-body-2">Advanced</v-expansion-panel-title>
        <v-expansion-panel-text>
          <v-text-field
            v-model="localCallsign"
            label="Local callsign override"
            :placeholder="selectedRadio?.fullCallsign ?? 'radio default'"
            hide-details
            class="callsign-input mb-3"
          />
          <v-switch
            v-model="mod128"
            label="Modulo-128 (EAX.25 extended sequence numbers)"
            color="primary"
            density="compact"
            hide-details
            class="mb-2"
          />
          <div class="text-caption text-medium-emphasis mb-2">
            Blank fields use the defaults from Settings → Packet.
          </div>
          <div class="d-flex flex-wrap ga-3">
            <v-text-field
              v-model.number="pacLen"
              label="PACLEN"
              type="number"
              min="1"
              hide-details
              style="max-width: 110px"
            />
            <v-text-field
              v-model.number="windowSize"
              label="Window"
              type="number"
              min="1"
              hide-details
              style="max-width: 110px"
            />
            <v-text-field
              v-model.number="maxRetries"
              label="Retries"
              type="number"
              min="1"
              hide-details
              style="max-width: 110px"
            />
            <v-text-field
              v-model.number="t1Seconds"
              label="T1 (s)"
              type="number"
              min="1"
              hide-details
              style="max-width: 110px"
            />
          </div>
        </v-expansion-panel-text>
      </v-expansion-panel>
    </v-expansion-panels>

    <v-alert v-if="props.error" type="error" variant="tonal" density="compact" class="mb-3">
      {{ props.error }}
    </v-alert>

    <div class="d-flex align-center ga-2">
      <v-btn
        color="primary"
        prepend-icon="mdi-lan-connect"
        :loading="props.loading"
        :disabled="!canConnect"
        @click="connect"
      >
        Connect
      </v-btn>
      <v-btn
        variant="tonal"
        prepend-icon="mdi-mailbox-open-outline"
        :loading="props.pmsLoading"
        @click="emit('open-pms')"
      >
        PMS preview
        <v-tooltip activator="parent" location="bottom">
          Open a local session against your own PMS mailbox
        </v-tooltip>
      </v-btn>
    </div>
  </v-card>
</template>

<style scoped>
.callsign-input :deep(input) {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  text-transform: uppercase;
}

.advanced-panel {
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 8px;
}
</style>
