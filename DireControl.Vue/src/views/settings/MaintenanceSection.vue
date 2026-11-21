<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { formatDateTime } from '@/utils/time'
import {
  getMaintenanceStatus,
  updateRetention,
  runCleanup,
  getReprocessStatus,
  startReprocess,
  type CleanupResult,
  type ReprocessStatusDto,
} from '@/api/maintenanceApi'
import { apiErrorDetail } from '@/api/axios'
import { useToastStore } from '@/stores/toastStore'
import SettingsSaveBar from './SettingsSaveBar.vue'
import { settingsSection } from './sections'
import { useFormSnapshot } from './useFormSnapshot'

const meta = settingsSection('maintenance')
const toastStore = useToastStore()

function showToast(text: string, color: 'success' | 'error') {
  toastStore.toast(text, color)
}

// ─── Database maintenance ─────────────────────────────────────────────────────
const dbSizeBytes = ref(0)
const cleanupIntervalHours = ref(0)
const vacuumOnCleanup = ref(true)
const lastCleanup = ref<CleanupResult | null>(null)
const cleanupRunning = ref(false)
const retentionRfDays = ref(0)
const retentionAprsIsDays = ref(14)
const retentionOwnDays = ref(0)
const retentionSaving = ref(false)
const retentionSaveError = ref('')
let cleanupPollTimer: ReturnType<typeof setInterval> | null = null

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let size = bytes
  let u = 0
  while (size >= 1024 && u < units.length - 1) {
    size /= 1024
    u++
  }
  return `${size.toFixed(1)} ${units[u]}`
}

async function loadMaintenance() {
  try {
    const s = await getMaintenanceStatus()
    dbSizeBytes.value = s.databaseSizeBytes
    cleanupIntervalHours.value = s.cleanupIntervalHours
    vacuumOnCleanup.value = s.vacuumOnCleanup
    lastCleanup.value = s.lastResult
    cleanupRunning.value = s.isRunning
    retentionRfDays.value = s.retention.rfDays
    retentionAprsIsDays.value = s.retention.aprsIsDays
    retentionOwnDays.value = s.retention.ownDays
    retention.markClean()
    if (s.isRunning) startCleanupPolling()
  } catch {
    /* ignore */
  }
}

const retention = useFormSnapshot(() => [
  Number(retentionRfDays.value),
  Number(retentionAprsIsDays.value),
  Number(retentionOwnDays.value),
])

async function saveRetention() {
  retentionSaving.value = true
  retentionSaveError.value = ''
  try {
    const payload = {
      rfDays: Math.max(0, Math.floor(retentionRfDays.value || 0)),
      aprsIsDays: Math.max(0, Math.floor(retentionAprsIsDays.value || 0)),
      ownDays: Math.max(0, Math.floor(retentionOwnDays.value || 0)),
    }
    await updateRetention(payload)
    retentionRfDays.value = payload.rfDays
    retentionAprsIsDays.value = payload.aprsIsDays
    retentionOwnDays.value = payload.ownDays
    retention.markClean()
    toastStore.toast('Retention saved. Applies at the next cleanup.', 'success', 3000)
  } catch (e) {
    retentionSaveError.value = `Failed to save retention settings: ${apiErrorDetail(e)}`
  } finally {
    retentionSaving.value = false
  }
}

function startCleanupPolling() {
  if (cleanupPollTimer) return
  cleanupPollTimer = setInterval(async () => {
    try {
      const s = await getMaintenanceStatus()
      cleanupRunning.value = s.isRunning
      dbSizeBytes.value = s.databaseSizeBytes
      lastCleanup.value = s.lastResult
      if (!s.isRunning) stopCleanupPolling()
    } catch {
      /* keep polling */
    }
  }, 1500)
}

function stopCleanupPolling() {
  if (cleanupPollTimer) {
    clearInterval(cleanupPollTimer)
    cleanupPollTimer = null
  }
}

// ─── Packet reprocessing ──────────────────────────────────────────────────────
const reprocessStatus = ref<ReprocessStatusDto | null>(null)
const reprocessStarting = ref(false)
const reprocessForce = ref(false)
let reprocessPollTimer: ReturnType<typeof setInterval> | null = null

async function loadReprocess() {
  try {
    reprocessStatus.value = await getReprocessStatus()
    if (reprocessStatus.value.isRunning) startReprocessPolling()
    else stopReprocessPolling()
  } catch {
    /* card shows last known state; maintenance load error is surfaced elsewhere */
  }
}

function startReprocessPolling() {
  if (reprocessPollTimer) return
  reprocessPollTimer = setInterval(loadReprocess, 1500)
}

function stopReprocessPolling() {
  if (reprocessPollTimer) {
    clearInterval(reprocessPollTimer)
    reprocessPollTimer = null
  }
}

