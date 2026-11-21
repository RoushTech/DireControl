import { defineStore } from 'pinia'
import { ref, computed, reactive, watch } from 'vue'
import { getRadios, getLastBeacon } from '@/api/radiosApi'
import { usePacketHubStore } from '@/stores/packetHub'
import { ModemStates, type ModemLevelDto, type ModemStatusDto } from '@/api/modemApi'
import type {
  RadioDto,
  LastBeaconDto,
  OwnBeaconBroadcastDto,
  DigiConfirmationBroadcastDto,
  BeaconConfirmedHeardDto,
} from '@/types/radio'

/** Live RX/TX activity for a radio's modem, updated from the modemLevel stream. */
export interface RadioActivity {
  carrierDetected: boolean
  transmitting: boolean
  /** Date.now() when this radio's last modemLevel frame arrived. */
  updatedAt: number
}

/**
 * modemLevel arrives at 10 Hz while a modem runs; this long without one means
 * the stream (or the modem) has stopped and the last reading is no longer live.
 */
export const LEVEL_STALE_MS = 2000

/**
 * Carrier held this long with no new decode is not a transmission — it is
 * noise or an open squelch, and it is holding transmissions back (CSMA).
 */
export const CARRIER_HELD_WARN_MS = 10_000

export const useRadiosStore = defineStore('radios', () => {
  const radios = ref<RadioDto[]>([])
  // reactive plain-object keyed by radioId — more reliable than ref<Map> because
  // Vue 3 property-assignment triggers are always tracked for plain reactive objects.
  const lastBeacons = reactive<Record<string, LastBeaconDto | undefined>>({})
  const currentBeaconIds = ref<Map<string, number>>(new Map())
  // Live modem RX/TX activity keyed by radioId, fed by the modemLevel stream.
  const activity = reactive<Record<string, RadioActivity | undefined>>({})
  // Latest full modem status per radio (pushed ~1 Hz while any modem runs).
  const modemStatus = reactive<Record<string, ModemStatusDto | undefined>>({})
  // When carrier was first seen in the current run, and the decode count then.
  const carrierRuns = reactive<Record<string, { since: number; decoded: number } | undefined>>({})
  const loading = ref(false)
  /** True once radios have been fetched at least once (distinguishes "none" from "not yet"). */
  const loaded = ref(false)
  // Ticks so staleness re-evaluates even when no new frames arrive.
  const activityClock = ref(Date.now())
  setInterval(() => {
    activityClock.value = Date.now()
  }, 1000)

  const activeRadios = computed(() => radios.value.filter((r) => r.isActive))

  async function fetchRadios() {
    loading.value = true
    try {
      radios.value = await getRadios()
      loaded.value = true
    } finally {
      loading.value = false
    }
  }

  async function fetchLastBeacon(radioId: string) {
    try {
      const dto = await getLastBeacon(radioId)
      lastBeacons[radioId] = dto
    } catch {
      /* ignore */
    }
  }

  async function fetchAllLastBeacons() {
    for (const radio of activeRadios.value) {
      await fetchLastBeacon(radio.id)
    }
  }

  function onOwnBeaconReceived(dto: OwnBeaconBroadcastDto) {
    // Track which beacon is current so DigiConfirmation events can be correlated
    currentBeaconIds.value.set(dto.radioId, dto.beaconId)

    // Update the radio's lastBeaconedAt in the radios list
    const radio = radios.value.find((r) => r.id === dto.radioId)
    if (radio) {
      radio.lastBeaconedAt = dto.beaconedAt
      radio.secondsSinceBeacon = 0
    }

    const existing = lastBeacons[dto.radioId]
    lastBeacons[dto.radioId] = {
      radioId: dto.radioId,
      radioName: existing?.radioName ?? radio?.name ?? dto.fullCallsign,
      fullCallsign: dto.fullCallsign,
      beaconedAt: dto.beaconedAt,
      secondsSinceBeacon: 0,
      latitude: dto.lat,
      longitude: dto.lon,
      pathUsed: dto.pathUsed,
      comment: existing?.comment ?? null,
      heard: dto.heard,
      confirmations: [],
    }
  }

  function onBeaconConfirmedHeard(dto: BeaconConfirmedHeardDto) {
    const existing = lastBeacons[dto.radioId]
    if (existing) {
      existing.heard = true
    }
  }

  function onDigiConfirmation(dto: DigiConfirmationBroadcastDto) {
    // Ignore confirmations that don't belong to the current beacon
    if (dto.beaconId !== currentBeaconIds.value.get(dto.radioId)) return

    const existing = lastBeacons[dto.radioId]
    if (existing) {
      // Deduplicate by callsign — belt-and-suspenders guard against multiple
      // SignalR events arriving before the backend check can prevent duplicates.
      if (existing.confirmations.some((c) => c.digipeater === dto.digipeater)) return

      existing.confirmations = [
        ...existing.confirmations,
        {
          digipeater: dto.digipeater,
          confirmedAt: dto.confirmedAt,
          secondsAfterBeacon: dto.secondsAfterBeacon,
          lat: dto.lat,
          lon: dto.lon,
          aliasUsed: null,
        },
      ]
    }

    // Bump confirmation count on radio
    const radio = radios.value.find((r) => r.id === dto.radioId)
    if (radio) {
      radio.confirmationCount++
    }
  }

  // Store-level hub subscriptions — beacon state and modem RX/TX activity stay
  // live regardless of which view is open.
  const hub = usePacketHubStore()
  hub.on('ownBeaconReceived', (dto: OwnBeaconBroadcastDto) => onOwnBeaconReceived(dto))
  hub.on('digiConfirmation', (dto: DigiConfirmationBroadcastDto) => onDigiConfirmation(dto))
  hub.on('beaconConfirmedHeard', (dto: BeaconConfirmedHeardDto) => onBeaconConfirmedHeard(dto))
  hub.on('modemLevel', (batch: ModemLevelDto[]) => {
    for (const level of batch) {
      if (!level.carrierDetected) delete carrierRuns[level.radioId]
      else if (!carrierRuns[level.radioId])
        carrierRuns[level.radioId] = {
          since: Date.now(),
          decoded: modemStatus[level.radioId]?.decodedFrames ?? 0,
        }
      activity[level.radioId] = {
        carrierDetected: level.carrierDetected,
        transmitting: level.transmitting,
        updatedAt: Date.now(),
      }
    }
  })
  hub.onReconnected(async () => {
    await fetchRadios()
    await fetchAllLastBeacons()
  })
  hub.on('modemStatusChanged', (statuses: ModemStatusDto[]) => {
    const seen = new Set<string>()
    for (const s of statuses) {
      modemStatus[s.radioId] = s
      seen.add(s.radioId)
    }
    for (const id of Object.keys(modemStatus)) if (!seen.has(id)) delete modemStatus[id]
  })
  // A dropped hub means nothing shown is live any more — never leave an RX/TX
  // chip lit from before the disconnect.
  watch(
    () => hub.state,
    (state) => {
      if (state === 'disconnected' || state === 'connecting') {
        for (const id of Object.keys(activity)) delete activity[id]
        for (const id of Object.keys(modemStatus)) delete modemStatus[id]
        for (const id of Object.keys(carrierRuns)) delete carrierRuns[id]
      }
    },
  )

  /**
   * Seconds carrier has been held without a single new decode, once that
   * passes CARRIER_HELD_WARN_MS (or the backend flags it stuck); else null.
   */
  function carrierHeldSeconds(radioId: string): number | null {
    if (isActivityStale(radioId)) return null
    const run = carrierRuns[radioId]
    const status = modemStatus[radioId]
    if (!run) return null
    const held = activityClock.value - run.since
    const noDecodes = (status?.decodedFrames ?? 0) === run.decoded
    if (status?.carrierStuck || (held > CARRIER_HELD_WARN_MS && noDecodes))
      return Math.round(held / 1000)
    return null
  }

  /** Live modem status, or undefined when none has arrived (modem not running). */
  function getModemStatusForRadio(radioId: string): ModemStatusDto | undefined {
    return modemStatus[radioId]
  }

  /** True only while the radio's modem is actually Running with TX enabled. */
  function isTxLive(radioId: string): boolean {
    const s = modemStatus[radioId]
    return s?.state === ModemStates.Running && s.txEnabled
  }

  function isActivityStale(radioId: string): boolean {
    const a = activity[radioId]
    return !a || activityClock.value - a.updatedAt > LEVEL_STALE_MS
  }

  function getLastBeaconForRadio(radioId: string): LastBeaconDto | undefined {
    return lastBeacons[radioId]
  }

  /** Live activity, or undefined once it has gone stale. */
  function getActivityForRadio(radioId: string): RadioActivity | undefined {
    return isActivityStale(radioId) ? undefined : activity[radioId]
  }

  return {
    radios,
    lastBeacons,
    activity,
    loading,
    loaded,
    activeRadios,
    fetchRadios,
    fetchLastBeacon,
    fetchAllLastBeacons,
    onOwnBeaconReceived,
    onDigiConfirmation,
    getLastBeaconForRadio,
    getActivityForRadio,
    isActivityStale,
    modemStatus,
    getModemStatusForRadio,
    isTxLive,
    carrierHeldSeconds,
  }
})
