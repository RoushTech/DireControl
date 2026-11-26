import { defineStore } from 'pinia'
import { computed, reactive } from 'vue'
import {
  getRecentTx,
  isFinalTxState,
  TxFrameKinds,
  TxFrameStates,
  type TxFrameDto,
  type TxFrameKind,
} from '@/api/txApi'
import { beaconNow as apiBeaconNow } from '@/api/radiosApi'
import { apiErrorDetail } from '@/api/axios'
import { usePacketHubStore } from '@/stores/packetHub'
import { useToastStore } from '@/stores/toastStore'
import { useUiStore } from '@/stores/uiStore'
import type { BeaconConfirmedHeardDto, DigiConfirmationBroadcastDto } from '@/types/radio'

/**
 * What happened after a frame was sent, for kinds that have a confirmation:
 * a beacon is heard back via a digipeater, or stays unverified; a message
 * part's ACK lives on the message itself.
 */
export type TxOutcome = 'heard' | 'unverified'

export interface TxItem extends TxFrameDto {
  outcome?: TxOutcome
  heardVia?: string
  heardAt?: string
}

/** How long a sent beacon may go without a digipeat echo before it is "unverified". */
export const BEACON_HEARD_WINDOW_MS = 30_000

const STATE_RANK: Record<number, number> = {
  [TxFrameStates.Unknown]: 0,
  [TxFrameStates.Queued]: 1,
  [TxFrameStates.WaitingForChannel]: 2,
  [TxFrameStates.OnAir]: 3,
  [TxFrameStates.Sent]: 4,
  [TxFrameStates.Failed]: 4,
  [TxFrameStates.Dropped]: 4,
}

/** Kinds whose failures are worth a toast; message parts retry on their own. */
const TOAST_FAILURES = new Set<TxFrameKind>([
  TxFrameKinds.Beacon,
  TxFrameKinds.TestTone,
  TxFrameKinds.IsToRf,
  TxFrameKinds.Unknown,
])

/**
 * Every frame this station transmits, keyed by frame id, from queued to on
 * air to sent — or failed with the reason. Fed by the
 * txFrameStateChanged hub event; resyncs from GET api/v0/tx/recent on start
 * and after every reconnect.
 */
