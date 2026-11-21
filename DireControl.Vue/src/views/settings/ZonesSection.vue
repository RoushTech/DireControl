<script setup lang="ts">
import { onMounted, onUnmounted, ref, shallowRef } from 'vue'
import L from 'leaflet'
import {
  getGeofences,
  createGeofence,
  deleteGeofence,
  getProximityRules,
  createProximityRule,
  deleteProximityRule,
} from '@/api/alertsApi'
import type { GeofenceDto, ProximityRuleDto } from '@/types/alert'
import { apiErrorDetail } from '@/api/axios'
import { useUnits } from '@/composables/useUnits'
import { useToastStore } from '@/stores/toastStore'
import SettingsSaveBar from './SettingsSaveBar.vue'
import ConfirmDeleteDialog from './ConfirmDeleteDialog.vue'
import { settingsSection } from './sections'

const meta = settingsSection('zones')
const { formatDistance } = useUnits()
const toastStore = useToastStore()

// ---- Geofences ----
const geofences = ref<GeofenceDto[]>([])
const showAddGeofence = ref(false)
const gfName = ref('')
const gfLat = ref<number | null>(null)
const gfLon = ref<number | null>(null)
const gfRadius = ref<number>(500)
const gfAlertOnEnter = ref(true)
const gfAlertOnExit = ref(true)
const gfSaving = ref(false)

// ---- Proximity Rules ----
const rules = ref<ProximityRuleDto[]>([])
const showAddRule = ref(false)
const prName = ref('')
const prCallsign = ref('')
const prLat = ref<number | null>(null)
const prLon = ref<number | null>(null)
const prRadius = ref<number>(1000)
const prSaving = ref(false)

// ---- Leaflet map for picking coordinates ----
let map: L.Map | null = null
let mapMarker: L.Marker | null = null
let mapCircle: L.Circle | null = null
let pickingFor: 'geofence' | 'rule' | null = null

async function loadGeofences() {
  try {
    geofences.value = await getGeofences()
  } catch {
    /* */
  }
}
async function loadRules() {
  try {
    rules.value = await getProximityRules()
  } catch {
    /* */
  }
}

function initMap(
  containerId: string,
  forType: 'geofence' | 'rule',
  defaultLat: number,
  defaultLon: number,
) {
  if (map) {
    map.remove()
    map = null
    mapMarker = null
    mapCircle = null
  }
  pickingFor = forType
  const el = document.getElementById(containerId)
  if (!el) return

  map = L.map(el).setView([defaultLat, defaultLon], 11)
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    attribution: '© OpenStreetMap',
  }).addTo(map)

  map.on('click', (e: L.LeafletMouseEvent) => {
    const { lat, lng } = e.latlng
    if (pickingFor === 'geofence') {
      gfLat.value = Math.round(lat * 1000000) / 1000000
      gfLon.value = Math.round(lng * 1000000) / 1000000
    } else if (pickingFor === 'rule') {
      prLat.value = Math.round(lat * 1000000) / 1000000
      prLon.value = Math.round(lng * 1000000) / 1000000
    }
    if (mapMarker) map!.removeLayer(mapMarker)
    mapMarker = L.marker([lat, lng]).addTo(map!)
    if (mapCircle) map!.removeLayer(mapCircle)
    const r = pickingFor === 'geofence' ? gfRadius.value || 500 : prRadius.value || 1000
    mapCircle = L.circle([lat, lng], { radius: r, color: '#2196f3', fillOpacity: 0.12 }).addTo(map!)
  })
}

function openAddGeofence() {
  showAddGeofence.value = true
  gfName.value = ''
  gfLat.value = null
  gfLon.value = null
  gfRadius.value = 500
  gfAlertOnEnter.value = true
  gfAlertOnExit.value = true
  setTimeout(() => initMap('gf-map', 'geofence', 39.0, -98.0), 50)
}

function openAddRule() {
  showAddRule.value = true
  prName.value = ''
  prCallsign.value = ''
  prLat.value = null
  prLon.value = null
  prRadius.value = 1000
  setTimeout(() => initMap('pr-map', 'rule', 39.0, -98.0), 50)
}

