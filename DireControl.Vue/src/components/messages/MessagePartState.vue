<script setup lang="ts">
import { computed } from 'vue'
import TxLifecycle from '@/components/tx/TxLifecycle.vue'
import { TxFrameKinds } from '@/api/txApi'
import { useTxStore } from '@/stores/tx'
import { RetryState, type InboxMessageDto } from '@/types/message'
import { isHeld, retryBadge } from './messageParts'

/**
 * One outbound message (or part) as the operator needs it: held behind the
 * previous part's ACK, the TX lifecycle of its latest frame, or the final
 * outcome (acked / gave up / cancelled).
 */
const props = defineProps<{
  msg: InboxMessageDto
  /** Part number in a multi-part message; drives the "held" wording. */
  partNo?: number | null
  now: number
}>()

const txStore = useTxStore()

const frame = computed(() =>
  props.msg.messageId ? txStore.latestForRef(TxFrameKinds.Message, props.msg.messageId) : undefined,
)

const held = computed(() => isHeld(props.msg))
const badge = computed(() => retryBadge(props.msg))

const nextRetrySeconds = computed(() => {
  if (props.msg.retryState !== RetryState.Retrying || !props.msg.nextRetryAt) return null
  return Math.max(0, Math.round((new Date(props.msg.nextRetryAt).getTime() - props.now) / 1000))
})

/** Short outcome line shown under the lifecycle once the attempt is settled. */
const outcome = computed<{ color: string; text: string } | null>(() => {
  switch (props.msg.retryState) {
    case RetryState.Failed:
      return {
        color: 'text-error',
        text: `Gave up after ${props.msg.retryCount} attempts with no ACK`,
      }
    case RetryState.Cancelled:
      return { color: 'text-medium-emphasis', text: 'Cancelled, no more retries' }
    case RetryState.Retrying:
      return {
        color: 'text-medium-emphasis',
        text:
          `Attempt ${props.msg.retryCount + 1} of ${props.msg.maxRetries}` +
          (nextRetrySeconds.value !== null ? `, next in ${nextRetrySeconds.value}s` : ''),
      }
    default:
      return null
  }
})
</script>

<template>
  <div class="mps">
    <div v-if="held" class="mps-line">
      <v-chip size="x-small" color="info" variant="tonal" label>Held</v-chip>
      <span class="text-caption text-medium-emphasis">
        {{
          partNo && partNo > 1 ? `waits for part ${partNo - 1}'s ACK` : 'waits for the previous ACK'
        }}
      </span>
    </div>
    <template v-else-if="frame">
      <TxLifecycle
        :item="frame"
        compact
        :acked="msg.retryState === RetryState.Acknowledged"
        :gave-up="msg.retryState === RetryState.Failed"
      />
      <div
        v-if="frame.reason && msg.retryState !== RetryState.Acknowledged"
        class="text-caption text-error"
      >
        {{ frame.reason }}
      </div>
      <div v-if="outcome" class="text-caption" :class="outcome.color">{{ outcome.text }}</div>
    </template>
    <div v-else class="mps-line">
      <v-chip size="x-small" :color="badge.color" variant="tonal" label>{{ badge.text }}</v-chip>
      <span v-if="nextRetrySeconds !== null" class="text-caption text-medium-emphasis">
        next in {{ nextRetrySeconds }}s
      </span>
    </div>
  </div>
</template>

<style scoped>
.mps {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.mps-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}
</style>
