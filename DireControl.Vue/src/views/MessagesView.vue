<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useMessagesStore } from '@/stores/messagesStore'
import { usePacketHubStore } from '@/stores/packetHub'
import { getAllMessages } from '@/api/messagesApi'
import {
  createPmsMessage,
  deletePmsMessage,
  getPmsMessages,
  killPmsMessage,
  pmsMessageTypeLabels,
  PmsMessageTypes,
  type PmsMessageDto,
  type PmsMessageType,
} from '@/api/pmsApi'
import { apiErrorDetail } from '@/api/axios'
import { getSettings, getStations } from '@/api/stationsApi'
import { formatUtc, timeAgo } from '@/utils/time'
import type { AllMessagePacketDto, InboxMessageDto, MessageFailedDto } from '@/types/message'
import { RetryState } from '@/types/message'
import { type StationDto, StationType } from '@/types/station'
import { useUiStore } from '@/stores/uiStore'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { useRouter } from 'vue-router'
import { useTick } from '@/composables/useTick'

const store = useMessagesStore()
const uiStore = useUiStore()
const stationSelection = useStationSelectionStore()
const router = useRouter()
const { now } = useTick(1000)

// Callsigns link to the station page (mock convention). The station page's
// "Show on map" covers the old select-on-map behavior.
function goToStation(callsign: string) {
  stationSelection.selectStation(callsign)
  router.push(`/stations/${encodeURIComponent(callsign)}`)
}

// ─── Settings & stations ────────────────────────────────────────────────────
const ourCallsign = ref('')
const allStations = ref<StationDto[]>([])

// ─── Common gateways ─────────────────────────────────────────────────────────
const COMMON_GATEWAYS = ['SMSGTE', 'EMAIL', 'WLNK-1', 'ANSRVR']

function stationTypeName(t: StationType): string {
  return StationType[t] ?? 'Unknown'
}

// ─── Tabs ────────────────────────────────────────────────────────────────────
const activeTab = ref<'inbox' | 'all' | 'outbox' | 'pms'>('inbox')

// ─── All-messages state ──────────────────────────────────────────────────────
const filterSender = ref('')
const filterAddressee = ref('')
const filterText = ref('')
const allItems = ref<AllMessagePacketDto[]>([])
const allPage = ref(1)
const allPageSize = ref(50)
const allTotalCount = ref(0)
const allLoading = ref(false)

const allTotalPages = computed(() =>
  Math.max(1, Math.ceil(allTotalCount.value / allPageSize.value)),
)

async function fetchAllMessages() {
  allLoading.value = true
  try {
    const result = await getAllMessages({
      page: allPage.value,
      pageSize: allPageSize.value,
      sender: filterSender.value || undefined,
      addressee: filterAddressee.value || undefined,
      text: filterText.value || undefined,
    })
    allItems.value = result.items
    allTotalCount.value = result.totalCount
  } finally {
    allLoading.value = false
  }
}

let filterDebounce: ReturnType<typeof setTimeout> | null = null
watch([filterSender, filterAddressee, filterText], () => {
  if (filterDebounce) clearTimeout(filterDebounce)
  filterDebounce = setTimeout(() => {
    allPage.value = 1
    void fetchAllMessages()
  }, 400)
})

// ─── PMS mailbox ─────────────────────────────────────────────────────────────
const pmsItems = ref<PmsMessageDto[]>([])
const pmsPage = ref(1)
const pmsPageSize = ref(50)
const pmsTotalCount = ref(0)
const pmsLoading = ref(false)
const pmsIncludeKilled = ref(false)
const pmsError = ref('')
let pmsLoadedOnce = false

const pmsTotalPages = computed(() =>
  Math.max(1, Math.ceil(pmsTotalCount.value / pmsPageSize.value)),
)

async function fetchPms() {
  pmsLoading.value = true
  pmsError.value = ''
  try {
    const result = await getPmsMessages({
      page: pmsPage.value,
      pageSize: pmsPageSize.value,
      includeKilled: pmsIncludeKilled.value,
    })
    pmsItems.value = result.messages
    pmsTotalCount.value = result.totalCount
    pmsLoadedOnce = true
  } catch (e: unknown) {
    pmsError.value = apiErrorDetail(e)
  } finally {
    pmsLoading.value = false
  }
}

