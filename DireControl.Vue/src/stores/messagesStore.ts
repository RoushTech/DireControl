import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  cancelMessage,
  getInboxMessages,
  markMessageRead,
  resetMessage,
  retryMessage,
  sendMessage,
} from '@/api/messagesApi'
import type {
  InboxMessageDto,
  MessageAcknowledgedDto,
  MessageFailedDto,
  MessageRetriedDto,
  SendMessageRequest,
} from '@/types/message'
import { RetryState } from '@/types/message'
import { usePacketHubStore } from '@/stores/packetHub'
import { useToastStore } from '@/stores/toastStore'

export const useMessagesStore = defineStore('messages', () => {
  const inboxMessages = ref<InboxMessageDto[]>([])
  const loading = ref(false)

  const unreadCount = computed(() => inboxMessages.value.filter((m) => !m.isRead).length)
  /** Outbound messages that gave up without an ACK — badged on the Comms nav. */
  const failedCount = computed(
    () => inboxMessages.value.filter((m) => m.retryState === RetryState.Failed).length,
  )
  const toastStore = useToastStore()

  async function fetchInbox() {
    loading.value = true
    try {
      inboxMessages.value = await getInboxMessages()
    } finally {
      loading.value = false
    }
  }

  async function markRead(id: number) {
    const updated = await markMessageRead(id)
    const idx = inboxMessages.value.findIndex((m) => m.id === id)
    if (idx !== -1) inboxMessages.value[idx] = updated
  }

  async function send(request: SendMessageRequest): Promise<InboxMessageDto[]> {
    const sent = await sendMessage(request)
    // Outbound records live alongside the inbox (threads pair them by peer).
    inboxMessages.value.unshift(...[...sent].reverse())
    return sent
  }

  async function retryNow(id: number): Promise<InboxMessageDto> {
    const updated = await retryMessage(id)
    replaceInbox(updated)
    return updated
  }

  async function resetRetry(id: number): Promise<InboxMessageDto> {
    const updated = await resetMessage(id)
    replaceInbox(updated)
    return updated
  }

  async function cancelRetry(id: number): Promise<InboxMessageDto> {
    const updated = await cancelMessage(id)
    replaceInbox(updated)
    return updated
  }

  /** Called when SignalR delivers a new inbound message addressed to us. */
  function onMessageReceived(message: InboxMessageDto) {
    const exists = inboxMessages.value.some((m) => m.id === message.id)
    if (!exists) inboxMessages.value.unshift(message)
  }

  /** Called when an existing message changes server-side (e.g. a retransmission was heard). */
  function onMessageUpdated(message: InboxMessageDto) {
    const idx = inboxMessages.value.findIndex((m) => m.id === message.id)
    if (idx !== -1) inboxMessages.value[idx] = message
    else inboxMessages.value.unshift(message)
  }

  /** Called when the retry service retransmits a message. Updates attempt info. */
  function onMessageRetried(data: MessageRetriedDto) {
    const msg = inboxMessages.value.find((m) => m.id === data.id)
    if (msg) {
      msg.retryCount = data.retryCount
      msg.maxRetries = data.maxRetries
      msg.nextRetryAt = data.nextRetryAt
      msg.lastSentAt = data.lastSentAt
      msg.retryState = RetryState.Retrying
    }
  }

  /** Called when an ACK is received — upgrades state to Acknowledged. */
  function onMessageAcknowledged(data: MessageAcknowledgedDto) {
    const msg = inboxMessages.value.find((m) => m.id === data.id)
    if (msg) {
      msg.retryState = RetryState.Acknowledged
      msg.nextRetryAt = null
      msg.ackSent = true
    }
  }

  /** Called when a message exhausts all retries and transitions to Failed. */
  function onMessageFailed(data: MessageFailedDto) {
    const msg = inboxMessages.value.find((m) => m.id === data.id)
    if (msg) {
      msg.retryState = RetryState.Failed
      msg.nextRetryAt = null
      msg.retryCount = data.retryCount
    }
    // Announced app-wide: a give-up matters on whatever screen is open.
    toastStore.toast(
      `Message to ${data.toCallsign} failed after ${data.retryCount} attempt(s) — no ACK received.`,
      'error',
      10000,
      { label: 'View', to: '/messages' },
    )
  }

  // ── Private helpers ────────────────────────────────────────────────────────

  function replaceInbox(updated: InboxMessageDto) {
    const idx = inboxMessages.value.findIndex((m) => m.id === updated.id)
    if (idx !== -1) inboxMessages.value[idx] = updated
  }

  // Store-level hub subscriptions: retry-state changes and the unread badge
  // stay live on every screen, not just while the Messages view is open.
  const hub = usePacketHubStore()
  hub.on('messageReceived', (message: InboxMessageDto) => onMessageReceived(message))
  hub.on('messageUpdated', (message: InboxMessageDto) => onMessageUpdated(message))
  hub.on('messageRetried', (data: MessageRetriedDto) => onMessageRetried(data))
  hub.on('messageAcknowledged', (data: MessageAcknowledgedDto) => onMessageAcknowledged(data))
  hub.on('messageFailed', (data: MessageFailedDto) => onMessageFailed(data))
  hub.onReconnected(() => fetchInbox())

  return {
    inboxMessages,
    loading,
    unreadCount,
    failedCount,
    fetchInbox,
    markRead,
    send,
    retryNow,
    resetRetry,
    cancelRetry,
    onMessageReceived,
    onMessageUpdated,
    onMessageRetried,
    onMessageAcknowledged,
    onMessageFailed,
  }
})