const reprocessProgressPercent = computed(() => {
  const s = reprocessStatus.value
  if (!s || !s.isRunning || s.total === 0) return null
  return Math.min(100, (s.processed / s.total) * 100)
})

async function startReprocessNow() {
  reprocessStarting.value = true
  try {
    await startReprocess({ force: reprocessForce.value })
    showToast('Reprocess started. Packets are being re-parsed in the background.', 'success')
    startReprocessPolling()
    await loadReprocess()
  } catch {
    showToast('Couldn’t start the reprocess. One may already be running.', 'error')
    await loadReprocess()
  } finally {
    reprocessStarting.value = false
  }
}

const cleanupConfirmOpen = ref(false)
const cleanupConfirmText = ref('')

watch(cleanupConfirmOpen, (open) => {
  if (open) cleanupConfirmText.value = ''
})

/** Human summary of the retention windows, for the confirmation dialog. */
function retentionLabel(days: number): string {
  return days === 0 ? 'kept forever' : `deleted after ${days} day${days === 1 ? '' : 's'}`
}

async function runCleanupNow() {
  cleanupConfirmOpen.value = false
  cleanupRunning.value = true
  try {
    await runCleanup()
    startCleanupPolling()
  } catch {
    cleanupRunning.value = false
    showToast('Cleanup failed to start. The backend may be unreachable.', 'error')
  }
}
onMounted(() => Promise.all([loadMaintenance(), loadReprocess()]))

onUnmounted(() => {
  stopCleanupPolling()
  stopReprocessPolling()
})
</script>

