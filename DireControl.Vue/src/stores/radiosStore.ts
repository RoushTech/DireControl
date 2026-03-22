import { defineStore } from 'pinia'
import { ref, computed, reactive } from 'vue'
import { HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr'
import { getRadios, getLastBeacon } from '@/api/radiosApi'
import type { RadioDto, LastBeaconDto, OwnBeaconBroadcastDto, DigiConfirmationBroadcastDto, BeaconConfirmedHeardDto } from '@/types/radio'

export const useRadiosStore = defineStore('radios', () => {
  const radios = ref<RadioDto[]>([])
  // reactive plain-object keyed by radioId — more reliable than ref<Map> because
  // Vue 3 property-assignment triggers are always tracked for plain reactive objects.
  const lastBeacons = reactive<Record<string, LastBeaconDto | undefined>>({})
  const currentBeaconIds = ref<Map<string, number>>(new Map())
  const loading = ref(false)
  let connectionStarted = false

  const activeRadios = computed(() => radios.value.filter((r) => r.isActive))

  async function fetchRadios() {
    loading.value = true
    try {
      radios.value = await getRadios()
    } finally {
      loading.value = false
    }
  }

  async function fetchLastBeacon(radioId: string) {
    try {
      const dto = await getLastBeacon(radioId)
      lastBeacons[radioId] = dto
    } catch { /* ignore */ }
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

  function startSignalR() {
    if (connectionStarted) return
    connectionStarted = true

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/packets')
      .withAutomaticReconnect()
      .build()

    connection.on('ownBeaconReceived', (dto: OwnBeaconBroadcastDto) => {
      onOwnBeaconReceived(dto)
    })

    connection.on('digiConfirmation', (dto: DigiConfirmationBroadcastDto) => {
      onDigiConfirmation(dto)
    })

    connection.on('beaconConfirmedHeard', (dto: BeaconConfirmedHeardDto) => {
      onBeaconConfirmedHeard(dto)
    })

    async function start() {
      try {
        await connection.start()
      } catch {
        setTimeout(start, 5000)
      }
    }

    connection.onclose(() => {
      if (connection.state !== HubConnectionState.Reconnecting) {
        setTimeout(start, 5000)
      }
    })

    start()
  }

  function getLastBeaconForRadio(radioId: string): LastBeaconDto | undefined {
    return lastBeacons[radioId]
  }

  return {
    radios,
    lastBeacons,
    loading,
    activeRadios,
    fetchRadios,
    fetchLastBeacon,
    fetchAllLastBeacons,
    onOwnBeaconReceived,
    onDigiConfirmation,
    startSignalR,
    getLastBeaconForRadio,
  }
})
