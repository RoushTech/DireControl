<script setup lang="ts">
import { computed, ref } from 'vue'
import { useHealthStore, agoShort, durationShort } from '@/stores/health'
import { usePacketHubStore } from '@/stores/packetHub'
import { useRadiosStore } from '@/stores/radiosStore'
import { useToastStore } from '@/stores/toastStore'
import { modemStateLabels } from '@/api/modemApi'
import { AprsIsStates, aprsIsStateLabels } from '@/api/statusApi'
import { formatTimeOfDay } from '@/utils/time'
import { apiErrorDetail } from '@/api/axios'
import { levelColor } from './healthUi'

/**
 * The detail behind the status strip: one group per lane with the raw facts
 * it was judged on, and the action that fixes it. Tap/click to open — never
 * hover (review 1.20).
 */
const emit = defineEmits<{ close: [] }>()

const healthStore = useHealthStore()
const hub = usePacketHubStore()
const radiosStore = useRadiosStore()
const toast = useToastStore()

const h = computed(() => healthStore.health)
const s = computed(() => healthStore.status)
const now = computed(() => healthStore.now)
const isReconnecting = ref(false)
const hubReconnecting = ref(false)

// While the feed is down the radios store has dropped live status; show the
// values as of the drop rather than "no status".
const frozenModem = (id: string) =>
  h.value.live ? undefined : radiosStore.lastKnownModemStatus[id]

const ms = (iso: string | null | undefined) => (iso ? new Date(iso).getTime() : null)
const ago = (iso: string | null | undefined) => {
  const t = ms(iso)
  return t == null ? 'never' : `${agoShort(t, now.value)} ago`
}

const asOf = computed(() =>
  hub.downSince != null ? formatTimeOfDay(hub.downSince, { seconds: true }) : null,
)

const radioRows = computed(() =>
  h.value.radios.map((rh) => {
    const m = radiosStore.modemStatus[rh.radioId] ?? frozenModem(rh.radioId)
    const carrier = m?.carrierDetected
      ? m.carrierSince
        ? `held ${durationShort(now.value - new Date(m.carrierSince).getTime())}`
        : 'detected'
      : 'quiet'
    return {
      ...rh,
      rows: m
        ? [
            ['Last RF decode', ago(m.lastDecodedAt)],
            ['Modem', modemStateLabels[m.state] ?? 'Unknown'],
            ['Audio level', m.audioLevel.toFixed(3) + (m.audioLevel < 0.02 ? ' (low)' : '')],
            ['Carrier detect', carrier],
            ['Decoded / bad CRC', `${m.decodedFrames.toLocaleString()} / ${m.invalidFrames}`],
          ]
        : [['Modem', 'no status received']],
      txRows: m
        ? [
            ['Queue depth', String(m.txQueueDepth)],
            ['Last TX', ago(m.lastTxAt)],
            [
              'CSMA waiting',
              m.csmaWaitingSince
                ? durationShort(now.value - new Date(m.csmaWaitingSince).getTime())
                : '—',
            ],
            ['Discarded (channel timeout)', String(m.csmaTimeouts)],
            ['Lane drops', String(m.txDropped)],
            ...(m.txError ? [['Last TX error', m.txError]] : []),
          ]
        : [],
    }
  }),
)

async function reconnectIs() {
  isReconnecting.value = true
  try {
    await healthStore.reconnectAprsIs()
    toast.toast('APRS-IS reconnect requested', 'info')
  } catch (e) {
    toast.toast(apiErrorDetail(e, 'Could not ask APRS-IS to reconnect'), 'error')
  } finally {
    isReconnecting.value = false
  }
}

async function reconnectHub() {
  hubReconnecting.value = true
  try {
    await hub.reconnectNow()
  } finally {
    hubReconnecting.value = false
  }
}
</script>

