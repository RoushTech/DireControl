<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useAlertsStore } from '@/stores/alertsStore'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { ALERT_TYPE_COLORS } from '@/types/alert'
import { formatUtc, timeAgo } from '@/utils/time'

const router = useRouter()
const alertsStore = useAlertsStore()
const stationSelection = useStationSelectionStore()

const typeFilter = ref<string>('')
const showAcknowledged = ref(true)

onMounted(() => {
  alertsStore.fetchAlerts()
})

const filteredAlerts = computed(() => {
  let list = alertsStore.alerts
  if (typeFilter.value) list = list.filter((a) => a.alertTypeName === typeFilter.value)
  if (!showAcknowledged.value) list = list.filter((a) => !a.isAcknowledged)
  return list
})

function alertDetailText(alert: (typeof alertsStore.alerts)[0]): string {
  switch (alert.alertTypeName) {
    case 'WatchList':
      return 'Station came online after being stale'
    case 'Proximity':
      return alert.ruleName
        ? `Entered proximity zone "${alert.ruleName}"${alert.distanceMeters ? ` (${Math.round(alert.distanceMeters)}m)` : ''}`
        : `Within proximity radius${alert.distanceMeters ? ` (${Math.round(alert.distanceMeters)}m)` : ''}`
    case 'Geofence':
      return alert.geofenceName
        ? `${alert.direction === 'entered' ? 'Entered' : 'Exited'} geofence "${alert.geofenceName}"`
        : `${alert.direction === 'entered' ? 'Entered' : 'Exited'} geofence`
    case 'NewMessage':
      return alert.messageText ? `Message: ${alert.messageText}` : 'New message'
    default:
      return alert.alertTypeName
  }
}

async function acknowledge(id: number) {
  await alertsStore.acknowledge(id)
}

function goToStation(callsign: string) {
  stationSelection.selectStation(callsign)
  router.push('/')
}
</script>

<template>
  <div class="alerts-view">
    <!-- Toolbar -->
    <div class="alerts-toolbar pa-3 d-flex align-center ga-3 flex-wrap">
      <span class="text-h6 font-weight-bold">Alert Log</span>

      <v-select
        v-model="typeFilter"
        :items="[
          { title: 'All Types', value: '' },
          { title: 'Watch List', value: 'WatchList' },
          { title: 'Proximity', value: 'Proximity' },
          { title: 'Geofence', value: 'Geofence' },
          { title: 'New Message', value: 'NewMessage' },
        ]"
        item-title="title"
        item-value="value"
        density="compact"
        variant="outlined"
        hide-details
        style="max-width: 180px"
      />

      <v-switch
        v-model="showAcknowledged"
        label="Show acknowledged"
        density="compact"
        hide-details
        color="primary"
      />

      <v-spacer />

      <span class="text-caption text-medium-emphasis">
        {{ filteredAlerts.length }} alert{{ filteredAlerts.length !== 1 ? 's' : '' }}
      </span>
    </div>

    <v-divider />

    <!-- Alert list -->
    <div class="alerts-list">
      <div
        v-if="filteredAlerts.length === 0"
        class="text-center text-medium-emphasis py-8"
      >
        No alerts
      </div>

      <div
        v-for="alert in filteredAlerts"
        :key="alert.id"
        class="alert-row"
        :class="{ 'alert-row--acked': alert.isAcknowledged }"
      >
        <div class="d-flex align-center ga-2 flex-wrap">
          <!-- Type badge -->
          <v-chip
            :color="ALERT_TYPE_COLORS[alert.alertTypeName] ?? 'grey'"
            size="x-small"
            label
            class="flex-shrink-0"
          >
            {{ alert.alertTypeName }}
          </v-chip>

          <!-- Callsign -->
          <span
            class="callsign-link font-weight-medium"
            @click="goToStation(alert.callsign)"
          >
            {{ alert.callsign }}
          </span>

          <!-- Timestamp -->
          <span
            class="text-caption text-medium-emphasis flex-shrink-0"
            :title="formatUtc(alert.triggeredAt)"
          >
            {{ timeAgo(alert.triggeredAt) }}
          </span>

          <v-spacer />

          <!-- Ack status/button -->
          <v-icon
            v-if="alert.isAcknowledged"
            size="16"
            color="success"
            title="Acknowledged"
          >
            mdi-check-circle
          </v-icon>
          <v-btn
            v-else
            size="x-small"
            variant="outlined"
            color="primary"
            @click="acknowledge(alert.id)"
          >
            Acknowledge
          </v-btn>
        </div>

        <!-- Detail text -->
        <div class="text-body-2 text-medium-emphasis mt-1">
          {{ alertDetailText(alert) }}
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

.alerts-list {
  flex: 1;
  overflow-y: auto;
}

.alert-row {
  padding: 10px 16px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.08);
  transition: background 0.15s;
}

.alert-row:hover {
  background: rgba(var(--v-theme-on-surface), 0.04);
}

.alert-row--acked {
  opacity: 0.55;
}

.callsign-link {
  cursor: pointer;
  color: rgba(var(--v-theme-primary), 1);
  font-size: 0.9rem;
}

.callsign-link:hover {
  text-decoration: underline;
}
</style>
