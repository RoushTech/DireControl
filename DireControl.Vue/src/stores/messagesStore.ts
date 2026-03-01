import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  getAllMessages,
  getInboxMessages,
  markMessageRead,
  sendMessage,
} from '@/api/messagesApi'
import type {
  AllMessagePacketDto,
  InboxMessageDto,
  MessageAckDto,
  SendMessageRequest,
} from '@/types/message'

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

  return {
    inboxMessages,
    allMessages,
    loading,
    unreadCount,
    fetchInbox,
    fetchAll,
    markRead,
    send,
    onMessageReceived,
    onMessageAcked,
  }
})
