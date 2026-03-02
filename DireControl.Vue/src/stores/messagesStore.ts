import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  cancelMessage,
  getAllMessages,
  getInboxMessages,
  markMessageRead,
  resetMessage,
  retryMessage,
  sendMessage,
} from '@/api/messagesApi'
import type {
  AllMessagePacketDto,
  InboxMessageDto,
  MessageAcknowledgedDto,
  MessageAckDto,
  MessageFailedDto,
  MessageRetriedDto,
  SendMessageRequest,
} from '@/types/message'
import { RetryState } from '@/types/message'

export const useMessagesStore = defineStore('messages', () => {
  const inboxMessages = ref<InboxMessageDto[]>([])
  const allMessages = ref<AllMessagePacketDto[]>([])
  const loading = ref(false)

  const unreadCount = computed(
    () => inboxMessages.value.filter((m) => !m.isRead).length,
  )

  async function fetchInbox() {
    loading.value = true
    try {
      inboxMessages.value = await getInboxMessages()
    } finally {
      loading.value = false
    }
  }

  async function fetchAll() {
    allMessages.value = await getAllMessages()
  }

  async function markRead(id: number) {
    const updated = await markMessageRead(id)
    const idx = inboxMessages.value.findIndex((m) => m.id === id)
    if (idx !== -1) inboxMessages.value[idx] = updated
  }

  async function send(request: SendMessageRequest): Promise<InboxMessageDto> {
    const sent = await sendMessage(request)
    // Sent messages that are TO us appear in inbox; others go to "all messages" eventually,
    // but we optimistically add them to inbox as outbound records.
    inboxMessages.value.unshift(sent)
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

  /** Called when SignalR delivers an ACK for one of our sent messages. */
  function onMessageAcked(ack: MessageAckDto) {
    const msg = inboxMessages.value.find((m) => m.id === ack.id)
    if (msg) msg.ackSent = true
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
  }

  // ── Private helpers ────────────────────────────────────────────────────────

  function replaceInbox(updated: InboxMessageDto) {
    const idx = inboxMessages.value.findIndex((m) => m.id === updated.id)
    if (idx !== -1) inboxMessages.value[idx] = updated
  }

  return {
    inboxMessages,
    allMessages,
    loading,
    unreadCount,
    fetchInbox,
    fetchAll,
    markRead,
    send,
    retryNow,
    resetRetry,
    cancelRetry,
    onMessageReceived,
    onMessageAcked,
    onMessageRetried,
    onMessageAcknowledged,
    onMessageFailed,
  }
})