<template>
  <v-card class="status-panel" min-width="300" max-width="420">
    <v-card-item>
      <template #prepend>
        <v-icon :color="levelColor(h.worst)" icon="mdi-circle" size="14" />
      </template>
      <v-card-title class="text-subtitle-1">{{ h.label }}</v-card-title>
      <template #append>
        <v-btn
          icon="mdi-close"
          variant="text"
          size="small"
          aria-label="Close status"
          @click="emit('close')"
        />
      </template>
    </v-card-item>
    <v-card-text class="pt-0 text-body-2 text-medium-emphasis">
      Worst of the four lanes below. RF and APRS-IS are judged separately, so internet traffic can't
      hide a deaf receiver.
    </v-card-text>

    <v-divider />

    <!-- Live feed -->
    <section class="sp-group" aria-label="Live feed">
      <div class="sp-head">
        <v-icon :color="levelColor(h.lanes.link.level)" icon="mdi-circle" size="10" />
        <h4>Live feed</h4>
        <v-chip size="small" variant="tonal" :color="levelColor(h.lanes.link.level)">
          {{ h.lanes.link.value }}
        </v-chip>
      </div>
      <p class="sp-detail">{{ h.lanes.link.detail }}</p>
      <div v-if="!h.live" class="sp-actions">
        <v-btn size="small" variant="tonal" :loading="hubReconnecting" @click="reconnectHub">
          Reconnect now
        </v-btn>
      </div>
    </section>

    <!-- RF receive, per radio -->
    <section v-if="radioRows.length === 0" class="sp-group" aria-label="RF receive">
      <div class="sp-head">
        <v-icon :color="levelColor(h.lanes.rf.level)" icon="mdi-circle" size="10" />
        <h4>RF receive</h4>
        <v-chip size="small" variant="tonal" :color="levelColor(h.lanes.rf.level)">
          {{ h.lanes.rf.value }}
        </v-chip>
      </div>
      <p class="sp-detail">{{ h.lanes.rf.detail }}</p>
      <div class="sp-actions">
        <v-btn size="small" variant="tonal" color="primary" to="/setup" @click="emit('close')">
          Add a radio
        </v-btn>
      </div>
    </section>
    <section
      v-for="r in radioRows"
      :key="r.radioId"
      class="sp-group"
      :aria-label="`RF receive, ${r.name}`"
    >
      <div class="sp-head">
        <v-icon :color="levelColor(h.live ? r.rf.level : 'off')" icon="mdi-circle" size="10" />
        <h4>RF receive · {{ r.name }}</h4>
        <v-chip size="small" variant="tonal" :color="levelColor(h.live ? r.rf.level : 'off')">
          {{ r.rf.value }}
        </v-chip>
      </div>
      <p class="sp-detail">
        {{ r.rf.detail }}
        <strong v-if="!h.live && asOf" class="text-warning"> · as of {{ asOf }}</strong>
      </p>
      <dl class="sp-kv">
        <template v-for="[k, v] in r.rows" :key="k">
          <dt>{{ k }}</dt>
          <dd>{{ v }}</dd>
        </template>
      </dl>
      <div class="sp-actions">
        <v-btn size="small" variant="tonal" to="/radio" @click="emit('close')">Open Radio</v-btn>
        <v-btn size="small" variant="text" to="/radio/diagnostics" @click="emit('close')">
          Diagnostics
        </v-btn>
      </div>
    </section>

    <!-- APRS-IS -->
    <section class="sp-group" aria-label="APRS-IS">
      <div class="sp-head">
        <v-icon :color="levelColor(h.lanes.is.level)" icon="mdi-circle" size="10" />
        <h4>APRS-IS</h4>
        <v-chip size="small" variant="tonal" :color="levelColor(h.lanes.is.level)">
          {{ h.lanes.is.value }}
        </v-chip>
      </div>
      <p class="sp-detail">{{ h.lanes.is.detail }}</p>
      <dl v-if="s && s.aprsIsState !== AprsIsStates.Disabled" class="sp-kv">
        <dt>State</dt>
        <dd>{{ aprsIsStateLabels[s.aprsIsState] }}</dd>
        <dt>Last IS packet</dt>
        <dd>{{ ago(s.lastIsPacketAt) }}</dd>
        <dt>Server</dt>
        <dd>{{ s.aprsIsServerName ?? '—' }}</dd>
        <dt>Filter</dt>
        <dd class="font-mono">{{ s.aprsIsFilter || '—' }}</dd>
        <dt>Session packets</dt>
        <dd>{{ s.aprsIsSessionPacketCount.toLocaleString() }}</dd>
        <dt>Failed attempts</dt>
        <dd>{{ s.aprsIsFailedAttempts }}</dd>
        <template v-if="s.aprsIsLastError">
          <dt>Last error</dt>
          <dd class="text-error">{{ s.aprsIsLastError }}</dd>
        </template>
      </dl>
      <div class="sp-actions">
        <template v-if="s && s.aprsIsState !== AprsIsStates.Disabled">
          <v-btn size="small" variant="tonal" :loading="isReconnecting" @click="reconnectIs">
            Reconnect APRS-IS
          </v-btn>
          <v-btn size="small" variant="text" to="/settings/aprsis" @click="emit('close')">
            Settings
          </v-btn>
        </template>
        <v-btn v-else size="small" variant="tonal" to="/settings/aprsis" @click="emit('close')">
          Turn on APRS-IS
        </v-btn>
      </div>
    </section>

    <!-- Transmit -->
    <section class="sp-group" aria-label="Transmit">
      <div class="sp-head">
        <v-icon :color="levelColor(h.lanes.tx.level)" icon="mdi-circle" size="10" />
        <h4>Transmit</h4>
        <v-chip size="small" variant="tonal" :color="levelColor(h.lanes.tx.level)">
          {{ h.lanes.tx.value }}
        </v-chip>
      </div>
      <p class="sp-detail">{{ h.txBlockedReason ?? h.lanes.tx.detail }}</p>
      <template v-for="r in radioRows" :key="`tx-${r.radioId}`">
        <dl v-if="r.txRows.length && r.tx.level !== 'off'" class="sp-kv">
          <dt class="font-weight-medium">{{ r.name }}</dt>
          <dd>{{ r.tx.value }}</dd>
          <template v-for="[k, v] in r.txRows" :key="k">
            <dt>{{ k }}</dt>
            <dd>{{ v }}</dd>
          </template>
        </dl>
      </template>
    </section>
  </v-card>
</template>

<style scoped>
.status-panel {
  max-height: min(80vh, 760px);
  overflow-y: auto;
}
.sp-group {
  padding: 12px 16px;
  border-bottom: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}
.sp-group:last-child {
  border-bottom: 0;
}
.sp-head {
  display: flex;
  align-items: center;
  gap: 8px;
}
.sp-head h4 {
  flex: 1;
  font-size: 0.875rem;
  font-weight: 600;
  margin: 0;
}
.sp-detail {
  margin: 4px 0 8px;
  font-size: 0.8125rem;
  color: rgba(var(--v-theme-on-surface), var(--v-medium-emphasis-opacity));
}
.sp-kv {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 2px 12px;
  font-size: 0.8125rem;
  margin: 0 0 8px;
}
.sp-kv dt {
  color: rgba(var(--v-theme-on-surface), var(--v-medium-emphasis-opacity));
}
.sp-kv dd {
  margin: 0;
  text-align: right;
  overflow-wrap: anywhere;
}
.sp-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.font-mono {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
}
</style>
