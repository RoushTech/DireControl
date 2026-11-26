<script setup lang="ts">
import { computed, ref } from 'vue'
import { apiErrorDetail } from '@/api/axios'
import { useMessagesStore } from '@/stores/messagesStore'
import { useToastStore } from '@/stores/toastStore'
import { RetryState, type InboxMessageDto } from '@/types/message'
import { formatDateTime, timeAgo } from '@/utils/time'
import MessagePartState from './MessagePartState.vue'
import { isResendable, type PartRow } from './messageParts'

/**
 * One outbound compose that went out as several numbered APRS messages.
 * Parts go one at a time (each waits for the previous ACK), so every part
 * gets its own state row and the bubble sums them up ("2 of 3 delivered").
 */
const props = defineProps<{
  parts: PartRow[]
  total: number
  now: number
  actionLoading: Record<number, 'retry' | 'reset' | 'cancel' | null>
}>()

const emit = defineEmits<{
  retry: [msg: InboxMessageDto]
  cancel: [msg: InboxMessageDto]
}>()

const store = useMessagesStore()
const toasts = useToastStore()

const first = computed(() => props.parts.find((p) => p.msg)!.msg!)
const fullText = computed(() =>
  props.parts
    .map((p) => p.text)
    .filter(Boolean)
    .join(' '),
)
const pathUsed = computed(() => first.value.pathUsed)

const delivered = computed(
  () => props.parts.filter((p) => p.msg?.retryState === RetryState.Acknowledged).length,
)
const notQueued = computed(() => props.parts.filter((p) => !p.msg).length)
const resendable = computed(() => props.parts.map((p) => p.msg).filter(isResendable))
const failedCount = computed(
  () => props.parts.filter((p) => p.msg?.retryState === RetryState.Failed).length,
)

const summaryColor = computed(() => {
  if (delivered.value === props.total) return 'text-success'
  if (failedCount.value > 0 || notQueued.value > 0) return 'text-error'
  return 'text-medium-emphasis'
})

// Retry each missing part in order; the server holds later parts behind the
// earlier ones' ACKs, so order matters.
const resending = ref(false)
async function resendMissing() {
  if (resending.value) return
  resending.value = true
  try {
    for (const msg of resendable.value) {
      try {
        await store.retryNow(msg.id)
      } catch (e) {
        const n = props.parts.find((p) => p.msg?.id === msg.id)?.n
        toasts.toast(apiErrorDetail(e, `Couldn't resend part ${n}/${props.total}`), 'error')
        break
      }
    }
  } finally {
    resending.value = false
  }
}
</script>

<template>
  <div class="mpb">
    <div class="mpb-body">{{ fullText }}</div>
    <div class="mpb-meta">
      <span class="mono-time" :title="formatDateTime(first.receivedAt)">
        {{ timeAgo(first.receivedAt, now) }}
      </span>
      <span>sent as {{ total }} messages via</span>
      <span class="route-path">{{ pathUsed ? pathUsed.split(',').join(' › ') : 'direct' }}</span>
    </div>

    <ol class="mpb-parts" :aria-label="`${total} parts`">
      <li v-for="p in parts" :key="p.n" class="mpb-part">
        <span class="mpb-n">{{ p.n }}/{{ total }}</span>
        <div class="mpb-state">
          <MessagePartState v-if="p.msg" :msg="p.msg" :part-no="p.n" :now="now" />
          <span v-else class="text-caption text-error">
            Never queued: the transmitter dropped out mid-send
          </span>
          <div
            v-if="
              p.msg &&
              (p.msg.retryState === RetryState.Failed || p.msg.retryState === RetryState.Retrying)
            "
            class="mpb-actions"
          >
            <v-btn
              size="small"
              variant="text"
              color="primary"
              :loading="actionLoading[p.msg.id] === 'retry'"
              :disabled="!!actionLoading[p.msg.id] || resending"
              @click="emit('retry', p.msg)"
            >
              Retry now
            </v-btn>
            <v-btn
              v-if="p.msg.retryState === RetryState.Retrying"
              size="small"
              variant="text"
              color="error"
              :loading="actionLoading[p.msg.id] === 'cancel'"
              :disabled="!!actionLoading[p.msg.id] || resending"
              @click="emit('cancel', p.msg)"
            >
              Cancel
            </v-btn>
          </div>
        </div>
      </li>
    </ol>

    <div class="mpb-summary">
      <span class="text-caption font-weight-medium" :class="summaryColor" role="status">
        {{ delivered }} of {{ total }} parts delivered
      </span>
      <v-spacer />
      <v-btn
        v-if="resendable.length > 0"
        size="small"
        variant="tonal"
        color="primary"
        prepend-icon="mdi-send-outline"
        :loading="resending"
        @click="resendMissing"
      >
        Resend missing parts
      </v-btn>
    </div>
    <div v-if="notQueued > 0" class="text-caption text-medium-emphasis mt-1">
      {{ notQueued }} part{{ notQueued === 1 ? ' was' : 's were' }} never queued. Send the rest
      again as a new message once TX is back.
    </div>
  </div>
</template>

<style scoped>
.mpb-body {
  white-space: pre-wrap;
  word-break: break-word;
}

.mpb-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
  margin-top: 4px;
  font-size: 0.72rem;
  color: rgba(var(--v-theme-on-surface), 0.6);
}

.mono-time {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-variant-numeric: tabular-nums;
}

.route-path {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  word-break: break-all;
}

.mpb-parts {
  list-style: none;
  margin: 6px 0 0;
  padding: 0;
  border-top: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}

.mpb-part {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 6px 0;
  border-bottom: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}

.mpb-n {
  flex: 0 0 auto;
  min-width: 2.6em;
  padding-top: 1px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.75rem;
  font-weight: 600;
  color: rgba(var(--v-theme-on-surface), 0.7);
}

.mpb-state {
  flex: 1 1 auto;
  min-width: 0;
}

.mpb-actions {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 4px;
  margin-top: 2px;
}

.mpb-summary {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
}
</style>
