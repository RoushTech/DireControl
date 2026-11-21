<script setup lang="ts">
import { computed } from 'vue'
import { useDisplay } from 'vuetify'
import { storeToRefs } from 'pinia'
import { useTxStore } from '@/stores/tx'
import { useUiStore } from '@/stores/uiStore'
import { useHealthStore } from '@/stores/health'
import { txFrameKindLabels, TxFrameKinds, TxFrameStates } from '@/api/txApi'
import { useMessagesStore } from '@/stores/messagesStore'
import { RetryState } from '@/types/message'
import { formatTimeOfDay } from '@/utils/time'
import TxLifecycle from './TxLifecycle.vue'

/**
 * Every frame DireControl sends, with its real state (review 1.2). Right-hand
 * drawer on desktop, bottom sheet on a phone; opened from the app-bar TX chip
 * and from toasts.
 */
const tx = useTxStore()
const ui = useUiStore()
const health = useHealthStore()
const messages = useMessagesStore()

/** Message parts: the ACK outcome lives on the message row. */
function messageState(it: (typeof items.value)[number]) {
  if (it.kind !== TxFrameKinds.Message || !it.ref) return null
  return messages.inboxMessages.find(
    (m) => m.messageId === it.ref && m.retryState !== RetryState.Pending,
  )?.retryState
}
const { txSheetOpen } = storeToRefs(ui)
const { width } = useDisplay()
const phone = computed(() => width.value < 600)

const items = computed(() => tx.list.slice(0, 40))

function heardLine(it: (typeof items.value)[number]): string | null {
  if (it.outcome !== 'heard' || !it.sentAt) return null
  const secs = it.heardAt
    ? ((new Date(it.heardAt).getTime() - new Date(it.sentAt).getTime()) / 1000).toFixed(1)
    : null
  return `Heard${it.heardVia ? ` via ${it.heardVia}` : ''}${secs ? ` · ${secs} s after unkey` : ''}`
}

function unverifiedLine(it: (typeof items.value)[number]): string | null {
  if (it.outcome !== 'unverified') return null
  return health.health.lanes.rf.level === 'warn' || health.health.lanes.rf.level === 'err'
    ? 'Sent, but no digipeater echo heard within 30 s — your receiver may be deaf, so this is unverified.'
    : 'Sent, but no digipeater echo heard within 30 s.'
}
</script>

<template>
  <component
    :is="phone ? 'v-bottom-sheet' : 'v-navigation-drawer'"
    v-model="txSheetOpen"
    v-bind="phone ? { scrollable: true } : { location: 'right', temporary: true, width: 420 }"
  >
    <v-card class="tx-sheet" :rounded="phone ? 't-lg' : 0" flat>
      <v-card-item>
        <v-card-title class="text-subtitle-1">Transmit queue</v-card-title>
        <template #append>
          <v-chip size="small" variant="tonal" class="mr-2">{{ tx.pending.length }} active</v-chip>
          <v-btn
            icon="mdi-close"
            variant="text"
            size="small"
            aria-label="Close transmit queue"
            @click="txSheetOpen = false"
          />
        </template>
      </v-card-item>
      <v-card-text class="pt-0 text-body-2 text-medium-emphasis">
        Every frame DireControl sends, with its real state. <strong>Queued</strong> isn't sent; it's
        sent once it's <strong>on air</strong>.
      </v-card-text>
      <v-alert
        v-if="health.health.txBlockedReason"
        type="warning"
        variant="tonal"
        density="compact"
        class="mx-4 mb-2"
      >
        {{ health.health.txBlockedReason }}
      </v-alert>
      <v-divider />
      <div class="tx-list">
        <v-empty-state
          v-if="items.length === 0"
          icon="mdi-radio-tower"
          title="Nothing sent this session"
          text="Beacons, messages, ACKs and digipeats appear here from queued to on air to confirmed."
        />
        <div v-for="it in items" :key="it.id" class="tx-row">
          <div class="tx-top">
            <v-chip size="x-small" label variant="tonal">{{ txFrameKindLabels[it.kind] }}</v-chip>
            <strong class="tx-title">{{ it.title }}</strong>
            <span class="text-caption text-medium-emphasis">{{
              formatTimeOfDay(it.queuedAt)
            }}</span>
          </div>
          <div v-if="it.detail" class="tx-detail">
            {{ it.detail }}<span v-if="it.radioName"> · {{ it.radioName }}</span>
          </div>
          <TxLifecycle
            :item="it"
            :acked="messageState(it) === RetryState.Acknowledged"
            :gave-up="messageState(it) === RetryState.Failed"
            class="mt-1"
          />
          <div
            v-if="it.reason"
            class="tx-why"
            :class="
              it.state === TxFrameStates.Failed || it.state === TxFrameStates.Dropped
                ? 'text-error'
                : ''
            "
          >
            {{ it.reason }}
          </div>
          <div v-if="heardLine(it)" class="tx-why text-success">{{ heardLine(it) }}</div>
          <div v-if="unverifiedLine(it)" class="tx-why text-warning">{{ unverifiedLine(it) }}</div>
        </div>
      </div>
    </v-card>
  </component>
</template>

<style scoped>
.tx-sheet {
  display: flex;
  flex-direction: column;
  height: 100%;
}
.tx-list {
  overflow-y: auto;
  flex: 1 1 auto;
  max-height: 100%;
}
.tx-row {
  padding: 10px 16px;
  border-bottom: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}
.tx-top {
  display: flex;
  align-items: center;
  gap: 8px;
}
.tx-title {
  flex: 1;
  font-size: 0.875rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tx-detail {
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  font-size: 0.6875rem;
  color: rgba(var(--v-theme-on-surface), var(--v-medium-emphasis-opacity));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tx-why {
  font-size: 0.75rem;
  margin-top: 4px;
}
</style>