<template>
  <div class="settings-single">
    <!-- ================================================================ -->
    <!-- Database maintenance -->
    <!-- ================================================================ -->
    <div class="section-header d-flex align-center mb-2">
      <span class="text-h6">Database Maintenance</span>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <div class="d-flex align-center mb-4">
        <div>
          <div class="text-caption text-medium-emphasis">Database size</div>
          <div class="text-h6">{{ formatBytes(dbSizeBytes) }}</div>
        </div>
        <v-spacer />
        <v-btn
          color="primary"
          :loading="cleanupRunning"
          :disabled="cleanupRunning"
          prepend-icon="mdi-broom"
          @click="cleanupConfirmOpen = true"
        >
          {{ cleanupRunning ? 'Cleaning…' : 'Run Cleanup Now' }}
        </v-btn>
      </div>

      <div class="text-body-2 mb-2">
        Packet retention: how long to keep received packets before pruning.
        <strong>0 = keep forever.</strong>
      </div>

      <div class="d-flex flex-wrap ga-4 mb-1">
        <v-text-field
          v-model.number="retentionRfDays"
          type="number"
          min="0"
          label="RF (days)"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 160px"
        />
        <v-text-field
          v-model.number="retentionAprsIsDays"
          type="number"
          min="0"
          label="APRS-IS (days)"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 160px"
        />
        <v-text-field
          v-model.number="retentionOwnDays"
          type="number"
          min="0"
          label="Own (days)"
          density="compact"
          variant="outlined"
          hide-details
          style="max-width: 160px"
        />
      </div>

      <v-alert
        v-if="retentionSaveError"
        type="error"
        variant="tonal"
        density="compact"
        class="mt-2"
      >
        {{ retentionSaveError }}
      </v-alert>

      <div class="text-caption text-medium-emphasis mt-3">
        Cleanup runs automatically
        <template v-if="cleanupIntervalHours > 0">every {{ cleanupIntervalHours }}h</template>
        <template v-else>only when triggered manually</template>, and
        {{ vacuumOnCleanup ? 'reclaims freed space (VACUUM)' : 'does not VACUUM' }} after pruning.
      </div>

      <v-divider class="my-3" />

      <div class="text-caption text-medium-emphasis mb-1">Last cleanup</div>
      <div v-if="!lastCleanup" class="text-body-2 text-medium-emphasis">
        No cleanup has run yet.
      </div>
      <div v-else-if="lastCleanup.error" class="text-body-2 text-error">
        Failed: {{ lastCleanup.error }}
      </div>
      <div v-else class="text-body-2">
        Deleted
        <strong>{{
          (
            lastCleanup.rfDeleted +
            lastCleanup.aprsIsDeleted +
            lastCleanup.ownDeleted
          ).toLocaleString()
        }}</strong>
        packets ({{ lastCleanup.aprsIsDeleted.toLocaleString() }} APRS-IS,
        {{ lastCleanup.rfDeleted.toLocaleString() }} RF,
        {{ lastCleanup.ownDeleted.toLocaleString() }} Own) ·
        {{ formatBytes(lastCleanup.sizeBeforeBytes) }} →
        {{ formatBytes(lastCleanup.sizeAfterBytes) }}
        <span v-if="lastCleanup.vacuumed" class="text-success">· vacuumed</span>
        <span v-else-if="lastCleanup.vacuumError" class="text-warning"
          >· VACUUM skipped (busy)</span
        >
        <span class="text-medium-emphasis"> · {{ formatDateTime(lastCleanup.completedAt) }}</span>
      </div>
    </v-card>

    <!-- ================================================================ -->
    <!-- Packet reprocessing -->
    <!-- ================================================================ -->
    <div class="section-header d-flex align-center mb-2">
      <span class="text-h6">Packet Reprocessing</span>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <div class="text-body-2 mb-3">
        Re-derives positions, weather, telemetry, and resolved paths from the stored raw packets.
        Useful after a parser fix. Runs in the background; parser version
        <strong>{{ reprocessStatus?.currentParserVersion ?? '…' }}</strong>.
      </div>

      <template v-if="reprocessStatus?.isRunning">
        <v-progress-linear
          :model-value="reprocessProgressPercent ?? 0"
          :indeterminate="reprocessProgressPercent === null"
          color="primary"
          height="8"
          rounded
          class="mb-2"
        />
        <div class="text-caption text-medium-emphasis mb-2">
          {{ reprocessStatus.processed.toLocaleString() }}
          <template v-if="reprocessStatus.total > 0">
            of {{ reprocessStatus.total.toLocaleString() }}
          </template>
          packets reprocessed…
        </div>
      </template>

      <div class="d-flex align-center ga-4 flex-wrap">
        <v-switch
          v-model="reprocessForce"
          label="Force: re-parse every packet, not just outdated ones"
          density="compact"
          hide-details
          color="primary"
          :disabled="reprocessStatus?.isRunning"
        />
        <v-btn
          color="primary"
          variant="tonal"
          prepend-icon="mdi-cog-refresh"
          :loading="reprocessStarting"
          :disabled="reprocessStatus?.isRunning"
          @click="startReprocessNow"
        >
          {{ reprocessStatus?.isRunning ? 'Running…' : 'Start Reprocess' }}
        </v-btn>
      </div>

      <template v-if="reprocessStatus?.lastResult">
        <v-divider class="my-3" />
        <div class="text-caption text-medium-emphasis mb-1">Last run</div>
        <div v-if="reprocessStatus.lastResult.error" class="text-body-2 text-error">
          Failed: {{ reprocessStatus.lastResult.error }}
        </div>
        <div v-else class="text-body-2">
          Reprocessed
          <strong>{{ reprocessStatus.lastResult.processed.toLocaleString() }}</strong>
          packets ({{ reprocessStatus.lastResult.failed.toLocaleString() }} failed<template
            v-if="reprocessStatus.lastResult.orphanStationsDeleted > 0"
          >
            · {{ reprocessStatus.lastResult.orphanStationsDeleted.toLocaleString() }} orphan
            stations removed</template
          >)
          <span class="text-medium-emphasis">
            · {{ formatDateTime(reprocessStatus.lastResult.completedAt) }}</span
          >
        </div>
      </template>
    </v-card>

    <SettingsSaveBar
      :restart="meta.restart"
      :dirty="retention.dirty.value"
      :saving="retentionSaving"
      :disabled="!retention.dirty.value"
      save-label="Save retention"
      @save="saveRetention"
    />
  </div>

  <!-- Cleanup confirmation dialog -->
  <v-dialog v-model="cleanupConfirmOpen" max-width="460">
    <v-card>
      <v-card-title>
        <v-icon color="error" class="mr-2">mdi-broom</v-icon>
        Run cleanup now?
      </v-card-title>
      <v-card-text>
        <v-alert
          v-if="retention.dirty.value"
          type="warning"
          variant="tonal"
          density="compact"
          class="mb-3"
        >
          The retention changes below are not saved yet. Save them first, or cleanup uses the
          previously saved windows.
        </v-alert>
        <p class="mb-3">
          This permanently deletes packets outside the retention windows: RF packets
          {{ retentionLabel(retentionRfDays) }}, APRS-IS packets
          {{ retentionLabel(retentionAprsIsDays) }}, own beacons
          {{ retentionLabel(retentionOwnDays) }}. This cannot be undone.
        </p>
        <p class="text-caption text-medium-emphasis mb-2">Type <code>cleanup</code> to confirm:</p>
        <v-text-field
          v-model="cleanupConfirmText"
          density="compact"
          variant="outlined"
          hide-details
          autofocus
          aria-label="Type cleanup to confirm"
          @keydown.enter="cleanupConfirmText.trim() === 'cleanup' && runCleanupNow()"
        />
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="cleanupConfirmOpen = false">Cancel</v-btn>
        <v-btn
          color="error"
          variant="tonal"
          :disabled="cleanupConfirmText.trim() !== 'cleanup'"
          @click="runCleanupNow"
        >
          Delete old packets
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
