<script setup lang="ts">
import { computed } from 'vue'
import {
  terminalSessionOriginLabels,
  terminalSessionStateLabels,
  TerminalSessionStates,
  type TerminalSessionDto,
  type TerminalSessionStatsDto,
} from '@/api/terminalApi'

const props = defineProps<{
  session: TerminalSessionDto
  stats: TerminalSessionStatsDto | null
}>()

const stateColor = computed(() => {
  switch (props.session.state) {
    case TerminalSessionStates.Connecting:
      return 'orange'
    case TerminalSessionStates.Connected:
      return 'green'
    case TerminalSessionStates.Error:
      return 'red'
    default:
      return 'grey'
  }
})

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  let size = bytes
  let u = 0
  while (size >= 1024 && u < units.length - 1) {
    size /= 1024
    u++
  }
  return u === 0 ? `${size} B` : `${size.toFixed(1)} ${units[u]}`
}
</script>

<template>
  <div class="telemetry-panel pa-3">
    <div class="d-flex align-center ga-2 mb-2">
      <v-chip :color="stateColor" size="x-small" variant="flat" label>
        {{ terminalSessionStateLabels[session.state] }}
      </v-chip>
      <span class="text-caption text-medium-emphasis">
        {{ terminalSessionOriginLabels[session.origin] }}
      </span>
    </div>

    <div class="text-caption text-medium-emphasis mb-2">
      {{ session.localCallsign }} ⇄ {{ session.remoteCallsign
      }}<template v-if="session.digiPath"> via {{ session.digiPath }}</template> · ch
      {{ session.channel }}
    </div>

    <v-table density="compact" class="telemetry-table">
      <tbody>
        <tr>
          <td>V(S) / V(R) / V(A)</td>
          <td class="text-right mono">
            {{ stats ? `${stats.vs} / ${stats.vr} / ${stats.va}` : '—' }}
          </td>
        </tr>
        <tr>
          <td>Outstanding I-frames</td>
          <td class="text-right mono">{{ stats?.outstandingIFrames ?? '—' }}</td>
        </tr>
        <tr>
          <td>Retries</td>
          <td class="text-right mono">{{ stats?.retryCount ?? '—' }}</td>
        </tr>
        <tr>
          <td>Send queue</td>
          <td class="text-right mono">{{ stats?.sendQueueDepth ?? '—' }}</td>
        </tr>
        <tr>
          <td>Bytes in</td>
          <td class="text-right mono">{{ stats ? formatBytes(stats.bytesIn) : '—' }}</td>
        </tr>
        <tr>
          <td>Bytes out</td>
          <td class="text-right mono">{{ stats ? formatBytes(stats.bytesOut) : '—' }}</td>
        </tr>
        <tr>
          <td>Started</td>
          <td class="text-right mono">{{ new Date(session.startedAt).toLocaleTimeString() }}</td>
        </tr>
        <tr v-if="session.endReason">
          <td>End reason</td>
          <td class="text-right">{{ session.endReason }}</td>
        </tr>
      </tbody>
    </v-table>
  </div>
</template>

<style scoped>
.telemetry-panel {
  width: 260px;
  flex-shrink: 0;
  border-left: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  overflow-y: auto;
}

.telemetry-table :deep(td) {
  font-size: 0.78rem;
  height: 30px !important;
}

.mono {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  font-variant-numeric: tabular-nums;
}
</style>
