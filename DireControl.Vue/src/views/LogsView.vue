<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { createHubConnection } from '@/composables/useSignalR'
import { useLogStreamStore } from '@/stores/logStream'
import {
  LOG_LEVEL_COLORS,
  shortCategory,
  type LogEntryDto,
} from '@/types/log'
import { getLogLevels, setLogLevel } from '@/api/loggingApi'

const route = useRoute()
const store = useLogStreamStore()

const hub = createHubConnection('/hubs/logs', {
  logBacklog: (entries: LogEntryDto[]) => store.seedBacklog(entries),
  logReceived: (entry: LogEntryDto) => store.addLog(entry),
})
const connectionStatus = hub.status

const selectedEntry = ref<LogEntryDto | null>(null)

// Minimum-level filter (shows the chosen level and everything more severe).
const levelOptions = [
  { label: 'All Levels', value: '' },
  { label: 'Debug+', value: 'Debug' },
  { label: 'Info+', value: 'Information' },
  { label: 'Warning+', value: 'Warning' },
  { label: 'Error+', value: 'Error' },
]

function levelColor(level: string): string {
  return LOG_LEVEL_COLORS[level] ?? 'grey'
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  const hh = String(d.getHours()).padStart(2, '0')
  const mm = String(d.getMinutes()).padStart(2, '0')
  const ss = String(d.getSeconds()).padStart(2, '0')
  const ms = String(d.getMilliseconds()).padStart(3, '0')
  return `${hh}:${mm}:${ss}.${ms}`
}

function openDetail(entry: LogEntryDto) {
  selectedEntry.value = entry
}

// Runtime log-level editor
const levelsDialog = ref(false)
const levelsLoading = ref(false)
const levelsSaving = ref(false)
const availableLevels = ref<string[]>([])
const commonCategories = ref<string[]>([])
// category -> override level ('' means inherit appsettings default)
const overrides = ref<Record<string, string>>({})

const INHERIT = ''

const levelSelectItems = computed(() => [
  { title: 'Inherit (default)', value: INHERIT },
  ...availableLevels.value.map(l => ({ title: l, value: l })),
])

// Show the common categories plus any extra categories that already have an override.
const editorCategories = computed(() => {
  const set = new Set<string>(commonCategories.value)
  for (const c of Object.keys(overrides.value)) set.add(c)
  return [...set]
})

async function loadLevels() {
  levelsLoading.value = true
  try {
    const data = await getLogLevels()
    availableLevels.value = data.availableLevels
    commonCategories.value = data.commonCategories
    overrides.value = Object.fromEntries(data.overrides.map(o => [o.category, o.level]))
  } finally {
    levelsLoading.value = false
  }
}

async function openLevelsDialog() {
  levelsDialog.value = true
  await loadLevels()
}

async function changeLevel(category: string, level: string) {
  levelsSaving.value = true
  try {
    await setLogLevel(category, level === INHERIT ? null : level)
    if (level === INHERIT) {
      const next = { ...overrides.value }
      delete next[category]
      overrides.value = next
    } else {
      overrides.value = { ...overrides.value, [category]: level }
    }
  } finally {
    levelsSaving.value = false
  }
}

onMounted(async () => {
  await hub.start()
})

onUnmounted(async () => {
  await hub.stop()
})

function openPopOut() {
  window.open('/logs-only', '_blank', 'width=1100,height=700,noopener')
}
</script>

