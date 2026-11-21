<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRadiosStore } from '@/stores/radiosStore'
import { useToastStore } from '@/stores/toastStore'
import { apiErrorDetail } from '@/api/axios'
import {
  listPresets,
  updatePreset,
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

// A session needs a transmitter: radios without modem TX are listed but
// disabled, so it is clear why they cannot be picked.
const radioItems = computed(() =>
  radiosStore.activeRadios.map((r) => {
    const canTx = r.modem.modemEnabled && r.modem.txEnabled
    return {
      title: `${r.name} — ch ${r.channelNumber} (${r.fullCallsign})${canTx ? '' : ' · RX only'}`,
      value: r.id,
      props: { disabled: !canTx },
    }
  }),
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

// Up to 8 comma-separated hops, each a callsign or alias with optional SSID.
const PATH_REGEX = /^[A-Z0-9]{1,6}(-(\d|1[0-5]))?(,[A-Z0-9]{1,6}(-(\d|1[0-5]))?){0,7}$/i

const digiPathError = computed(() => {
  const path = digiPath.value.trim().replace(/\s+/g, '')
  if (!path) return ''
  return PATH_REGEX.test(path) ? '' : 'Up to 8 comma-separated hops, e.g. WIDE1-1 or K4NODE,K4BBS-1'
})

const canConnect = computed(
  () =>
    selectedRadio.value !== null &&
    selectedRadio.value.modem.modemEnabled &&
    selectedRadio.value.modem.txEnabled &&
    remoteCallsign.value.trim().length > 0 &&
    !remoteCallsignError.value &&
    !digiPathError.value,
)

// ── Presets ──────────────────────────────────────────────────────────────────
const toast = useToastStore()
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

const pinning = ref<number | null>(null)

function isPinned(id: unknown): boolean {
  return presets.value.find((p) => p.id === id)?.isPinned ?? false
}

/** Pin or unpin a preset in place (pruning keeps pinned presets). */
async function togglePin(id: unknown) {
  const p = presets.value.find((x) => x.id === id)
  if (!p) return
  pinning.value = p.id
  try {
    const updated = await updatePreset(p.id, {
      name: p.name ?? undefined,
      remoteCallsign: p.remoteCallsign,
      channel: p.channel,
      digiPath: p.digiPath ?? undefined,
      localCallsign: p.localCallsign ?? undefined,
      isPinned: !p.isPinned,
      mod128: p.mod128 ?? undefined,
      pacLen: p.pacLen ?? undefined,
      windowSize: p.windowSize ?? undefined,
      maxRetries: p.maxRetries ?? undefined,
      t1Seconds: p.t1Seconds ?? undefined,
    })
    presets.value = presets.value.map((x) => (x.id === updated.id ? updated : x))
  } catch (e) {
    toast.toast(apiErrorDetail(e, 'Could not update the preset'), 'error')
  } finally {
    pinning.value = null
  }
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
  const path = digiPath.value.trim().replace(/\s+/g, '').toUpperCase()
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
    <!-- A form, so Enter in any field connects (not just the callsign box). -->
    <v-form @submit.prevent="connect">
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
          <v-list-item v-bind="itemProps" :subtitle="presetSubtitle(item.value)">
            <template #append>
              <!-- Pinned presets sort first and are never pruned. -->
              <v-btn
                :icon="isPinned(item.value) ? 'mdi-pin' : 'mdi-pin-outline'"
                :color="isPinned(item.value) ? 'primary' : undefined"
                :aria-label="isPinned(item.value) ? 'Unpin preset' : 'Pin preset'"
                :aria-pressed="isPinned(item.value)"
                :loading="pinning === item.value"
                variant="text"
                size="small"
                @click.stop="togglePin(item.value)"
              />
            </template>
          </v-list-item>
        </template>
      </v-select>

      <v-select
        v-model="selectedRadioId"
        :items="radioItems"
        label="Radio / channel"
        :hint="
          selectedRadio ? `Local callsign defaults to ${selectedRadio.fullCallsign}` : undefined
        "
        persistent-hint
        class="mb-3"
      >
        <template #no-data>
          <v-list-item to="/settings/radios" title="No active radios — add one" />
        </template>
      </v-select>

      <v-text-field
        v-model="remoteCallsign"
        label="Remote callsign"
        placeholder="e.g. W3UWU-1"
        class="callsign-input mb-3"
        :error-messages="remoteCallsignError || undefined"
        hide-details="auto"
      />

      <v-text-field
        v-model="digiPath"
        label="Digi path (optional)"
        placeholder="e.g. WIDE1-1 or a node alias"
        :error-messages="digiPathError || undefined"
        hide-details="auto"
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
              Blank fields use the
              <v-btn variant="text" size="x-small" density="compact" to="/settings/packet">
                packet defaults
              </v-btn>
              .
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
          type="submit"
          color="primary"
          prepend-icon="mdi-lan-connect"
          :loading="props.loading"
          :disabled="!canConnect"
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
    </v-form>
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
