<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { getSettings } from '@/api/stationsApi'
import { updateRfServices, updateExternalTnc } from '@/api/modemApi'
import { apiErrorDetail } from '@/api/axios'
import type { SettingsDto } from '@/types/station'
import { useToastStore } from '@/stores/toastStore'
import SettingsSaveBar from './SettingsSaveBar.vue'
import { settingsSection } from './sections'
import { useFormSnapshot } from './useFormSnapshot'

const meta = settingsSection('rf')
const toastStore = useToastStore()

// ─── RF services (digipeater / KISS server / iGate) ──────────────────────────
const digipeaterEnabled = ref(false)
const digipeaterMaxWideN = ref(2)
const digipeaterFillInOnly = ref(false)
const kissServerEnabled = ref(false)
const kissServerPort = ref(8010)
const kissServerBindAddress = ref('0.0.0.0')
const rfToIsGatingEnabled = ref(false)
const isToRfGatingEnabled = ref(false)
const isToRfPath = ref('')
const isToRfRecentHeardMinutes = ref(30)

function loadRfServicesSettings(s: SettingsDto) {
  digipeaterEnabled.value = s.digipeaterEnabled
  digipeaterMaxWideN.value = s.digipeaterMaxWideN
  digipeaterFillInOnly.value = s.digipeaterFillInOnly
  kissServerEnabled.value = s.kissServerEnabled
  kissServerPort.value = s.kissServerPort
  kissServerBindAddress.value = s.kissServerBindAddress
  rfToIsGatingEnabled.value = s.rfToIsGatingEnabled
  isToRfGatingEnabled.value = s.isToRfGatingEnabled
  isToRfPath.value = s.isToRfPath
  isToRfRecentHeardMinutes.value = s.isToRfRecentHeardMinutes
}

// ─── External TNC (KISS TCP client, e.g. Direwolf) ───────────────────────────
const direwolfEnabled = ref(false)
const direwolfHost = ref('localhost')
const direwolfPort = ref(8001)
const direwolfReconnectDelaySeconds = ref(5)

function loadExternalTncSettings(s: SettingsDto) {
  direwolfEnabled.value = s.direwolfEnabled
  direwolfHost.value = s.direwolfHost
  direwolfPort.value = s.direwolfPort
  direwolfReconnectDelaySeconds.value = s.direwolfReconnectDelaySeconds
}

const rfServices = useFormSnapshot(() => ({
  digipeaterEnabled: digipeaterEnabled.value,
  digipeaterMaxWideN: Number(digipeaterMaxWideN.value),
  digipeaterFillInOnly: digipeaterFillInOnly.value,
  kissServerEnabled: kissServerEnabled.value,
  kissServerPort: Number(kissServerPort.value),
  kissServerBindAddress: (kissServerBindAddress.value ?? '').trim(),
  rfToIsGatingEnabled: rfToIsGatingEnabled.value,
  isToRfGatingEnabled: isToRfGatingEnabled.value,
  isToRfPath: (isToRfPath.value ?? '').trim(),
  isToRfRecentHeardMinutes: Number(isToRfRecentHeardMinutes.value),
}))
const externalTnc = useFormSnapshot(() => ({
  direwolfEnabled: direwolfEnabled.value,
  direwolfHost: (direwolfHost.value ?? '').trim(),
  direwolfPort: Number(direwolfPort.value),
  direwolfReconnectDelaySeconds: Number(direwolfReconnectDelaySeconds.value),
}))
const dirty = computed(() => rfServices.dirty.value || externalTnc.dirty.value)

const loadError = ref('')
const saveError = ref('')
const saving = ref(false)

/** One save for the section; only the parts that changed are sent (each restarts services). */
async function save() {
  saving.value = true
  saveError.value = ''
  const restarted: string[] = []
  try {
    if (rfServices.dirty.value) {
      await updateRfServices({
        digipeaterEnabled: digipeaterEnabled.value,
        digipeaterMaxWideN: digipeaterMaxWideN.value,
        digipeaterFillInOnly: digipeaterFillInOnly.value,
        kissServerEnabled: kissServerEnabled.value,
        kissServerPort: kissServerPort.value,
        kissServerBindAddress: kissServerBindAddress.value.trim(),
        rfToIsGatingEnabled: rfToIsGatingEnabled.value,
        isToRfGatingEnabled: isToRfGatingEnabled.value,
        isToRfPath: (isToRfPath.value ?? '').trim(),
        isToRfRecentHeardMinutes: isToRfRecentHeardMinutes.value,
      })
      rfServices.markClean()
      restarted.push('modem and KISS server restarting; KISS clients dropped')
    }
    if (externalTnc.dirty.value) {
      await updateExternalTnc({
        direwolfEnabled: direwolfEnabled.value,
        direwolfHost: direwolfHost.value.trim(),
        direwolfPort: direwolfPort.value,
        direwolfReconnectDelaySeconds: direwolfReconnectDelaySeconds.value,
      })
      externalTnc.markClean()
      restarted.push('reconnecting to the external TNC')
    }
    toastStore.toast(`RF services saved: ${restarted.join('; ')}…`, 'info')
  } catch (e) {
    saveError.value = `Save failed: ${apiErrorDetail(e)}`
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    const s = await getSettings()
    loadRfServicesSettings(s)
    loadExternalTncSettings(s)
    rfServices.markClean()
    externalTnc.markClean()
  } catch (e) {
    loadError.value = `Couldn't load RF settings: ${apiErrorDetail(e)}`
  }
})
</script>