<template>
  <div class="log-view">
    <!-- Toolbar -->
    <div class="log-toolbar">
      <div class="log-filters">
        <v-text-field
          v-model="store.textFilter"
          placeholder="Search message or category…"
          density="compact"
          variant="outlined"
          hide-details
          clearable
          class="log-filter-input"
        />
        <v-select
          v-model="store.levelFilter"
          :items="levelOptions"
          item-title="label"
          item-value="value"
          label="Level"
          density="compact"
          variant="outlined"
          hide-details
          class="log-level-select"
        />
      </div>

      <div class="d-flex align-center ga-2">
        <v-chip
          :color="connectionStatus === 'connected' ? 'success' : connectionStatus === 'connecting' ? 'warning' : 'error'"
          size="x-small"
          variant="flat"
          label
        >
          <v-icon start size="10">mdi-circle</v-icon>
          {{ connectionStatus }}
        </v-chip>

        <v-btn
          v-if="!store.paused"
          color="primary"
          size="small"
          variant="tonal"
          prepend-icon="mdi-pause"
          @click="store.pause()"
        >
          Pause
        </v-btn>
        <v-btn
          v-else
          color="green"
          size="small"
          variant="tonal"
          @click="store.unpause()"
        >
          <v-icon start>mdi-play</v-icon>
          Unpause
          <v-badge
            v-if="store.pendingCount > 0"
            :content="store.pendingCount > 99 ? '99+' : store.pendingCount"
            color="error"
            class="ml-2"
            inline
          />
        </v-btn>

        <v-btn
          size="small"
          variant="tonal"
          prepend-icon="mdi-tune-variant"
          @click="openLevelsDialog"
        >
          Levels
        </v-btn>

        <v-btn
          size="small"
          variant="tonal"
          prepend-icon="mdi-notification-clear-all"
          @click="store.clear()"
        >
          Clear
        </v-btn>

        <v-btn
          v-if="!route.meta.isPopOut"
          icon="mdi-open-in-new"
          size="small"
          variant="tonal"
          title="Open logs in new window"
          @click="openPopOut"
        />
      </div>
    </div>

    <v-divider />

    <!-- Header row -->
    <div class="log-header">
      <span style="width: 96px" class="text-caption font-weight-medium text-medium-emphasis">Time</span>
      <span style="width: 80px" class="text-caption font-weight-medium text-medium-emphasis">Level</span>
      <span style="width: 160px" class="text-caption font-weight-medium text-medium-emphasis">Source</span>
      <span class="text-caption font-weight-medium text-medium-emphasis">Message</span>
    </div>

    <v-divider />

    <!-- Log list -->
    <div v-if="store.filteredLogs.length === 0" class="text-center text-medium-emphasis py-8">
      No logs to show yet…
    </div>
    <v-virtual-scroll
      v-else
      class="log-list"
      :items="store.filteredLogs"
      :item-height="32"
    >
      <template #default="{ item: e }">
        <div
          :key="e.sequence"
          class="log-row"
          :class="{ 'log-row--error': e.level === 'Error' || e.level === 'Critical' }"
          @click="openDetail(e)"
        >
          <span class="log-cell log-time text-caption text-medium-emphasis">{{ formatTime(e.timestamp) }}</span>
          <span class="log-cell log-level">
            <v-chip :color="levelColor(e.level)" size="x-small" label variant="flat">{{ e.level }}</v-chip>
          </span>
          <span class="log-cell log-source text-caption text-medium-emphasis text-truncate" :title="e.category">
            {{ shortCategory(e.category) }}
          </span>
          <span class="log-cell log-message text-body-2 text-truncate" :title="e.message">
            <v-icon v-if="e.exception" size="14" color="error" class="mr-1">mdi-alert-circle</v-icon>{{ e.message }}
          </span>
        </div>
      </template>
    </v-virtual-scroll>

    <!-- Detail dialog -->
    <v-dialog :model-value="selectedEntry !== null" max-width="760" @update:model-value="selectedEntry = null">
      <v-card v-if="selectedEntry">
        <v-card-title class="d-flex align-center ga-2">
          <v-chip :color="levelColor(selectedEntry.level)" size="small" label variant="flat">
            {{ selectedEntry.level }}
          </v-chip>
          <span class="text-subtitle-2">{{ shortCategory(selectedEntry.category) }}</span>
          <v-spacer />
          <span class="text-caption text-medium-emphasis">{{ formatTime(selectedEntry.timestamp) }}</span>
        </v-card-title>
        <v-card-text>
          <div class="text-caption text-medium-emphasis mb-1">{{ selectedEntry.category }}</div>
          <pre class="log-detail-text">{{ selectedEntry.message }}</pre>
          <template v-if="selectedEntry.exception">
            <v-divider class="my-3" />
            <div class="text-caption font-weight-medium text-error mb-1">Exception</div>
            <pre class="log-detail-text log-detail-exception">{{ selectedEntry.exception }}</pre>
          </template>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="selectedEntry = null">Close</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Runtime log-level editor -->
    <v-dialog v-model="levelsDialog" max-width="560">
      <v-card>
        <v-card-title class="d-flex align-center ga-2">
          <v-icon>mdi-tune-variant</v-icon>
          Log Levels
          <v-progress-circular v-if="levelsLoading || levelsSaving" indeterminate size="18" width="2" class="ml-1" />
        </v-card-title>
        <v-card-subtitle>
          Applied live to console and this stream, and saved across restarts.
        </v-card-subtitle>
        <v-card-text>
          <div
            v-for="category in editorCategories"
            :key="category"
            class="d-flex align-center ga-3 mb-2"
          >
            <code class="level-category text-truncate" :title="category">{{ category }}</code>
            <v-select
              :model-value="overrides[category] ?? INHERIT"
              :items="levelSelectItems"
              item-title="title"
              item-value="value"
              density="compact"
              variant="outlined"
              hide-details
              :disabled="levelsSaving"
              class="level-select"
              @update:model-value="(v: string) => changeLevel(category, v)"
            />
          </div>
          <div class="text-caption text-medium-emphasis mt-2">
            "Inherit" falls back to the level configured in appsettings.json.
          </div>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="levelsDialog = false">Close</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </div>
</template>

<style scoped>
.log-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

.log-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  gap: 8px;
  flex-shrink: 0;
}

.log-filters {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 8px;
}

.log-filter-input {
  flex: 1;
  min-width: 0;
}

.log-level-select {
  width: 150px;
  flex-shrink: 0;
}

.log-header {
  display: flex;
  align-items: center;
  padding: 4px 12px;
  gap: 8px;
  flex-shrink: 0;
  background: rgba(var(--v-theme-on-surface), 0.04);
}

.log-list {
  flex: 1;
  min-height: 0;
  font-family: 'JetBrains Mono', 'Fira Code', monospace;
}

.log-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 2px 12px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.06);
  min-height: 32px;
  cursor: pointer;
}

.log-row:hover {
  background: rgba(var(--v-theme-on-surface), 0.04);
}

.log-row--error {
  background: rgba(var(--v-theme-error), 0.06);
}

.log-cell {
  flex-shrink: 0;
}

.log-time {
  width: 96px;
}

.log-level {
  width: 80px;
}

.log-source {
  width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.log-message {
  flex: 1;
  min-width: 0;
  max-width: 100%;
}

.log-detail-text {
  white-space: pre-wrap;
  word-break: break-word;
  font-family: 'JetBrains Mono', 'Fira Code', monospace;
  font-size: 0.8125rem;
  margin: 0;
}

.log-detail-exception {
  color: rgba(var(--v-theme-error), 1);
  max-height: 360px;
  overflow: auto;
}

.level-category {
  flex: 1;
  min-width: 0;
  font-size: 0.8125rem;
}

.level-select {
  width: 200px;
  flex-shrink: 0;
}
</style>