async function saveGeofence() {
  if (!gfName.value || gfLat.value == null || gfLon.value == null) return
  gfSaving.value = true
  try {
    const gf = await createGeofence({
      name: gfName.value,
      centerLat: gfLat.value,
      centerLon: gfLon.value,
      radiusMeters: gfRadius.value,
      alertOnEnter: gfAlertOnEnter.value,
      alertOnExit: gfAlertOnExit.value,
    })
    geofences.value.push(gf)
    showAddGeofence.value = false
    if (map) {
      map.remove()
      map = null
    }
    toastStore.toast(`Geofence "${gf.name}" added`, 'success', 3000)
  } catch (e) {
    toastStore.toast(`Couldn't add the geofence: ${apiErrorDetail(e)}`, 'error')
  } finally {
    gfSaving.value = false
  }
}

async function removeGeofence(id: number) {
  await deleteGeofence(id)
  geofences.value = geofences.value.filter((f) => f.id !== id)
}

async function saveRule() {
  if (!prName.value || prLat.value == null || prLon.value == null) return
  prSaving.value = true
  try {
    const rule = await createProximityRule({
      name: prName.value,
      targetCallsign: prCallsign.value.trim() || null,
      centerLat: prLat.value,
      centerLon: prLon.value,
      radiusMetres: prRadius.value,
    })
    rules.value.push(rule)
    showAddRule.value = false
    if (map) {
      map.remove()
      map = null
    }
    toastStore.toast(`Proximity rule "${rule.name}" added`, 'success', 3000)
  } catch (e) {
    toastStore.toast(`Couldn't add the proximity rule: ${apiErrorDetail(e)}`, 'error')
  } finally {
    prSaving.value = false
  }
}

async function removeRule(id: number) {
  await deleteProximityRule(id)
  rules.value = rules.value.filter((r) => r.id !== id)
}

// ─── Confirm delete dialog ────────────────────────────────────────────────────
const deleteConfirmOpen = ref(false)
const deleteConfirmMessage = ref('')
const deleteAction = shallowRef<(() => Promise<void>) | null>(null)

function promptDeleteGeofence(id: number, name: string) {
  deleteConfirmMessage.value = `Delete geofence "${name}"? This cannot be undone.`
  deleteAction.value = () => removeGeofence(id)
  deleteConfirmOpen.value = true
}

function promptDeleteRule(id: number, name: string) {
  deleteConfirmMessage.value = `Delete proximity rule "${name}"? This cannot be undone.`
  deleteAction.value = () => removeRule(id)
  deleteConfirmOpen.value = true
}

onMounted(() => Promise.all([loadGeofences(), loadRules()]))

onUnmounted(() => {
  if (map) {
    map.remove()
    map = null
  }
})
</script>