<template>
  <div class="settings-single">
    <!-- ================================================================ -->
    <!-- RF Services -->
    <!-- ================================================================ -->
    <div class="section-header d-flex align-center mb-2">
      <span class="text-h6">RF Services</span>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <!-- Digipeater -->
      <div class="text-body-2 font-weight-medium mb-1">Digipeater</div>
      <v-switch
        v-model="digipeaterEnabled"
        label="Enable WIDEn-N digipeater"
        hide-details
        density="compact"
      />
      <div v-if="digipeaterEnabled" class="d-flex ga-4 align-center flex-wrap mb-2 mt-1">
        <v-text-field
          v-model.number="digipeaterMaxWideN"
          label="Max WIDEn"
          density="compact"
          type="number"
          style="max-width: 140px"
          hint="Larger n is trapped"
          persistent-hint
        />
        <v-checkbox
          v-model="digipeaterFillInOnly"
          label="Fill-in only (WIDE1-1)"
          hide-details
          density="compact"
        />
      </div>

      <v-divider class="my-4" />

      <!-- KISS server -->
      <div class="text-body-2 font-weight-medium mb-1">KISS TCP Server</div>
      <div class="text-caption text-medium-emphasis mb-1">
        Lets other applications use DireControl as their TNC.
      </div>
      <div class="d-flex ga-4 align-center flex-wrap">
        <v-switch
          v-model="kissServerEnabled"
          label="Enable KISS server"
          hide-details
          density="compact"
        />
        <v-text-field
          v-if="kissServerEnabled"
          v-model.number="kissServerPort"
          label="Port"
          density="compact"
          type="number"
          style="max-width: 140px"
        />
        <v-text-field
          v-if="kissServerEnabled"
          v-model="kissServerBindAddress"
          label="Bind address"
          density="compact"
          hint="KISS has no authentication. 127.0.0.1 keeps it on this host."
          persistent-hint
          style="max-width: 260px"
        />
      </div>

      <v-divider class="my-4" />

      <!-- iGate -->
      <div class="text-body-2 font-weight-medium mb-1">iGate</div>
      <div class="text-caption text-medium-emphasis mb-1">
        Requires APRS-IS to be enabled with a valid passcode.
      </div>
      <v-switch
        v-model="rfToIsGatingEnabled"
        label="Gate RF → APRS-IS (qAR)"
        hide-details
        density="compact"
      />
      <v-switch
        v-model="isToRfGatingEnabled"
        label="Gate APRS-IS messages → RF (third-party format)"
        hide-details
        density="compact"
      />
      <div v-if="isToRfGatingEnabled" class="d-flex ga-4 align-center flex-wrap mb-2 mt-1">
        <v-text-field
          v-model="isToRfPath"
          label="IS→RF path"
          density="compact"
          style="max-width: 240px"
          hint="Empty = direct"
          persistent-hint
        />
        <v-text-field
          v-model.number="isToRfRecentHeardMinutes"
          label="Heard-on-RF window (min)"
          density="compact"
          type="number"
          style="max-width: 200px"
        />
      </div>
    </v-card>

    <!-- ================================================================ -->
    <!-- External TNC (KISS TCP client, e.g. Direwolf) -->
    <!-- ================================================================ -->
    <div class="section-header d-flex align-center mb-2">
      <span class="text-h6">External TNC</span>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <v-switch
        v-model="direwolfEnabled"
        label="Connect to an external KISS TNC (e.g. Direwolf) over TCP"
        hide-details
        density="compact"
      />
      <div class="text-caption text-medium-emphasis mb-2">
        Leave off when using the native sound modem.
      </div>
      <div v-if="direwolfEnabled" class="d-flex ga-4 align-center flex-wrap mt-1">
        <v-text-field
          v-model="direwolfHost"
          label="Host"
          density="compact"
          placeholder="localhost"
          style="max-width: 220px"
        />
        <v-text-field
          v-model.number="direwolfPort"
          label="Port"
          density="compact"
          type="number"
          style="max-width: 140px"
        />
        <v-text-field
          v-model.number="direwolfReconnectDelaySeconds"
          label="Reconnect delay (s)"
          density="compact"
          type="number"
          style="max-width: 180px"
        />
      </div>
    </v-card>

    <v-alert v-if="loadError" type="error" variant="tonal" density="compact" class="mb-2">
      {{ loadError }}
    </v-alert>
    <v-alert v-if="saveError" type="error" variant="tonal" density="compact" class="mb-2">
      {{ saveError }}
    </v-alert>
    <SettingsSaveBar
      :restart="meta.restart"
      :dirty="dirty"
      :saving="saving"
      :disabled="!dirty"
      save-label="Save RF services"
      @save="save"
    />
  </div>
</template>
