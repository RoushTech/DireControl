<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import {
  HubConnectionBuilder,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import { useMessagesStore } from '@/stores/messagesStore'
import { getSettings, getStations } from '@/api/stationsApi'
import { formatUtc, timeAgo } from '@/utils/time'
import type {
  InboxMessageDto,
  MessageAcknowledgedDto,
  MessageAckDto,
  MessageFailedDto,
  MessageRetriedDto,
} from '@/types/message'
import { RetryState } from '@/types/message'
import { type StationDto, StationType } from '@/types/station'
import { useUiStore } from '@/stores/uiStore'
import { useTick } from '@/composables/useTick'

const store = useMessagesStore()
const uiStore = useUiStore()
const { now } = useTick(1000)

// ─── Settings & stations ────────────────────────────────────────────────────
const ourCallsign = ref('')
const allStations = ref<StationDto[]>([])

// ─── Common gateways ─────────────────────────────────────────────────────────
const COMMON_GATEWAYS = ['SMSGTE', 'EMAIL', 'WLNK-1', 'ANSRVR']

function stationTypeName(t: StationType): string {
  return StationType[t] ?? 'Unknown'
}

// ─── Tabs ────────────────────────────────────────────────────────────────────
const activeTab = ref<'inbox' | 'all' | 'outbox'>('inbox')

// ─── All-messages filters ────────────────────────────────────────────────────
const filterSender = ref('')
const filterAddressee = ref('')
const filterText = ref('')

const filteredAllMessages = computed(() => {
  return store.allMessages.filter((m) => {
    if (filterSender.value && !m.fromCallsign.toLowerCase().includes(filterSender.value.toLowerCase()))
      return false
    if (filterAddressee.value && !m.toCallsign.toLowerCase().includes(filterAddressee.value.toLowerCase()))
      return false
    if (filterText.value && !m.body.toLowerCase().includes(filterText.value.toLowerCase()))
      return false
    return true
  })
})

// ─── Inbox / Outbox ──────────────────────────────────────────────────────────
const inboundMessages = computed(() =>
  store.inboxMessages.filter(
    (m) => m.fromCallsign.toUpperCase() !== ourCallsign.value.toUpperCase()
  )
)

const outboxMessages = computed(() =>
  store.inboxMessages.filter(
    (m) => m.fromCallsign.toUpperCase() === ourCallsign.value.toUpperCase()
  )
)

const actionLoading = ref<Record<number, 'retry' | 'reset' | 'cancel' | null>>({})

function secondsUntilRetry(msg: InboxMessageDto): number {
  if (!msg.nextRetryAt) return 0
  return Math.max(0, Math.round((new Date(msg.nextRetryAt).getTime() - now.value) / 1000))
}

function retryBadge(msg: InboxMessageDto): { color: string; text: string } {
  switch (msg.retryState) {
    case RetryState.Retrying:
      return { color: 'warning', text: `Attempt ${msg.retryCount + 1}/${msg.maxRetries}` }
    case RetryState.Acknowledged:
      return { color: 'success', text: 'Acknowledged' }
    case RetryState.Failed:
      return { color: 'error', text: `Failed after ${msg.retryCount} attempts` }
    case RetryState.Cancelled:
      return { color: 'default', text: 'Cancelled' }
    default:
      return { color: 'info', text: 'Pending' }
  }
}

async function doRetryNow(msg: InboxMessageDto) {
  actionLoading.value[msg.id] = 'retry'
  try {
    await store.retryNow(msg.id)
  } catch { /* ignore */ } finally {
    delete actionLoading.value[msg.id]
  }
}

// Reset confirmation dialog
const resetDialogOpen = ref(false)
const resetDialogMsg = ref<InboxMessageDto | null>(null)

function openResetDialog(msg: InboxMessageDto) {
  resetDialogMsg.value = msg
  resetDialogOpen.value = true
}

async function confirmReset() {
  const msg = resetDialogMsg.value
  if (!msg) return
  resetDialogOpen.value = false
  resetDialogMsg.value = null
  actionLoading.value[msg.id] = 'reset'
  try {
    await store.resetRetry(msg.id)
  } catch { /* ignore */ } finally {
    delete actionLoading.value[msg.id]
  }
}

async function doCancel(msg: InboxMessageDto) {
  actionLoading.value[msg.id] = 'cancel'
  try {
    await store.cancelRetry(msg.id)
  } catch { /* ignore */ } finally {
    delete actionLoading.value[msg.id]
  }
}

// Failed message toast
const failedToast = ref(false)
const failedToastText = ref('')

function showFailedToast(data: MessageFailedDto) {
  failedToastText.value = `Message to ${data.toCallsign} failed after ${data.retryCount} attempts — no ACK received.`
  failedToast.value = true
}

// ─── Compose panel ──────────────────────────────────────────────────────────
const composeOpen = ref(false)
const composeTo = ref('')
const composeBody = ref('')
const sending = ref(false)
const sendError = ref('')
const MAX_BODY = 67

const addresseeSuggestions = computed(() => {
  const q = composeTo.value?.trim().toUpperCase()
  if (!q || q.length < 2) return []
  return allStations.value
    .filter(s => s.callsign.toUpperCase().startsWith(q))
    .slice(0, 8)
})

function openCompose(prefillTo = '') {
  composeTo.value = prefillTo
  composeBody.value = ''
  sendError.value = ''
  composeOpen.value = true
}

async function doSend() {
  const to = composeTo.value?.trim().toUpperCase() ?? ''
  if (!to || !composeBody.value.trim()) return
  if (to.length > 9 || !/^[A-Z0-9-]+$/.test(to)) return
  sending.value = true
  sendError.value = ''
  try {
    await store.send({
      toCallsign: to,
      body: composeBody.value.trim().slice(0, MAX_BODY),
    })
    composeOpen.value = false
    activeTab.value = 'outbox'
  } catch {
    sendError.value = 'Failed to send. Is Direwolf connected?'
  } finally {
    sending.value = false
  }
}

// ─── Keyboard shortcut ───────────────────────────────────────────────────────
function onKeydown(e: KeyboardEvent) {
  if (
    e.key === 'm' &&
    !e.ctrlKey &&
    !e.metaKey &&
    !composeOpen.value &&
    !(e.target instanceof HTMLInputElement) &&
    !(e.target instanceof HTMLTextAreaElement)
  ) {
    openCompose()
  }
}

// Open compose when triggered via global shortcut (M key from any view)
watch(() => uiStore.pendingComposeOpen, (pending) => {
  if (pending) {
    uiStore.consumeCompose()
    openCompose()
  }
})

// ─── Inbox actions ───────────────────────────────────────────────────────────
async function onRowClick(message: InboxMessageDto) {
  if (!message.isRead) {
    await store.markRead(message.id)
  }
}

// ─── SignalR ─────────────────────────────────────────────────────────────────
let connection: HubConnection | null = null
const connectionStatus = ref<'connecting' | 'connected' | 'disconnected'>('connecting')

async function connectSignalR() {
  connectionStatus.value = 'connecting'
  connection = new HubConnectionBuilder()
    .withUrl('/hubs/packets')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  connection.onreconnecting(() => { connectionStatus.value = 'connecting' })
  connection.onreconnected(() => { connectionStatus.value = 'connected' })
  connection.onclose(() => { connectionStatus.value = 'disconnected' })

  connection.on('messageReceived', (message: InboxMessageDto) => {
    store.onMessageReceived(message)
    showBrowserNotification(message)
  })

  connection.on('messageAcked', (ack: MessageAckDto) => {
    store.onMessageAcked(ack)
  })

  connection.on('messageRetried', (data: MessageRetriedDto) => {
    store.onMessageRetried(data)
  })

  connection.on('messageAcknowledged', (data: MessageAcknowledgedDto) => {
    store.onMessageAcknowledged(data)
  })

  connection.on('messageFailed', (data: MessageFailedDto) => {
    store.onMessageFailed(data)
    showFailedToast(data)
  })

  try {
    await connection.start()
    connectionStatus.value = 'connected'
  } catch {
    connectionStatus.value = 'disconnected'
  }
}

// ─── Browser notifications ───────────────────────────────────────────────────
async function requestNotificationPermission() {
  if ('Notification' in window && Notification.permission === 'default') {
    await Notification.requestPermission()
  }
}

function showBrowserNotification(message: InboxMessageDto) {
  if (!('Notification' in window)) return
  if (Notification.permission !== 'granted') return
  if (document.hasFocus()) return
  new Notification(`Message from ${message.fromCallsign}`, {
    body: message.body,
    tag: `msg-${message.id}`,
  })
}

// ─── Lifecycle ───────────────────────────────────────────────────────────────
onMounted(async () => {
  window.addEventListener('keydown', onKeydown)

  await requestNotificationPermission()

  try {
    const settings = await getSettings()
    ourCallsign.value = settings.ourCallsign
  } catch { /* ignore */ }

  try {
    const stations = await getStations(true)
    allStations.value = stations
  } catch { /* ignore */ }

  await Promise.all([store.fetchInbox(), store.fetchAll()])
  await connectSignalR()
})

onUnmounted(() => {
  window.removeEventListener('keydown', onKeydown)
  connection?.stop()
})

function replyTo(message: InboxMessageDto) {
  openCompose(message.fromCallsign)
}
</script>

<template>
  <v-container fluid class="pa-4 fill-height d-flex flex-column">
    <!-- Header row -->
    <v-row no-gutters align="center" class="mb-3">
      <v-col>
        <div class="d-flex align-center gap-2">
          <span class="text-h6">Messages</span>
          <v-chip
            v-if="store.unreadCount > 0"
            color="error"
            size="small"
            class="ml-2"
          >
            {{ store.unreadCount }} unread
          </v-chip>
          <v-chip
            v-if="connectionStatus !== 'connected'"
            :color="connectionStatus === 'connecting' ? 'warning' : 'error'"
            size="x-small"
            class="ml-2"
          >
            {{ connectionStatus }}
          </v-chip>
        </div>
      </v-col>
      <v-col cols="auto">
        <v-btn
          color="primary"
          prepend-icon="mdi-pencil"
          size="small"
          @click="openCompose()"
        >
          Compose
          <v-tooltip activator="parent" location="bottom">Press M</v-tooltip>
        </v-btn>
      </v-col>
    </v-row>

    <!-- Tabs -->
    <v-tabs v-model="activeTab" density="compact" class="mb-2">
      <v-tab value="inbox">
        Inbox
        <v-badge
          v-if="store.unreadCount > 0"
          :content="store.unreadCount"
          color="error"
          inline
          class="ml-2"
        />
      </v-tab>
      <v-tab value="outbox">Outbox</v-tab>
      <v-tab value="all">All Messages</v-tab>
    </v-tabs>

    <v-window v-model="activeTab" class="flex-grow-1 overflow-auto">
      <!-- ── Inbox Tab ───────────────────────────────────────────────────────── -->
      <v-window-item value="inbox">
        <v-table density="compact" hover>
          <thead>
            <tr>
              <th>From</th>
              <th>Message</th>
              <th>Time</th>
              <th>Status</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="msg in inboundMessages"
              :key="msg.id"
              :class="{ 'font-weight-bold': !msg.isRead }"
              style="cursor: pointer"
              @click="onRowClick(msg)"
            >
              <td>
                <a
                  href="#"
                  class="text-decoration-none"
                  @click.stop.prevent="$router.push('/')"
                >{{ msg.fromCallsign }}</a>
              </td>
              <td style="max-width: 400px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis">
                {{ msg.body }}
              </td>
              <td class="text-no-wrap">
                <span :title="formatUtc(msg.receivedAt)">{{ timeAgo(msg.receivedAt) }}</span>
              </td>
              <td>
                <v-chip
                  v-if="!msg.isRead"
                  color="primary"
                  size="x-small"
                  class="mr-1"
                >
                  Unread
                </v-chip>
              </td>
              <td>
                <v-btn
                  icon="mdi-reply"
                  size="x-small"
                  variant="text"
                  @click.stop="replyTo(msg)"
                />
              </td>
            </tr>
            <tr v-if="inboundMessages.length === 0">
              <td colspan="5" class="text-center text-medium-emphasis py-6">
                No messages yet.
              </td>
            </tr>
          </tbody>
        </v-table>
      </v-window-item>

      <!-- ── Outbox Tab ─────────────────────────────────────────────────────── -->
      <v-window-item value="outbox">
        <v-table density="compact">
          <thead>
            <tr>
              <th>To</th>
              <th>Message</th>
              <th>Status</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="msg in outboxMessages" :key="msg.id">
              <td class="text-no-wrap">{{ msg.toCallsign }}</td>
              <td style="max-width: 300px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis">
                {{ msg.body }}
              </td>
              <td>
                <v-chip
                  :color="retryBadge(msg).color"
                  size="x-small"
                  class="mr-1"
                >
                  {{ retryBadge(msg).text }}
                </v-chip>
                <span
                  v-if="msg.retryState === RetryState.Retrying && msg.nextRetryAt"
                  class="text-caption text-medium-emphasis"
                >
                  · Next retry in {{ secondsUntilRetry(msg) }}s
                </span>
                <span
                  v-if="msg.lastSentAt"
                  class="text-caption text-medium-emphasis ml-1"
                >
                  · Sent {{ formatUtc(msg.lastSentAt) }}
                </span>
              </td>
              <td class="text-no-wrap">
                <v-btn
                  v-if="msg.retryState !== RetryState.Acknowledged"
                  size="x-small"
                  variant="tonal"
                  color="primary"
                  class="mr-1"
                  :loading="actionLoading[msg.id] === 'retry'"
                  :disabled="!!actionLoading[msg.id]"
                  @click="doRetryNow(msg)"
                >
                  {{ actionLoading[msg.id] === 'retry' ? 'Sending…' : 'Retry Now' }}
                </v-btn>
                <v-btn
                  v-if="msg.retryState !== RetryState.Acknowledged"
                  size="x-small"
                  variant="tonal"
                  class="mr-1"
                  :loading="actionLoading[msg.id] === 'reset'"
                  :disabled="!!actionLoading[msg.id]"
                  @click="openResetDialog(msg)"
                >
                  Reset
                </v-btn>
                <v-btn
                  v-if="msg.retryState === RetryState.Retrying"
                  size="x-small"
                  variant="tonal"
                  color="error"
                  :loading="actionLoading[msg.id] === 'cancel'"
                  :disabled="!!actionLoading[msg.id]"
                  @click="doCancel(msg)"
                >
                  Cancel
                </v-btn>
              </td>
            </tr>
            <tr v-if="outboxMessages.length === 0">
              <td colspan="4" class="text-center text-medium-emphasis py-6">
                No outbound messages.
              </td>
            </tr>
          </tbody>
        </v-table>
      </v-window-item>

      <!-- ── All Messages Tab ───────────────────────────────────────────────── -->
      <v-window-item value="all">
        <!-- Filters -->
        <v-row dense class="mb-2 mt-1">
          <v-col cols="4">
            <v-text-field
              v-model="filterSender"
              label="Filter by sender"
              density="compact"
              variant="outlined"
              clearable
              hide-details
            />
          </v-col>
          <v-col cols="4">
            <v-text-field
              v-model="filterAddressee"
              label="Filter by addressee"
              density="compact"
              variant="outlined"
              clearable
              hide-details
            />
          </v-col>
          <v-col cols="4">
            <v-text-field
              v-model="filterText"
              label="Filter by text"
              density="compact"
              variant="outlined"
              clearable
              hide-details
            />
          </v-col>
        </v-row>

        <v-table density="compact" hover>
          <thead>
            <tr>
              <th>From</th>
              <th>To</th>
              <th>Message</th>
              <th>Time</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="msg in filteredAllMessages"
              :key="msg.packetId"
              :class="{
                'bg-blue-lighten-5': msg.toCallsign.toUpperCase() === ourCallsign.toUpperCase() && ourCallsign,
              }"
            >
              <td>{{ msg.fromCallsign }}</td>
              <td>{{ msg.toCallsign || '—' }}</td>
              <td style="max-width: 400px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis">
                {{ msg.body }}
              </td>
              <td class="text-no-wrap">
                <span :title="formatUtc(msg.receivedAt)">{{ timeAgo(msg.receivedAt) }}</span>
              </td>
            </tr>
            <tr v-if="filteredAllMessages.length === 0">
              <td colspan="4" class="text-center text-medium-emphasis py-6">
                No messages match the filter.
              </td>
            </tr>
          </tbody>
        </v-table>
      </v-window-item>
    </v-window>

    <!-- ── Compose Dialog ─────────────────────────────────────────────────── -->
    <v-dialog v-model="composeOpen" max-width="520" @keydown.esc="composeOpen = false">
      <v-card>
        <v-card-title class="d-flex align-center">
          <v-icon class="mr-2">mdi-message-text-outline</v-icon>
          Compose Message
        </v-card-title>

        <v-card-text>
          <v-combobox
            v-model="composeTo"
            :items="addresseeSuggestions"
            item-title="callsign"
            item-value="callsign"
            :return-object="false"
            no-filter
            label="To callsign"
            density="compact"
            variant="outlined"
            clearable
            class="mb-1"
            hide-details="auto"
            placeholder="Callsign or gateway (e.g. SMSGTE)"
            :rules="[
              (v: string) => !!v?.trim() || 'Required',
              (v: string) => !v || v.trim().length <= 9 || 'Max 9 characters',
              (v: string) => !v || /^[A-Za-z0-9-]+$/.test(v.trim()) || 'Letters, digits, and - only',
            ]"
          >
            <template #item="{ item, props: itemProps }">
              <v-list-item
                v-bind="itemProps"
                :subtitle="`${stationTypeName(item.stationType)} · ${timeAgo(item.lastSeen)}`"
              />
            </template>
          </v-combobox>

          <div class="mb-3 d-flex align-center flex-wrap gap-1">
            <span class="text-caption text-medium-emphasis mr-1">Common gateways:</span>
            <v-btn
              v-for="gw in COMMON_GATEWAYS"
              :key="gw"
              size="x-small"
              variant="tonal"
              @click="composeTo = gw"
            >
              {{ gw }}
            </v-btn>
          </div>

          <v-textarea
            v-model="composeBody"
            label="Message"
            density="compact"
            variant="outlined"
            rows="3"
            :maxlength="MAX_BODY"
            :counter="MAX_BODY"
            :hint="`${MAX_BODY - composeBody.length} characters remaining`"
            persistent-hint
            hide-details="auto"
            no-resize
          />

          <v-alert
            v-if="sendError"
            type="error"
            density="compact"
            class="mt-3"
          >
            {{ sendError }}
          </v-alert>
        </v-card-text>

        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="composeOpen = false">Cancel</v-btn>
          <v-btn
            color="primary"
            :loading="sending"
            :disabled="!composeTo?.trim() || !composeBody.trim()"
            @click="doSend"
          >
            Send
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- ── Reset Confirmation Dialog ─────────────────────────────────────── -->
    <v-dialog v-model="resetDialogOpen" max-width="420">
      <v-card v-if="resetDialogMsg">
        <v-card-title>Reset retries?</v-card-title>
        <v-card-text>
          Reset retries for this message? This will retransmit from attempt 1.
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="resetDialogOpen = false">Cancel</v-btn>
          <v-btn color="primary" @click="confirmReset">Reset</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- ── Failed Message Toast ───────────────────────────────────────────── -->
    <v-snackbar v-model="failedToast" color="error" :timeout="6000" location="bottom right">
      {{ failedToastText }}
      <template #actions>
        <v-btn variant="text" @click="failedToast = false">Dismiss</v-btn>
      </template>
    </v-snackbar>
  </v-container>
</template>
