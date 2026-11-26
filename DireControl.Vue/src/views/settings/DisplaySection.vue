<script setup lang="ts">
/** Per-browser display preferences — applied immediately, nothing to save. */
import { computed } from 'vue'
import { useTheme } from 'vuetify'
import { timeZonePref, setTimeZonePref, type TimeZonePref } from '@/utils/time'
import { useUnits } from '@/composables/useUnits'
import { useUiStore, DEFAULT_RF_SILENT_MINUTES } from '@/stores/uiStore'
import SettingsSaveBar from './SettingsSaveBar.vue'
import { settingsSection } from './sections'

const meta = settingsSection('display')
const { distanceUnit, setDistanceUnit } = useUnits()
const uiStore = useUiStore()
const theme = useTheme()

// Same key App.vue reads at startup.
const THEME_STORAGE_KEY = 'direcontrol-theme'

const themeName = computed({
  get: () => (theme.global.current.value.dark ? 'dark' : 'light'),
  set: (name: 'light' | 'dark') => {
    theme.global.name.value = name
    try {
      localStorage.setItem(THEME_STORAGE_KEY, name)
    } catch {
      /* private window — lasts for this page load */
    }
  },
})

const rfSilentItems = [15, 30, 60, 120].map((m) => ({
  title: m === DEFAULT_RF_SILENT_MINUTES ? `${m} min (default)` : `${m} min`,
  value: m,
}))
</script>

<template>
  <div class="settings-single">
    <div class="section-header d-flex align-center mb-2">
      <span class="text-h6">Display</span>
    </div>

    <v-card variant="outlined" class="mb-6 pa-4">
      <div class="text-subtitle-2 font-weight-medium mb-1">Times</div>
      <v-btn-toggle
        :model-value="timeZonePref"
        mandatory
        color="primary"
        variant="outlined"
        divided
        density="comfortable"
        aria-label="Time zone"
        @update:model-value="(v: TimeZonePref) => setTimeZonePref(v)"
      >
        <v-btn value="utc" class="px-4">UTC (Z)</v-btn>
        <v-btn value="local" class="px-4">Local time</v-btn>
      </v-btn-toggle>
      <div class="text-caption text-medium-emphasis mt-1 mb-5">
        Every timestamp in the app uses this. UTC matches APRS timestamps and logs.
      </div>

      <div class="text-subtitle-2 font-weight-medium mb-1">Units</div>
      <v-radio-group
        :model-value="distanceUnit"
        hide-details
        density="compact"
        aria-label="Units"
        @update:model-value="(v) => setDistanceUnit(v as 'km' | 'mi')"
      >
        <v-radio label="Metric (km, m, km/h, °C)" value="km" />
        <v-radio label="Imperial (mi, ft, mph, °F)" value="mi" />
      </v-radio-group>
      <div class="text-caption text-medium-emphasis mt-1 mb-5">
        Distance, altitude, speed and temperature everywhere.
      </div>

      <div class="text-subtitle-2 font-weight-medium mb-1">Theme</div>
      <v-btn-toggle
        v-model="themeName"
        mandatory
        color="primary"
        variant="outlined"
        divided
        density="comfortable"
        aria-label="Theme"
        class="mb-5"
      >
        <v-btn value="light" prepend-icon="mdi-white-balance-sunny" class="px-4">Light</v-btn>
        <v-btn value="dark" prepend-icon="mdi-weather-night" class="px-4">Dark</v-btn>
      </v-btn-toggle>

      <div class="text-subtitle-2 font-weight-medium mb-1">RF silent warning after</div>
      <v-select
        v-model="uiStore.rfSilentMinutes"
        :items="rfSilentItems"
        aria-label="RF silent threshold"
        hint="A quiet rural channel may need 60 min to avoid false alarms"
        persistent-hint
        style="max-width: 240px"
      />
    </v-card>

    <SettingsSaveBar :restart="meta.restart" instant />
  </div>
</template>