// The PMS list loads lazily on first visit to its tab.
watch(activeTab, (tab) => {
  if (tab === 'pms' && !pmsLoadedOnce) void fetchPms()
})

watch(pmsIncludeKilled, () => {
  pmsPage.value = 1
  void fetchPms()
})

function pmsTypeChip(type: PmsMessageType): { label: string; color: string } {
  switch (type) {
    case PmsMessageTypes.Private:
      return { label: 'P', color: 'primary' }
    case PmsMessageTypes.Bulletin:
      return { label: 'B', color: 'orange' }
    default:
      return { label: '?', color: 'grey' }
  }
}

// PMS compose
const pmsComposeOpen = ref(false)
const pmsType = ref<PmsMessageType>(PmsMessageTypes.Private)
const pmsTo = ref('')
const pmsSubject = ref('')
const pmsBody = ref('')
const pmsSending = ref(false)
const pmsSendError = ref('')

const pmsTypeItems = [
  { title: pmsMessageTypeLabels[PmsMessageTypes.Private], value: PmsMessageTypes.Private },
  { title: pmsMessageTypeLabels[PmsMessageTypes.Bulletin], value: PmsMessageTypes.Bulletin },
]

function openPmsCompose() {
  pmsType.value = PmsMessageTypes.Private
  pmsTo.value = ''
  pmsSubject.value = ''
  pmsBody.value = ''
  pmsSendError.value = ''
  pmsComposeOpen.value = true
}

async function doPmsSend() {
  const to = pmsTo.value.trim().toUpperCase()
  if (!to) return
  pmsSending.value = true
  pmsSendError.value = ''
  try {
    await createPmsMessage({
      type: pmsType.value,
      toCallsign: to,
      subject: pmsSubject.value.trim() || undefined,
      body: pmsBody.value.trim() || undefined,
    })
    pmsComposeOpen.value = false
    await fetchPms()
  } catch (e: unknown) {
    pmsSendError.value = apiErrorDetail(e)
  } finally {
    pmsSending.value = false
  }
}

// PMS kill / delete (both confirmed)
const pmsConfirm = ref<{ action: 'kill' | 'delete'; msg: PmsMessageDto } | null>(null)

async function doPmsConfirm() {
  const confirm = pmsConfirm.value
  if (!confirm) return
  pmsConfirm.value = null
  pmsError.value = ''
  try {
    if (confirm.action === 'kill') await killPmsMessage(confirm.msg.id)
    else await deletePmsMessage(confirm.msg.id)
    await fetchPms()
  } catch (e: unknown) {
    pmsError.value = apiErrorDetail(e)
  }
}

// ─── Inbox / Outbox ──────────────────────────────────────────────────────────
// Sortable inbox columns (mock: clickable "From ▲" headers).
type InboxSortKey = 'from' | 'receivedAt'
const inboxSortKey = ref<InboxSortKey>('receivedAt')
const inboxSortDesc = ref(true)

function toggleInboxSort(key: InboxSortKey) {
  if (inboxSortKey.value === key) {
    inboxSortDesc.value = !inboxSortDesc.value
  } else {
    inboxSortKey.value = key
    inboxSortDesc.value = key === 'receivedAt'
  }
}

function inboxSortIcon(key: InboxSortKey): string {
  if (inboxSortKey.value !== key) return 'mdi-unfold-more-horizontal'
  return inboxSortDesc.value ? 'mdi-arrow-down' : 'mdi-arrow-up'
}

const inboundMessages = computed(() => {
  const list = store.inboxMessages.filter(
    (m) => m.fromCallsign.toUpperCase() !== ourCallsign.value.toUpperCase(),
  )
  const dir = inboxSortDesc.value ? -1 : 1
  return [...list].sort((a, b) => {
    if (inboxSortKey.value === 'from') return dir * a.fromCallsign.localeCompare(b.fromCallsign)
    return dir * a.receivedAt.localeCompare(b.receivedAt)
  })
})