export const useTxStore = defineStore('tx', () => {
  const hub = usePacketHubStore()
  const toast = useToastStore()
  const ui = useUiStore()
  const frames = reactive<Record<number, TxItem>>({})
  /** Frames this browser asked for: success toasts only for these. */
  const requested = new Set<number>()
  const unverifiedTimers = new Map<number, ReturnType<typeof setTimeout>>()

  const list = computed(() => Object.values(frames).sort((a, b) => b.id - a.id))

  /** Not yet final, or a sent beacon still listening for its echo. */
  const active = computed(() =>
    list.value.filter(
      (f) =>
        !isFinalTxState(f.state) ||
        (f.kind === TxFrameKinds.Beacon && f.state === TxFrameStates.Sent && !f.outcome),
    ),
  )
  const pending = computed(() => list.value.filter((f) => !isFinalTxState(f.state)))
  const onAir = computed(() => pending.value.some((f) => f.state === TxFrameStates.OnAir))
  const waiting = computed(() =>
    pending.value.some((f) => f.state === TxFrameStates.WaitingForChannel),
  )

  function scheduleUnverified(f: TxItem) {
    if (f.kind !== TxFrameKinds.Beacon || f.state !== TxFrameStates.Sent || f.outcome) return
    if (unverifiedTimers.has(f.id)) return
    const sentAt = f.sentAt ? new Date(f.sentAt).getTime() : Date.now()
    const delay = Math.max(0, sentAt + BEACON_HEARD_WINDOW_MS - Date.now())
    unverifiedTimers.set(
      f.id,
      setTimeout(() => {
        unverifiedTimers.delete(f.id)
        const cur = frames[f.id]
        if (cur && !cur.outcome) cur.outcome = 'unverified'
      }, delay),
    )
  }

  /** Applies a state; never moves a frame backwards (events can race the resync). */
  function upsert(dto: TxFrameDto, fromResync = false) {
    const cur = frames[dto.id]
    if (cur && STATE_RANK[dto.state]! < STATE_RANK[cur.state]!) return
    if (cur && cur.state === dto.state && cur.at >= dto.at) return
    frames[dto.id] = { ...cur, ...dto }
    const f = frames[dto.id]!
    scheduleUnverified(f)
    if (fromResync || cur?.state === dto.state) return

    if (
      (dto.state === TxFrameStates.Failed || dto.state === TxFrameStates.Dropped) &&
      TOAST_FAILURES.has(dto.kind)
    )
      toast.toast(
        `${dto.title} ${dto.state === TxFrameStates.Dropped ? 'dropped' : 'failed'}: ${dto.reason ?? 'not sent'}`,
        'error',
        12_000,
        { label: 'TX queue', run: () => (ui.txSheetOpen = true) },
      )
  }

  async function fetchRecent() {
    const recent = await getRecentTx(100)
    for (const dto of recent.reverse()) upsert(dto, true)
  }

  /** The newest frame of a kind carrying a reference (message number). */
  function latestForRef(kind: TxFrameKind, ref: string): TxItem | undefined {
    return list.value.find((f) => f.kind === kind && f.ref === ref)
  }

  /** The newest beacon frame for a radio. */
  function latestBeacon(radioId: string): TxItem | undefined {
    return list.value.find((f) => f.kind === TxFrameKinds.Beacon && f.radioId === radioId)
  }

  function markBeaconHeard(radioId: string, via?: string, at?: string) {
    const b = list.value.find(
      (f) =>
        f.kind === TxFrameKinds.Beacon &&
        f.radioId === radioId &&
        f.state === TxFrameStates.Sent &&
        f.outcome !== 'heard',
    )
    if (!b) return
    b.outcome = 'heard'
    b.heardVia = via ?? b.heardVia
    b.heardAt = at ?? new Date().toISOString()
    if (requested.has(b.id))
      toast.toast(
        via ? `Beacon digipeated by ${via}.` : 'Beacon repeated by a digipeater.',
        'success',
      )
    requested.delete(b.id)
  }

  /**
   * Queues a beacon and follows it: "Beacon queued" now, "Beacon heard" when
   * a digipeater repeats it, or the failure reason if it never goes to air.
   */
  async function beaconNow(radioId: string, label: string): Promise<boolean> {
    try {
      const res = await apiBeaconNow(radioId)
      for (const id of res.txFrameIds) requested.add(id)
      toast.toast(`Beacon queued for ${label}. It is sent once it is on air.`, 'info', 6000, {
        label: 'TX queue',
        run: () => (ui.txSheetOpen = true),
      })
      return true
    } catch (e) {
      toast.toast(apiErrorDetail(e, `Beacon not queued for ${label}`), 'error', 10_000)
      return false
    }
  }

  function track(ids: number[]) {
    for (const id of ids) requested.add(id)
  }

  /** Resolves with the frame once it reaches a final state (or after `timeoutMs`). */
  function waitFor(id: number, timeoutMs = 60_000): Promise<TxItem | undefined> {
    return new Promise((resolve) => {
      const started = Date.now()
      const timer = setInterval(() => {
        const f = frames[id]
        if ((f && isFinalTxState(f.state)) || Date.now() - started > timeoutMs) {
          clearInterval(timer)
          resolve(f)
        }
      }, 250)
    })
  }

  hub.on('txFrameStateChanged', (dto: TxFrameDto) => upsert(dto))
  hub.on('beaconConfirmedHeard', (dto: BeaconConfirmedHeardDto) => markBeaconHeard(dto.radioId))
  hub.on('digiConfirmation', (dto: DigiConfirmationBroadcastDto) =>
    markBeaconHeard(dto.radioId, dto.digipeater, dto.confirmedAt),
  )
  hub.onReconnected(() => fetchRecent(), 'transmit queue')
  void fetchRecent().catch(() => {})

  return {
    frames,
    list,
    active,
    pending,
    onAir,
    waiting,
    upsert,
    fetchRecent,
    latestForRef,
    latestBeacon,
    beaconNow,
    track,
    waitFor,
  }
})