<template>
  <div class="settings-grid">
    <div class="settings-section">
      <!-- ================================================================ -->
      <!-- Geofences -->
      <!-- ================================================================ -->
      <div class="section-header d-flex align-center mb-2">
        <span class="text-h6">Geofences</span>
        <v-spacer />
        <v-btn size="small" color="primary" prepend-icon="mdi-plus" @click="openAddGeofence">
          Add Geofence
        </v-btn>
      </div>

      <v-card variant="outlined" class="mb-6">
        <div v-if="geofences.length === 0" class="text-center text-medium-emphasis py-4">
          No geofences defined
        </div>
        <v-list v-else density="compact">
          <v-list-item v-for="gf in geofences" :key="gf.id">
            <template #prepend>
              <v-icon :color="gf.isActive ? 'green' : 'grey'" size="20"
                >mdi-map-marker-radius</v-icon
              >
            </template>
            <v-list-item-title>{{ gf.name }}</v-list-item-title>
            <v-list-item-subtitle>
              {{ gf.centerLat.toFixed(5) }}, {{ gf.centerLon.toFixed(5) }} —
              {{ formatDistance(gf.radiusMeters / 1000) }}
              <span v-if="gf.alertOnEnter" class="ml-1 text-caption">↓Enter</span>
              <span v-if="gf.alertOnExit" class="ml-1 text-caption">↑Exit</span>
            </v-list-item-subtitle>
            <template #append>
              <v-btn
                :aria-label="`Delete geofence ${gf.name}`"
                icon="mdi-delete"
                size="small"
                variant="text"
                color="error"
                @click="promptDeleteGeofence(gf.id, gf.name)"
              />
            </template>
          </v-list-item>
        </v-list>
      </v-card>
    </div>
    <div class="settings-section">
      <!-- ================================================================ -->
      <!-- Proximity Rules -->
      <!-- ================================================================ -->
      <div class="section-header d-flex align-center mb-2">
        <span class="text-h6">Proximity Rules</span>
        <v-spacer />
        <v-btn size="small" color="primary" prepend-icon="mdi-plus" @click="openAddRule">
          Add Rule
        </v-btn>
      </div>

      <v-card variant="outlined">
        <div v-if="rules.length === 0" class="text-center text-medium-emphasis py-4">
          No proximity rules defined
        </div>
        <v-list v-else density="compact">
          <v-list-item v-for="rule in rules" :key="rule.id">
            <template #prepend>
              <v-icon :color="rule.isActive ? 'blue' : 'grey'" size="20">mdi-radar</v-icon>
            </template>
            <v-list-item-title>{{ rule.name }}</v-list-item-title>
            <v-list-item-subtitle>
              <span v-if="rule.targetCallsign">{{ rule.targetCallsign }} — </span>
              {{ rule.centerLat.toFixed(5) }}, {{ rule.centerLon.toFixed(5) }} —
              {{ formatDistance(rule.radiusMetres / 1000) }}
            </v-list-item-subtitle>
            <template #append>
              <v-btn
                :aria-label="`Delete proximity rule ${rule.name}`"
                icon="mdi-delete"
                size="small"
                variant="text"
                color="error"
                @click="promptDeleteRule(rule.id, rule.name)"
              />
            </template>
          </v-list-item>
        </v-list>
      </v-card>
    </div>
  </div>
  <div class="settings-single">
    <SettingsSaveBar :restart="meta.restart" />
  </div>

  <!-- Add Geofence dialog -->
  <v-dialog v-model="showAddGeofence" max-width="600">
    <v-card>
      <v-card-title>Add Geofence</v-card-title>
      <v-card-text>
        <v-text-field v-model="gfName" label="Name" density="compact" class="mb-2" />
        <div class="d-flex ga-2 mb-2">
          <v-text-field v-model.number="gfLat" label="Latitude" density="compact" type="number" />
          <v-text-field v-model.number="gfLon" label="Longitude" density="compact" type="number" />
        </div>
        <v-text-field
          v-model.number="gfRadius"
          label="Radius (metres)"
          density="compact"
          type="number"
          class="mb-2"
        />
        <div class="d-flex ga-4 mb-2">
          <v-checkbox
            v-model="gfAlertOnEnter"
            label="Alert on enter"
            density="compact"
            hide-details
          />
          <v-checkbox
            v-model="gfAlertOnExit"
            label="Alert on exit"
            density="compact"
            hide-details
          />
        </div>
        <div class="text-caption text-medium-emphasis mb-1">
          Click on the map to set the centre point
        </div>
        <div id="gf-map" style="height: 280px; border-radius: 4px" />
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="showAddGeofence = false">Cancel</v-btn>
        <v-btn
          color="primary"
          :disabled="!gfName || gfLat == null || gfLon == null"
          :loading="gfSaving"
          @click="saveGeofence"
        >
          Save
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>

  <!-- Add Proximity Rule dialog -->
  <v-dialog v-model="showAddRule" max-width="600">
    <v-card>
      <v-card-title>Add Proximity Rule</v-card-title>
      <v-card-text>
        <v-text-field v-model="prName" label="Name" density="compact" class="mb-2" />
        <v-text-field
          v-model="prCallsign"
          label="Target callsign (optional — leave blank for any station)"
          density="compact"
          class="mb-2"
        />
        <div class="d-flex ga-2 mb-2">
          <v-text-field v-model.number="prLat" label="Latitude" density="compact" type="number" />
          <v-text-field v-model.number="prLon" label="Longitude" density="compact" type="number" />
        </div>
        <v-text-field
          v-model.number="prRadius"
          label="Radius (metres)"
          density="compact"
          type="number"
          class="mb-2"
        />
        <div class="text-caption text-medium-emphasis mb-1">
          Click on the map to set the centre point
        </div>
        <div id="pr-map" style="height: 280px; border-radius: 4px" />
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="showAddRule = false">Cancel</v-btn>
        <v-btn
          color="primary"
          :disabled="!prName || prLat == null || prLon == null"
          :loading="prSaving"
          @click="saveRule"
        >
          Save
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>

  <ConfirmDeleteDialog
    v-model="deleteConfirmOpen"
    :message="deleteConfirmMessage"
    :action="deleteAction"
  />
</template>
