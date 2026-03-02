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
import type { MessageAckDto, InboxMessageDto } from '@/types/message'
import { type StationDto, StationType } from '@/types/station'
import { useUiStore } from '@/stores/uiStore'

const store = useMessagesStore()
const uiStore = useUiStore()

// ─── Settings & stations ────────────────────────────────────────────────────
const ourCallsign = ref('')
const allStations = ref<StationDto[]>([])

// ─── Common gateways ─────────────────────────────────────────────────────────
const COMMON_GATEWAYS = ['SMSGTE', 'EMAIL', 'WLNK-1', 'ANSRVR']

function stationTypeName(t: StationType): string {
  return StationType[t] ?? 'Unknown'
}

// ─── Tabs ────────────────────────────────────────────────────────────────────
const activeTab = ref<'inbox' | 'all'>('inbox')

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
    activeTab.value = 'inbox'
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

function replyTo(message: InboxMessageDto) {
  openCompose(message.fromCallsign === ourCallsign.value ? message.toCallsign : message.fromCallsign)
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

// ─── Helpers ─────────────────────────────────────────────────────────────────
function isOutbound(message: InboxMessageDto) {
  return message.fromCallsign.toUpperCase() === ourCallsign.value.toUpperCase()
}

function ackBadge(message: InboxMessageDto): { text: string; color: string } | null {
  if (!isOutbound(message)) return null
  return message.ackSent
    ? { text: 'Acknowledged', color: 'success' }
    : { text: 'Pending ACK', color: 'warning' }
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
      <v-tab value="all">All Messages</v-tab>
    </v-tabs>

    <v-window v-model="activeTab" class="flex-grow-1 overflow-auto">
      <!-- ── Inbox Tab ───────────────────────────────────────────────────────── -->
      <v-window-item value="inbox">
        <v-table density="compact" hover>
          <thead>
            <tr>
              <th>From / To</th>
              <th>Message</th>
              <th>Time</th>
              <th>Status</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="msg in store.inboxMessages"
              :key="msg.id"
              :class="{ 'font-weight-bold': !msg.isRead && !isOutbound(msg), 'text-blue-grey': isOutbound(msg) }"
              style="cursor: pointer"
              @click="onRowClick(msg)"
            >
              <td>
                <span v-if="isOutbound(msg)">
                  → <a
                    href="#"
                    class="text-decoration-none"
                    @click.stop.prevent="$router.push('/')"
                  >{{ msg.toCallsign }}</a>
                </span>
                <span v-else>
                  <a
                    href="#"
                    class="text-decoration-none"
                    @click.stop.prevent="$router.push('/')"
                  >{{ msg.fromCallsign }}</a>
                </span>
              </td>
              <td style="max-width: 400px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis">
                {{ msg.body }}
              </td>
              <td class="text-no-wrap">
                <span :title="formatUtc(msg.receivedAt)">{{ timeAgo(msg.receivedAt) }}</span>
              </td>
              <td>
                <v-chip
                  v-if="!msg.isRead && !isOutbound(msg)"
                  color="primary"
                  size="x-small"
                  class="mr-1"
                >
                  Unread
                </v-chip>
                <v-chip
                  v-if="ackBadge(msg)"
                  :color="ackBadge(msg)!.color"
                  size="x-small"
                >
                  {{ ackBadge(msg)!.text }}
                </v-chip>
              </td>
              <td>
                <v-btn
                  v-if="!isOutbound(msg)"
                  icon="mdi-reply"
                  size="x-small"
                  variant="text"
                  @click.stop="replyTo(msg)"
                />
              </td>
            </tr>
            <tr v-if="store.inboxMessages.length === 0">
              <td colspan="5" class="text-center text-medium-emphasis py-6">
                No messages yet.
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
                :subtitle="`${stationTypeName(item.raw.stationType)} · ${timeAgo(item.raw.lastSeen)}`"
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
  </v-container>
</template>