const outboxMessages = computed(() =>
  store.inboxMessages.filter(
    (m) => m.fromCallsign.toUpperCase() === ourCallsign.value.toUpperCase(),
  ),
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
  } catch {
    /* ignore */
  } finally {
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
  } catch {
    /* ignore */
  } finally {
    delete actionLoading.value[msg.id]
  }
}

async function doCancel(msg: InboxMessageDto) {
  actionLoading.value[msg.id] = 'cancel'
  try {
    await store.cancelRetry(msg.id)
  } catch {
    /* ignore */
  } finally {
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
const composePath = ref('')
const composeAdvancedOpen = ref(false)
const defaultOutboundPath = ref('')
const sending = ref(false)
const sendError = ref('')
const MAX_BODY = 67

const PATH_REGEX = /^[A-Za-z0-9-]+(,[A-Za-z0-9-]+)*$/

const composePathError = computed(() => {
  const p = composePath.value.trim()
  if (!p) return ''
  return PATH_REGEX.test(p) ? '' : 'Use comma-separated callsigns, e.g. WIDE1-1,WIDE2-1'
})

const addresseeSuggestions = computed(() => {
  const q = composeTo.value?.trim().toUpperCase()
  if (!q || q.length < 2) return []
  return allStations.value.filter((s) => s.callsign.toUpperCase().startsWith(q)).slice(0, 8)
})

function openCompose(prefillTo = '') {
  composeTo.value = prefillTo
  composeBody.value = ''
  composePath.value = defaultOutboundPath.value
  composeAdvancedOpen.value = false
  sendError.value = ''
  composeOpen.value = true
}

async function doSend() {
  const to = composeTo.value?.trim().toUpperCase() ?? ''
  if (!to || !composeBody.value.trim()) return
  if (to.length > 9 || !/^[A-Z0-9-]+$/.test(to)) return
  if (composePathError.value) return
  sending.value = true
  sendError.value = ''
  try {
    await store.send({
      toCallsign: to,
      body: composeBody.value.trim().slice(0, MAX_BODY),
      path: composePath.value.trim() || undefined,
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
watch(
  () => uiStore.pendingComposeOpen,
  (pending) => {
    if (pending) {
      uiStore.consumeCompose()
      openCompose()
    }
  },
)

// ─── Inbox actions ───────────────────────────────────────────────────────────
async function onRowClick(message: InboxMessageDto) {
  toggleExpand(message.id)
  if (!message.isRead) {
    await store.markRead(message.id)
  }
}

// ─── Expandable message bodies ───────────────────────────────────────────────
// APRS messages are ≤67 chars but "all messages" rows (telemetry defs, bulletins)
// can be long — a click un-clips the row instead of hiding the tail forever.
const expandedMessages = ref(new Set<string>())

function toggleExpand(id: number | string) {
  const key = String(id)
  const next = new Set(expandedMessages.value)
  if (next.has(key)) next.delete(key)
  else next.add(key)
  expandedMessages.value = next
}

function isExpanded(id: number | string): boolean {
  return expandedMessages.value.has(String(id))
}

// ─── SignalR (shared hub) ────────────────────────────────────────────────────
// Store mutations are registered app-level in messagesStore; this view only
// adds its UI reactions (browser notification, failed toast) while mounted.
const hub = usePacketHubStore()
const connectionStatus = computed(() => hub.state)

function onHubMessageReceived(message: InboxMessageDto) {
  showBrowserNotification(message)
}

function onHubMessageFailed(data: MessageFailedDto) {
  showFailedToast(data)
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
    defaultOutboundPath.value = settings.outboundPath
    composePath.value = settings.outboundPath
  } catch {
    /* ignore */
  }

  try {
    const stations = await getStations(true)
    allStations.value = stations
  } catch {
    /* ignore */
  }

  await Promise.all([store.fetchInbox(), fetchAllMessages()])
  hub.on('messageReceived', onHubMessageReceived)
  hub.on('messageFailed', onHubMessageFailed)
})

onUnmounted(() => {
  window.removeEventListener('keydown', onKeydown)
  hub.off('messageReceived', onHubMessageReceived)
  hub.off('messageFailed', onHubMessageFailed)
})

function replyTo(message: InboxMessageDto) {
  openCompose(message.fromCallsign)
}
</script>

<template>
  <div class="messages-view">
    <!-- Header (mock: 17px/650 title; Compose lives in the card's tab row) -->
    <div class="d-flex align-center ga-2 mb-3 flex-shrink-0">
      <span class="page-title">Messages</span>
      <v-chip
        v-if="connectionStatus !== 'connected'"
        :color="connectionStatus === 'connecting' ? 'warning' : 'error'"
        size="x-small"
        variant="tonal"
      >
        {{ connectionStatus }}
      </v-chip>
    </div>

    <!-- One card: tab row (with counts + Compose) over the table, mock-style -->
    <v-card variant="outlined" class="messages-card">
      <div class="msg-tabs" role="tablist">
        <button
          class="msg-tab"
          :class="{ 'msg-tab--active': activeTab === 'inbox' }"
          role="tab"
          :aria-selected="activeTab === 'inbox'"
          @click="activeTab = 'inbox'"
        >
          Inbox
          <v-chip size="x-small" variant="tonal" class="ml-1">
            {{ inboundMessages.length.toLocaleString() }}
          </v-chip>
          <v-chip v-if="store.unreadCount > 0" size="x-small" color="error" class="ml-1">
            {{ store.unreadCount }} unread
          </v-chip>
        </button>
        <button
          class="msg-tab"
          :class="{ 'msg-tab--active': activeTab === 'outbox' }"
          role="tab"
          :aria-selected="activeTab === 'outbox'"
          @click="activeTab = 'outbox'"
        >
          Outbox
          <v-chip size="x-small" variant="tonal" class="ml-1">
            {{ outboxMessages.length.toLocaleString() }}
          </v-chip>
        </button>
        <button
          class="msg-tab"
          :class="{ 'msg-tab--active': activeTab === 'all' }"
          role="tab"
          :aria-selected="activeTab === 'all'"
          @click="activeTab = 'all'"
        >
          All
          <v-chip size="x-small" variant="tonal" class="ml-1">
            {{ allTotalCount.toLocaleString() }}
          </v-chip>
        </button>
        <button
          class="msg-tab"
          :class="{ 'msg-tab--active': activeTab === 'pms' }"
          role="tab"
          :aria-selected="activeTab === 'pms'"
          @click="activeTab = 'pms'"
        >
          PMS
          <v-chip size="x-small" variant="tonal" class="ml-1">
            {{ pmsTotalCount.toLocaleString() }}
          </v-chip>
        </button>
        <v-spacer />
        <v-btn
          color="primary"
          prepend-icon="mdi-pencil"
          size="small"
          class="align-self-center mr-2"
          @click="openCompose()"
        >
          Compose
          <v-tooltip activator="parent" location="bottom">Press M</v-tooltip>
        </v-btn>
      </div>
      <v-divider />

      <v-window v-model="activeTab">
        <!-- ── Inbox Tab ───────────────────────────────────────────────────────── -->
        <v-window-item value="inbox">
          <v-table density="compact" hover>
            <thead>
              <tr>
                <th>
                  <button class="sort-th" @click="toggleInboxSort('from')">
                    From <v-icon size="12">{{ inboxSortIcon('from') }}</v-icon>
                  </button>
                </th>
                <th>Message</th>
                <th>
                  <button class="sort-th" @click="toggleInboxSort('receivedAt')">
                    Received <v-icon size="12">{{ inboxSortIcon('receivedAt') }}</v-icon>
                  </button>
                </th>
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="msg in inboundMessages"
                :key="msg.id"
                :class="{ 'msg-row-unread': !msg.isRead }"
                style="cursor: pointer"
                @click="onRowClick(msg)"
              >
                <td>
                  <a
                    href="#"
                    class="callsign-link text-decoration-none"
                    @click.stop.prevent="goToStation(msg.fromCallsign)"
                    >{{ msg.fromCallsign }}</a
                  >
                </td>
                <td
                  class="msg-body"
                  :class="{ 'msg-body--open': isExpanded(msg.id) }"
                  :title="isExpanded(msg.id) ? undefined : msg.body"
                >
                  {{ msg.body }}
                </td>
                <td class="text-no-wrap mono-time">
                  <span :title="formatUtc(msg.receivedAt)">{{ timeAgo(msg.receivedAt, now) }}</span>
                </td>
                <td>
                  <v-chip v-if="!msg.isRead" color="primary" size="x-small" variant="tonal">
                    unread
                  </v-chip>
                  <v-chip v-else size="x-small" variant="tonal" class="msg-chip-read">read</v-chip>
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
                <td colspan="5" class="text-center py-8">
                  <v-icon size="36" class="text-medium-emphasis mb-2">mdi-email-outline</v-icon>
                  <div class="text-body-2 font-weight-medium mb-1">No messages yet</div>
                  <div class="text-caption text-medium-emphasis mb-3">
                    Anything addressed to {{ ourCallsign || 'your station' }} lands here.
                  </div>
                  <v-btn
                    size="small"
                    color="primary"
                    variant="tonal"
                    prepend-icon="mdi-email-edit-outline"
                    @click="openCompose()"
                  >
                    Compose your first message
                  </v-btn>
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
                <td class="text-no-wrap">
                  <a
                    href="#"
                    class="callsign-link text-decoration-none"
                    @click.stop.prevent="goToStation(msg.toCallsign)"
                    >{{ msg.toCallsign }}</a
                  >
                </td>
                <td
                  class="msg-body msg-body--narrow"
                  :class="{ 'msg-body--open': isExpanded(`out-${msg.id}`) }"
                  :title="isExpanded(`out-${msg.id}`) ? undefined : msg.body"
                  style="cursor: pointer"
                  @click="toggleExpand(`out-${msg.id}`)"
                >
                  {{ msg.body }}
                </td>
                <td>
                  <!-- Stacked: chip on top, detail lines under it — no more one-line cram -->
                  <div class="d-flex flex-column align-start ga-1 py-1">
                    <v-chip :color="retryBadge(msg).color" size="x-small">
                      {{ retryBadge(msg).text }}
                    </v-chip>
                    <span
                      v-if="msg.retryState === RetryState.Retrying && msg.nextRetryAt"
                      class="text-caption text-medium-emphasis"
                    >
                      next retry in {{ secondsUntilRetry(msg) }}s
                    </span>
                    <span
                      v-if="msg.lastSentAt"
                      class="text-caption text-medium-emphasis"
                      :title="formatUtc(msg.lastSentAt)"
                    >
                      sent {{ timeAgo(msg.lastSentAt, now) }}
                    </span>
                  </div>
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
          <v-row dense class="px-3 pt-3">
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
              <tr v-if="allLoading">
                <td colspan="4" class="text-center text-medium-emphasis py-6">Loading…</td>
              </tr>
              <template v-else>
                <tr
                  v-for="msg in allItems"
                  :key="msg.packetId"
                  :class="{
                    'msg-row-own':
                      msg.toCallsign.toUpperCase() === ourCallsign.toUpperCase() && ourCallsign,
                  }"
                >
                  <td class="callsign-plain">{{ msg.fromCallsign }}</td>
                  <td class="callsign-plain">{{ msg.toCallsign || '—' }}</td>
                  <td
                    class="msg-body"
                    :class="{ 'msg-body--open': isExpanded(`all-${msg.packetId}`) }"
                    :title="isExpanded(`all-${msg.packetId}`) ? undefined : msg.body"
                    style="cursor: pointer"
                    @click="toggleExpand(`all-${msg.packetId}`)"
                  >
                    {{ msg.body }}
                  </td>
                  <td class="text-no-wrap mono-time">
                    <span :title="formatUtc(msg.receivedAt)">{{
                      timeAgo(msg.receivedAt, now)
                    }}</span>
                  </td>
                </tr>
                <tr v-if="allItems.length === 0">
                  <td colspan="4" class="text-center text-medium-emphasis py-6">
                    No messages match the filter.
                  </td>
                </tr>
              </template>
            </tbody>
          </v-table>

          <div class="d-flex align-center justify-space-between px-3 py-2">
            <span class="text-caption text-medium-emphasis"> {{ allTotalCount }} total </span>
            <v-pagination
              v-if="allTotalPages > 1"
              v-model="allPage"
              :length="allTotalPages"
              :total-visible="7"
              density="compact"
              @update:model-value="fetchAllMessages"
            />
          </div>
        </v-window-item>

        <!-- ── PMS Mailbox Tab ────────────────────────────────────────────────── -->
        <v-window-item value="pms">
          <div class="d-flex align-center flex-wrap ga-3 px-3 pt-3">
            <v-btn
              color="primary"
              size="small"
              prepend-icon="mdi-email-edit-outline"
              @click="openPmsCompose"
            >
              Compose PMS
            </v-btn>
            <v-switch
              v-model="pmsIncludeKilled"
              label="Include killed"
              color="primary"
              density="compact"
              hide-details
            />
            <v-spacer />
            <v-btn
              icon="mdi-refresh"
              size="small"
              variant="text"
              :loading="pmsLoading"
              @click="fetchPms"
            />
          </div>

          <v-alert v-if="pmsError" type="error" variant="tonal" density="compact" class="mx-3 mt-2">
            {{ pmsError }}
          </v-alert>

          <v-table density="compact" hover>
            <thead>
              <tr>
                <th style="width: 60px">#</th>
                <th style="width: 60px">Type</th>
                <th>From</th>
                <th>To</th>
                <th>Subject</th>
                <th>Date</th>
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              <tr v-if="pmsLoading && pmsItems.length === 0">
                <td colspan="8" class="text-center text-medium-emphasis py-6">Loading…</td>
              </tr>
              <template v-else>
                <tr v-for="msg in pmsItems" :key="msg.id">
                  <td class="mono-time">{{ msg.id }}</td>
                  <td>
                    <v-chip
                      :color="pmsTypeChip(msg.type).color"
                      size="x-small"
                      variant="tonal"
                      :title="pmsMessageTypeLabels[msg.type]"
                    >
                      {{ pmsTypeChip(msg.type).label }}
                    </v-chip>
                  </td>
                  <td class="callsign-plain">{{ msg.fromCallsign }}</td>
                  <td class="callsign-plain">{{ msg.toCallsign }}</td>
                  <td
                    class="msg-body"
                    :class="{ 'msg-body--open': isExpanded(`pms-${msg.id}`) }"
                    :title="isExpanded(`pms-${msg.id}`) ? undefined : (msg.body ?? undefined)"
                    style="cursor: pointer"
                    @click="toggleExpand(`pms-${msg.id}`)"
                  >
                    <span class="font-weight-medium">{{ msg.subject || '(no subject)' }}</span>
                    <template v-if="isExpanded(`pms-${msg.id}`) && msg.body">
                      <br />
                      <span class="text-medium-emphasis">{{ msg.body }}</span>
                    </template>
                  </td>
                  <td class="text-no-wrap mono-time">
                    <span :title="formatUtc(msg.createdAt)">{{ timeAgo(msg.createdAt, now) }}</span>
                  </td>
                  <td>
                    <div class="d-flex ga-1">
                      <v-chip v-if="msg.isKilled" color="error" size="x-small" variant="tonal">
                        killed
                      </v-chip>
                      <v-chip
                        v-else-if="msg.readAt"
                        size="x-small"
                        variant="tonal"
                        class="msg-chip-read"
                      >
                        read
                      </v-chip>
                      <v-chip v-else color="primary" size="x-small" variant="tonal">unread</v-chip>
                    </div>
                  </td>
                  <td class="text-no-wrap text-right">
                    <v-btn
                      v-if="!msg.isKilled"
                      size="x-small"
                      variant="tonal"
                      color="warning"
                      class="mr-1"
                      title="Kill (BBS-style soft delete)"
                      @click="pmsConfirm = { action: 'kill', msg }"
                    >
                      Kill
                    </v-btn>
                    <v-btn
                      icon="mdi-delete-outline"
                      size="x-small"
                      variant="text"
                      color="error"
                      title="Delete permanently"
                      @click="pmsConfirm = { action: 'delete', msg }"
                    />
                  </td>
                </tr>
                <tr v-if="pmsItems.length === 0">
                  <td colspan="8" class="text-center py-8">
                    <v-icon size="36" class="text-medium-emphasis mb-2">
                      mdi-mailbox-open-outline
                    </v-icon>
                    <div class="text-body-2 font-weight-medium mb-1">The mailbox is empty</div>
                    <div class="text-caption text-medium-emphasis">
                      Messages left by stations connecting to your PMS land here.
                    </div>
                  </td>
                </tr>
              </template>
            </tbody>
          </v-table>

          <div class="d-flex align-center justify-space-between px-3 py-2">
            <span class="text-caption text-medium-emphasis">{{ pmsTotalCount }} total</span>
            <v-pagination
              v-if="pmsTotalPages > 1"
              v-model="pmsPage"
              :length="pmsTotalPages"
              :total-visible="7"
              density="compact"
              @update:model-value="fetchPms"
            />
          </div>
        </v-window-item>
      </v-window>
    </v-card>

    <!-- ── PMS Compose Dialog ─────────────────────────────────────────────── -->
    <v-dialog v-model="pmsComposeOpen" max-width="520" @keydown.esc="pmsComposeOpen = false">
      <v-card>
        <v-card-title class="d-flex align-center">
          <v-icon class="mr-2">mdi-mailbox-open-outline</v-icon>
          Compose PMS Message
        </v-card-title>
        <v-card-text>
          <v-select
            v-model="pmsType"
            :items="pmsTypeItems"
            label="Type"
            class="mb-3"
            hide-details
          />
          <v-text-field
            v-model="pmsTo"
            label="To callsign"
            placeholder="e.g. W3UWU or ALL for bulletins"
            class="mb-3"
            hide-details="auto"
            :rules="[(v: string) => !!v?.trim() || 'Required']"
          />
          <v-text-field v-model="pmsSubject" label="Subject" class="mb-3" hide-details />
          <v-textarea
            v-model="pmsBody"
            label="Body"
            variant="outlined"
            density="compact"
            rows="4"
            hide-details
          />
          <v-alert v-if="pmsSendError" type="error" density="compact" variant="tonal" class="mt-3">
            {{ pmsSendError }}
          </v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="pmsComposeOpen = false">Cancel</v-btn>
          <v-btn color="primary" :loading="pmsSending" :disabled="!pmsTo.trim()" @click="doPmsSend">
            Save to mailbox
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- ── PMS Kill / Delete Confirmation ─────────────────────────────────── -->
    <v-dialog
      :model-value="pmsConfirm !== null"
      max-width="440"
      @update:model-value="pmsConfirm = null"
    >
      <v-card v-if="pmsConfirm">
        <v-card-title>
          {{ pmsConfirm.action === 'kill' ? 'Kill message?' : 'Delete message?' }}
        </v-card-title>
        <v-card-text>
          <template v-if="pmsConfirm.action === 'kill'">
            Kill message #{{ pmsConfirm.msg.id }} to <strong>{{ pmsConfirm.msg.toCallsign }}</strong
            >? Killed messages are hidden from connecting stations and purged after the retention
            period.
          </template>
          <template v-else>
            Permanently delete message #{{ pmsConfirm.msg.id }} to
            <strong>{{ pmsConfirm.msg.toCallsign }}</strong
            >? This cannot be undone.
          </template>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="pmsConfirm = null">Cancel</v-btn>
          <v-btn
            :color="pmsConfirm.action === 'kill' ? 'warning' : 'error'"
            variant="tonal"
            @click="doPmsConfirm"
          >
            {{ pmsConfirm.action === 'kill' ? 'Kill' : 'Delete' }}
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

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
              (v: string) =>
                !v || /^[A-Za-z0-9-]+$/.test(v.trim()) || 'Letters, digits, and - only',
            ]"
          >
            <template #item="{ item, props: itemProps }">
              <v-list-item
                v-bind="itemProps"
                :subtitle="`${stationTypeName(item.stationType)} · ${timeAgo(item.lastSeen, now)}`"
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

          <div class="mt-3">
            <v-btn
              variant="text"
              size="small"
              :prepend-icon="composeAdvancedOpen ? 'mdi-chevron-down' : 'mdi-chevron-right'"
              class="px-0 text-medium-emphasis"
              @click="composeAdvancedOpen = !composeAdvancedOpen"
            >
              Advanced
            </v-btn>
            <div v-if="composeAdvancedOpen" class="mt-2">
              <v-text-field
                v-model="composePath"
                label="Path"
                density="compact"
                variant="outlined"
                clearable
                :error-messages="composePathError"
                hide-details="auto"
                placeholder="e.g. WIDE1-1,WIDE2-1 or leave blank for direct"
                class="mb-1"
              />
              <div class="d-flex align-center flex-wrap gap-1 mb-1">
                <span class="text-caption text-medium-emphasis mr-1">Common paths:</span>
                <v-btn size="x-small" variant="tonal" @click="composePath = 'WIDE1-1,WIDE2-1'"
                  >WIDE1-1,WIDE2-1</v-btn
                >
                <v-btn size="x-small" variant="tonal" @click="composePath = 'WIDE2-1'"
                  >WIDE2-1</v-btn
                >
                <v-btn size="x-small" variant="tonal" @click="composePath = 'WIDE1-1'"
                  >WIDE1-1</v-btn
                >
                <v-btn size="x-small" variant="tonal" @click="composePath = ''"
                  >Direct (no path)</v-btn
                >
              </div>
            </div>
          </div>

          <v-alert v-if="sendError" type="error" density="compact" class="mt-3">
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
  </div>
</template>

<style scoped>
.page-title {
  font-size: 17px;
  font-weight: 650;
  line-height: 1.3;
}

.messages-card {
  display: flex;
  flex-direction: column;
  min-height: 0;
}

/* Mock tab row: underline text tabs with count chips, Compose at the right. */
.msg-tabs {
  display: flex;
  gap: 2px;
  padding: 0 10px;
  overflow-x: auto;
}

.msg-tab {
  display: inline-flex;
  align-items: center;
  padding: 10px 12px;
  font-size: 0.85rem;
  font-weight: 600;
  color: rgba(var(--v-theme-on-surface), 0.6);
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  cursor: pointer;
  white-space: nowrap;
}

.msg-tab:hover {
  color: rgba(var(--v-theme-on-surface), 0.9);
}

.msg-tab--active {
  color: rgb(var(--v-theme-primary));
  border-bottom-color: rgb(var(--v-theme-primary));
}

/* Theme-safe "addressed to us" highlight (works in light and dark). */
.msg-row-own td {
  background: rgba(var(--v-theme-primary), 0.08);
}

/* Unread rows: bold + theme-token tint (mock's highlight, dark-mode safe). */
.msg-row-unread td {
  background: rgba(var(--v-theme-primary), 0.08);
  font-weight: 600;
}

.msg-chip-read {
  color: rgba(var(--v-theme-on-surface), 0.6);
}

.mono-time {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.78rem;
  font-variant-numeric: tabular-nums;
}

.callsign-plain {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-weight: 600;
}

.sort-th {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  background: none;
  border: none;
  padding: 0;
  cursor: pointer;
  font: inherit;
  color: inherit;
}

.sort-th:hover {
  color: rgba(var(--v-theme-primary), 1);
}

/* Clipped by default; click expands to the full text. */
.msg-body {
  max-width: 400px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.msg-body--narrow {
  max-width: 300px;
}

.msg-body--open {
  white-space: normal;
  overflow: visible;
  text-overflow: clip;
  word-break: break-word;
}

.messages-view {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  justify-content: flex-start;
  height: 100%;
  padding: 16px;
  overflow-y: auto;
}
</style>
