<script setup lang="ts">
import { ref } from 'vue'
import { useHealthStore, type HealthBanner } from '@/stores/health'
import type { BannerAction } from '@/stores/healthModel'
import { usePacketHubStore } from '@/stores/packetHub'
import { useRadiosStore } from '@/stores/radiosStore'
import { useToastStore } from '@/stores/toastStore'
import { useUiStore } from '@/stores/uiStore'
import { forceUnkey, restartModem } from '@/api/modemApi'
import { toggleRadioActive } from '@/api/radiosApi'
import { apiErrorDetail } from '@/api/axios'

/**
 * One banner per failure, worst first, each with the action that fixes it.
 * The copy and actions come from deriveBanners() so they match the status
 * strip exactly.
 */
const healthStore = useHealthStore()
const hub = usePacketHubStore()
const radiosStore = useRadiosStore()
const toast = useToastStore()
const ui = useUiStore()

const busy = ref<string | null>(null)
const confirmStop = ref<{ radioId: string; name: string } | null>(null)

const color = (b: HealthBanner) =>
  b.level === 'err' ? 'error' : b.level === 'warn' ? 'warning' : 'info'
const icon = (b: HealthBanner) =>
  b.level === 'err' ? 'mdi-alert-circle' : b.level === 'warn' ? 'mdi-alert' : 'mdi-information'

const key = (b: HealthBanner, a: BannerAction) => `${b.id}:${a.label}`

async function run(b: HealthBanner, a: BannerAction) {
  if (!('run' in a)) return
  if (a.run === 'stopModem') {
    const r = radiosStore.radios.find((x) => x.id === a.radioId)
    confirmStop.value = { radioId: a.radioId!, name: r?.name ?? 'this radio' }
    return
  }
  busy.value = key(b, a)
  try {
    switch (a.run) {
      case 'hubReconnect':
        await hub.reconnectNow()
        break
      case 'restartModem':
        await restartModem()
        toast.toast('Modem restarting…', 'info')
        break
      case 'forceUnkey':
        await forceUnkey(a.radioId!)
        toast.toast('PTT released', 'success')
        await radiosStore.fetchModemStatus()
        break
      case 'isReconnect':
        await healthStore.reconnectAprsIs()
        toast.toast('APRS-IS reconnect requested', 'info')
        break
    }
  } catch (e) {
    toast.toast(apiErrorDetail(e, `${a.label} failed`), 'error', 10_000)
  } finally {
    busy.value = null
  }
}

async function stopModem() {
  const target = confirmStop.value
  if (!target) return
  busy.value = `stop:${target.radioId}`
  try {
    await toggleRadioActive(target.radioId)
    await radiosStore.fetchRadios()
    toast.toast(`${target.name} deactivated — its modem is stopped`, 'warning', 10_000, {
      label: 'Settings',
      to: '/settings/radios',
    })
    confirmStop.value = null
  } catch (e) {
    toast.toast(apiErrorDetail(e, 'Could not stop the modem'), 'error', 10_000)
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <div v-if="healthStore.banners.length" class="health-banners">
    <v-alert
      v-for="b in healthStore.banners"
      :key="b.id"
      :color="color(b)"
      :icon="icon(b)"
      variant="tonal"
      density="compact"
      rounded="0"
      :role="b.level === 'err' ? 'alert' : 'status'"
      class="health-banner"
    >
      <div class="hb-body">
        <span class="hb-text"><strong>{{ b.title }}</strong> {{ b.text }}</span>
        <span v-if="b.actions.length" class="hb-actions">
          <template v-for="a in b.actions" :key="a.label">
            <v-btn v-if="'to' in a" :to="a.to" size="small" variant="tonal" :color="color(b)">
              {{ a.label }}
            </v-btn>
            <v-btn
              v-else
              size="small"
              variant="tonal"
              :color="color(b)"
              :loading="busy === key(b, a)"
              @click="run(b, a)"
            >
              {{ a.label }}
            </v-btn>
          </template>
          <v-btn
            v-if="b.id === 'setup'"
            size="small"
            variant="text"
            @click="ui.setupDismissed = true"
          >
            Hide
          </v-btn>
        </span>
      </div>
    </v-alert>

    <v-dialog :model-value="!!confirmStop" max-width="420" @update:model-value="confirmStop = null">
      <v-card v-if="confirmStop">
        <v-card-title>Stop the modem on {{ confirmStop.name }}?</v-card-title>
        <v-card-text>
          This deactivates the radio, which stops its modem and closes the PTT device. Turn it back
          on in Settings › Radios once the radio is safe.
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="confirmStop = null">Cancel</v-btn>
          <v-btn
            color="error"
            variant="tonal"
            :loading="busy === `stop:${confirmStop.radioId}`"
            @click="stopModem"
          >
            Stop modem
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </div>
</template>

<style scoped>
.health-banners {
  display: flex;
  flex-direction: column;
}
.health-banner + .health-banner {
  border-top: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}
.hb-body {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 16px;
}
.hb-text {
  flex: 1 1 280px;
  font-size: 0.875rem;
}
.hb-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
</style>
