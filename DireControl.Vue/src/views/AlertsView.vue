<script setup lang="ts">
import { useToastStore } from '@/stores/toastStore'
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useAlertsStore } from '@/stores/alertsStore'
import { useLightningAlertsStore, cardinal } from '@/stores/lightningAlertsStore'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { useHealthStore } from '@/stores/health'
import { useUnits } from '@/composables/useUnits'
import { ALERT_TYPE_COLORS, type AlertDto } from '@/types/alert'
import { formatDateTime, timeAgo } from '@/utils/time'
import { useTick } from '@/composables/useTick'

const router = useRouter()
const alertsStore = useAlertsStore()
const lightningStore = useLightningAlertsStore()
const stationSelection = useStationSelectionStore()
const healthStore = useHealthStore()
const { formatDistance } = useUnits()
const { now } = useTick(5000)

type FilterKey = 'all' | 'open' | 'WatchList' | 'Proximity' | 'Geofence' | 'Lightning'

const FILTERS: { key: FilterKey; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'open', label: 'Unacknowledged' },
  { key: 'WatchList', label: 'Watch list' },
  { key: 'Proximity', label: 'Proximity' },
  { key: 'Geofence', label: 'Geofence' },
  { key: 'Lightning', label: 'Lightning' },
]

const filter = ref<FilterKey>('all')

onMounted(() => {
  alertsStore.fetchAlerts()
})

/** One row in the log: a stored alert, or a lightning strike that fired an alert this session. */
interface AlertRow {
  key: string
  type: string
  label: string
  callsign: string | null
  text: string
  at: string
  /** Stored alerts can be acknowledged; lightning alerts are session-only and expire. */
  alertId: number | null
  isAcknowledged: boolean
}

function typeLabel(type: string): string {
  return type === 'WatchList' ? 'Watch list' : type
}

function alertDetailText(alert: AlertDto): string {
  const dist = alert.distanceMeters ? ` (${formatDistance(alert.distanceMeters / 1000)})` : ''
  switch (alert.alertTypeName) {
    case 'WatchList':
      return 'Station came online after being stale'
    case 'Proximity':
      return alert.ruleName
        ? `Entered proximity zone "${alert.ruleName}"${dist}`
        : `Within proximity radius${dist}`
    case 'Geofence':
      return alert.geofenceName
        ? `${alert.direction === 'entered' ? 'Entered' : 'Exited'} geofence "${alert.geofenceName}"`
        : `${alert.direction === 'entered' ? 'Entered' : 'Exited'} geofence`
    default:
      return alert.alertTypeName
  }
}

const rows = computed<AlertRow[]>(() => {
  const stored: AlertRow[] = alertsStore.alerts.map((a) => ({
    key: `a${a.id}`,
    type: a.alertTypeName,
    label: typeLabel(a.alertTypeName),
    callsign: a.callsign,
    text: alertDetailText(a),
    at: a.triggeredAt,
    alertId: a.id,
    isAcknowledged: a.isAcknowledged,
  }))
  const lightning: AlertRow[] = lightningStore.triggeringStrikes.map((s) => ({
    key: `l${s.key}`,
    type: 'Lightning',
    label: 'Lightning',
    callsign: null,
    text: `Strike ${formatDistance(s.distanceKm)} ${cardinal(s.bearingDegrees)} of home, inside the ${formatDistance(s.radiusKm)} alert radius`,
    at: s.strikeTimeUtc,
    alertId: null,
    isAcknowledged: false,
  }))
  return [...stored, ...lightning].sort((a, b) => Date.parse(b.at) - Date.parse(a.at))
})

const filteredRows = computed(() => {
  const f = filter.value
  if (f === 'all') return rows.value
  if (f === 'open') return rows.value.filter((r) => r.alertId !== null && !r.isAcknowledged)
  return rows.value.filter((r) => r.type === f)
})

const openCount = computed(() => alertsStore.unacknowledgedCount)

function chipColor(type: string): string {
  if (type === 'Lightning') return 'error'
  return ALERT_TYPE_COLORS[type] ?? 'grey'
}

