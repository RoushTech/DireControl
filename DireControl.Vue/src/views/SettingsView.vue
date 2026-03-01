<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import L from 'leaflet'
import { getGeofences, createGeofence, deleteGeofence, getProximityRules, createProximityRule, deleteProximityRule } from '@/api/alertsApi'
import type { GeofenceDto, ProximityRuleDto } from '@/types/alert'

// ─── API Keys ────────────────────────────────────────────────────────────────
const API_KEYS_STORAGE_KEY = 'direcontrol-api-keys'

function readApiKeys(): Record<string, string> {
  try {
    const raw = localStorage.getItem(API_KEYS_STORAGE_KEY)
    if (raw) return JSON.parse(raw) as Record<string, string>
  } catch { /* ignore */ }
  return {}
}

const jawgApiKey = ref(readApiKeys()['jawg'] ?? '')
const showJawgKey = ref(false)
const apiKeySaved = ref(false)

function saveApiKeys() {
  const keys = readApiKeys()
  if (jawgApiKey.value.trim()) {
    keys['jawg'] = jawgApiKey.value.trim()
  } else {
    delete keys['jawg']
  }
  localStorage.setItem(API_KEYS_STORAGE_KEY, JSON.stringify(keys))
  apiKeySaved.value = true
  setTimeout(() => { apiKeySaved.value = false }, 2500)
}

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

onMounted(async () => {
  await Promise.all([loadGeofences(), loadRules()])
})

onUnmounted(() => {
  if (map) {
    map.remove()
    map = null
  }
})

async function loadGeofences() {
  try { geofences.value = await getGeofences() } catch { /* */ }
}
async function loadRules() {
  try { rules.value = await getProximityRules() } catch { /* */ }
}

function initMap(containerId: string, forType: 'geofence' | 'rule', defaultLat: number, defaultLon: number) {
  if (map) { map.remove(); map = null; mapMarker = null; mapCircle = null }
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
    const r = pickingFor === 'geofence' ? (gfRadius.value || 500) : (prRadius.value || 1000)
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
    if (map) { map.remove(); map = null }
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
    if (map) { map.remove(); map = null }
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
let deleteConfirmAction: (() => Promise<void>) | null = null

function promptDeleteGeofence(id: number, name: string) {
  deleteConfirmMessage.value = `Delete geofence "${name}"? This cannot be undone.`
  deleteConfirmAction = () => removeGeofence(id)
  deleteConfirmOpen.value = true
}

function promptDeleteRule(id: number, name: string) {
  deleteConfirmMessage.value = `Delete proximity rule "${name}"? This cannot be undone.`
  deleteConfirmAction = () => removeRule(id)
  deleteConfirmOpen.value = true
}

async function confirmDelete() {
  if (deleteConfirmAction) await deleteConfirmAction()
  deleteConfirmOpen.value = false
  deleteConfirmAction = null
}
</script>

<template>
  <div class="settings-view pa-4">
    <div class="text-h5 font-weight-bold mb-4">Settings</div>

    <!-- ================================================================ -->
    <!-- API Keys -->
    <!-- ================================================================ -->
    <div class="section-header d-flex align-center mb-2">
      <span class="text-h6">Map API Keys</span>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <div class="text-body-2 text-medium-emphasis mb-4">
        API keys are stored only in your browser's local storage and are never sent to the server.
      </div>
      <v-text-field
        v-model="jawgApiKey"
        label="Jawg Maps API key"
        density="compact"
        :type="showJawgKey ? 'text' : 'password'"
        :append-inner-icon="showJawgKey ? 'mdi-eye-off' : 'mdi-eye'"
        hint="Required for the Jawg Dark tile provider. Get a free key at jawg.io."
        persistent-hint
        class="mb-3"
        @click:append-inner="showJawgKey = !showJawgKey"
      />
      <div class="d-flex align-center ga-3 mt-2">
        <v-btn
          size="small"
          color="primary"
          prepend-icon="mdi-content-save"
          @click="saveApiKeys"
        >
          Save Keys
        </v-btn>
        <v-fade-transition>
          <span v-if="apiKeySaved" class="text-caption text-success">
            <v-icon size="14" class="mr-1">mdi-check-circle</v-icon>Saved
          </span>
        </v-fade-transition>
      </div>
    </v-card>

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
        <v-list-item
          v-for="gf in geofences"
          :key="gf.id"
        >
          <template #prepend>
            <v-icon :color="gf.isActive ? 'green' : 'grey'" size="20">mdi-map-marker-radius</v-icon>
          </template>
          <v-list-item-title>{{ gf.name }}</v-list-item-title>
          <v-list-item-subtitle>
            {{ gf.centerLat.toFixed(5) }}, {{ gf.centerLon.toFixed(5) }} —
            {{ gf.radiusMeters >= 1000 ? `${(gf.radiusMeters / 1000).toFixed(1)} km` : `${Math.round(gf.radiusMeters)} m` }}
            <span v-if="gf.alertOnEnter" class="ml-1 text-caption">↓Enter</span>
            <span v-if="gf.alertOnExit" class="ml-1 text-caption">↑Exit</span>
          </v-list-item-subtitle>
          <template #append>
            <v-btn icon="mdi-delete" size="x-small" variant="text" color="error" @click="promptDeleteGeofence(gf.id, gf.name)" />
          </template>
        </v-list-item>
      </v-list>
    </v-card>

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
            <v-checkbox v-model="gfAlertOnEnter" label="Alert on enter" density="compact" hide-details />
            <v-checkbox v-model="gfAlertOnExit" label="Alert on exit" density="compact" hide-details />
          </div>
          <div class="text-caption text-medium-emphasis mb-1">Click on the map to set the centre point</div>
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
        <v-list-item
          v-for="rule in rules"
          :key="rule.id"
        >
          <template #prepend>
            <v-icon :color="rule.isActive ? 'blue' : 'grey'" size="20">mdi-radar</v-icon>
          </template>
          <v-list-item-title>{{ rule.name }}</v-list-item-title>
          <v-list-item-subtitle>
            <span v-if="rule.targetCallsign">{{ rule.targetCallsign }} — </span>
            {{ rule.centerLat.toFixed(5) }}, {{ rule.centerLon.toFixed(5) }} —
            {{ rule.radiusMetres >= 1000 ? `${(rule.radiusMetres / 1000).toFixed(1)} km` : `${Math.round(rule.radiusMetres)} m` }}
          </v-list-item-subtitle>
          <template #append>
            <v-btn icon="mdi-delete" size="x-small" variant="text" color="error" @click="promptDeleteRule(rule.id, rule.name)" />
          </template>
        </v-list-item>
      </v-list>
    </v-card>

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
          <div class="text-caption text-medium-emphasis mb-1">Click on the map to set the centre point</div>
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

    <!-- Delete confirmation dialog -->
    <v-dialog v-model="deleteConfirmOpen" max-width="400">
      <v-card>
        <v-card-title>
          <v-icon color="error" class="mr-2">mdi-alert-circle-outline</v-icon>
          Confirm Delete
        </v-card-title>
        <v-card-text>{{ deleteConfirmMessage }}</v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="deleteConfirmOpen = false">Cancel</v-btn>
          <v-btn color="error" variant="tonal" @click="confirmDelete">Delete</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </div>
</template>

<style scoped>
.settings-view {
  height: 100%;
  overflow-y: auto;
  max-width: 800px;
}

.section-header {
  margin-top: 8px;
}
</style>
