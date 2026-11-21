<script setup lang="ts">
/**
 * Settings shell: section nav (rail on desktop, scrollable strip on narrow
 * screens) and the active section, one route per section (/settings/:section).
 * Each section component owns its own state, load and save.
 */
import { computed, type Component } from 'vue'
import { useRoute } from 'vue-router'
import { useDisplay } from 'vuetify'
import { AprsIsStates } from '@/api/statusApi'
import { useHealthStore } from '@/stores/health'
import {
  DEFAULT_SETTINGS_SECTION,
  SETTINGS_SECTIONS,
  isSettingsSection,
  settingsSection,
} from '@/views/settings/sections'
import StationSection from '@/views/settings/StationSection.vue'
import RadiosSection from '@/views/settings/RadiosSection.vue'
import RfSection from '@/views/settings/RfSection.vue'
import PacketSection from '@/views/settings/PacketSection.vue'
import AprsIsSection from '@/views/settings/AprsIsSection.vue'
import MapSection from '@/views/settings/MapSection.vue'
import ZonesSection from '@/views/settings/ZonesSection.vue'
import MaintenanceSection from '@/views/settings/MaintenanceSection.vue'
import DisplaySection from '@/views/settings/DisplaySection.vue'

const COMPONENTS: Record<string, Component> = {
  station: StationSection,
  radios: RadiosSection,
  rf: RfSection,
  packet: PacketSection,
  aprsis: AprsIsSection,
  map: MapSection,
  zones: ZonesSection,
  maintenance: MaintenanceSection,
  display: DisplaySection,
}

const route = useRoute()
const { mdAndUp } = useDisplay()
const healthStore = useHealthStore()

const section = computed(() => {
  const s = route.params.section
  return isSettingsSection(s) ? s : DEFAULT_SETTINGS_SECTION
})
const activeMeta = computed(() => settingsSection(section.value))
const activeComponent = computed(() => COMPONENTS[section.value] ?? StationSection)

/** Sections that need attention get a red "!" in the nav. */
function needsAttention(id: string): boolean {
  if (id === 'station') return (healthStore.callsign ?? '').toUpperCase().startsWith('N0CALL')
  if (id === 'aprsis') return healthStore.status?.aprsIsState === AprsIsStates.AuthFailed
  return false
}
</script>

<template>
  <div class="settings-view">
    <div class="settings-head">
      <h1 class="text-h5">Settings</h1>
      <v-spacer />
      <v-btn variant="tonal" prepend-icon="mdi-format-list-checks" to="/setup">
        Setup checklist
      </v-btn>
    </div>

    <div class="settings-layout" :class="{ 'settings-layout--rail': mdAndUp }">
      <nav v-if="mdAndUp" class="settings-nav" aria-label="Settings sections">
        <v-list nav density="comfortable" color="primary">
          <v-list-item
            v-for="s in SETTINGS_SECTIONS"
            :key="s.id"
            :to="`/settings/${s.id}`"
            :prepend-icon="s.icon"
            :title="s.title"
            :active="s.id === section"
          >
            <template v-if="needsAttention(s.id)" #append>
              <v-badge
                color="error"
                content="!"
                inline
                :aria-label="`${s.title} needs attention`"
              />
            </template>
          </v-list-item>
        </v-list>
      </nav>

      <v-tabs
        v-else
        :model-value="section"
        show-arrows
        color="primary"
        density="comfortable"
        class="settings-strip"
        aria-label="Settings sections"
      >
        <v-tab
          v-for="s in SETTINGS_SECTIONS"
          :key="s.id"
          :value="s.id"
          :to="`/settings/${s.id}`"
          :prepend-icon="s.icon"
        >
          {{ s.title }}
          <v-badge
            v-if="needsAttention(s.id)"
            color="error"
            content="!"
            inline
            :aria-label="`${s.title} needs attention`"
          />
        </v-tab>
      </v-tabs>

      <main class="settings-content">
        <h2 class="text-h6 mb-3">{{ activeMeta.title }}</h2>
        <component :is="activeComponent" :key="section" />
      </main>
    </div>
  </div>
</template>

<style scoped>
.settings-view {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.settings-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  padding: 12px 16px 8px;
  flex-shrink: 0;
}

.settings-layout {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.settings-layout--rail {
  flex-direction: row;
}

.settings-nav {
  flex-shrink: 0;
  width: 220px;
  overflow-y: auto;
  border-right: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}

.settings-strip {
  flex-shrink: 0;
  border-bottom: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}

.settings-content {
  flex: 1;
  min-width: 0;
  min-height: 0;
  overflow-y: auto;
  overflow-x: hidden;
  padding: 16px;
}
</style>

<!-- Layout helpers shared by the section components (src/views/settings/*). -->
<style>
.settings-single {
  max-width: 900px;
}

.settings-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(420px, 100%), 1fr));
  gap: 0 24px;
  align-items: start;
  max-width: 1280px;
}

.settings-section {
  min-width: 0;
}

.section-header {
  margin-top: 8px;
}
</style>