const toastStore = useToastStore()

async function acknowledge(id: number) {
  try {
    await alertsStore.acknowledge(id)
    toastStore.toast('Alert acknowledged', 'success')
  } catch {
    toastStore.toast("Couldn't acknowledge the alert — the backend may be unreachable.", 'error')
  }
}

function goToStation(callsign: string) {
  stationSelection.selectStation(callsign)
  router.push('/')
}
</script>

<template>
  <div class="alerts-view">
    <!-- Toolbar -->
    <div class="alerts-toolbar pa-3 d-flex flex-column ga-3">
      <div class="d-flex align-center ga-3 flex-wrap">
        <span class="text-h6 font-weight-bold">Alerts</span>
        <span class="text-caption text-medium-emphasis">{{ openCount }} not acknowledged</span>
        <v-spacer />
        <v-btn variant="text" color="primary" to="/settings/zones">Rules and zones</v-btn>
      </div>

      <v-alert v-if="!healthStore.health.live" type="warning" variant="tonal" density="compact">
        Live feed lost — alerts raised meanwhile appear once it reconnects.
      </v-alert>

      <div class="filter-scroll">
        <v-btn-toggle
          v-model="filter"
          mandatory
          density="compact"
          variant="outlined"
          divided
          color="primary"
          aria-label="Filter alerts"
        >
          <v-btn v-for="f in FILTERS" :key="f.key" :value="f.key">{{ f.label }}</v-btn>
        </v-btn-toggle>
      </div>
    </div>

    <v-divider />

    <!-- Alert list -->
    <div class="alerts-list">
      <v-empty-state
        v-if="filteredRows.length === 0"
        icon="mdi-bell-outline"
        :title="rows.length === 0 ? 'No alerts yet' : 'Nothing matches'"
        :text="
          rows.length === 0
            ? 'Set up geofences, proximity rules, lightning alerts or a watch list and alerts land here.'
            : 'Try another filter.'
        "
      />

      <div
        v-for="row in filteredRows"
        :key="row.key"
        class="alert-row"
        :class="{ 'alert-row--acked': row.isAcknowledged }"
      >
        <div class="d-flex align-center ga-2 flex-wrap">
          <v-chip :color="chipColor(row.type)" size="small" label class="flex-shrink-0">
            {{ row.label }}
          </v-chip>

          <a
            v-if="row.callsign"
            href="#"
            class="callsign-link font-weight-medium"
            @click.prevent="goToStation(row.callsign)"
          >
            {{ row.callsign }}
          </a>

          <span class="text-caption text-medium-emphasis flex-shrink-0">
            {{ formatDateTime(row.at) }} · {{ timeAgo(row.at, now) }}
          </span>

          <v-spacer />

          <span
            v-if="row.isAcknowledged"
            class="text-caption text-medium-emphasis d-inline-flex align-center ga-1"
          >
            <v-icon size="16" color="success">mdi-check-circle</v-icon>
            Acknowledged
          </span>
          <v-btn
            v-else-if="row.alertId !== null"
            size="small"
            variant="tonal"
            color="primary"
            @click="acknowledge(row.alertId)"
          >
            Acknowledge
          </v-btn>
          <span v-else class="text-caption text-medium-emphasis">Clears after 10 min</span>
        </div>

        <div class="text-body-2 text-medium-emphasis mt-1">
          {{ row.text }}
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.alerts-view {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.alerts-toolbar {
  background: rgb(var(--v-theme-surface));
  flex-shrink: 0;
}

.filter-scroll {
  max-width: 100%;
  overflow-x: auto;
}

.alerts-list {
  flex: 1;
  overflow-y: auto;
}

.alert-row {
  padding: 10px 16px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
}

.alert-row--acked {
  opacity: 0.6;
}

.callsign-link {
  color: rgb(var(--v-theme-primary));
  font-size: 0.9rem;
  text-decoration: none;
}

.callsign-link:hover,
.callsign-link:focus-visible {
  text-decoration: underline;
}
</style>
