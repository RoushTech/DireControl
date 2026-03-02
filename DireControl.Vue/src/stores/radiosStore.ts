import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr'
import { getRadios, getLastBeacon } from '@/api/radiosApi'
import type { RadioDto, LastBeaconDto, OwnBeaconBroadcastDto, DigiConfirmationBroadcastDto } from '@/types/radio'

export const useRadiosStore = defineStore('radios', () => {
  const radios = ref<RadioDto[]>([])
  const lastBeacons = ref<Map<string, LastBeaconDto>>(new Map())
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
      lastBeacons.value.set(radioId, dto)
    } catch { /* ignore */ }
  }

  async function fetchAllLastBeacons() {
    for (const radio of activeRadios.value) {
      await fetchLastBeacon(radio.id)
    }
  }

  function onOwnBeaconReceived(dto: OwnBeaconBroadcastDto) {
    // Update the radio's lastBeaconedAt in the radios list
    const radio = radios.value.find((r) => r.id === dto.radioId)
    if (radio) {
      radio.lastBeaconedAt = dto.beaconedAt
      radio.secondsSinceBeacon = 0
    }

    // Update the lastBeacons map — always replace via set() so Vue's reactive
    // Map triggers dependency tracking on the new entry (property mutation on a
    // Map value is not reliably tracked).
    const existing = lastBeacons.value.get(dto.radioId)
    lastBeacons.value.set(dto.radioId, {
      radioId: dto.radioId,
      radioName: existing?.radioName ?? radio?.name ?? dto.fullCallsign,
      fullCallsign: dto.fullCallsign,
      beaconedAt: dto.beaconedAt,
      secondsSinceBeacon: 0,
      latitude: dto.lat,
      longitude: dto.lon,
      pathUsed: dto.pathUsed,
      comment: existing?.comment ?? null,
      confirmations: [],
    })
  }

  function onDigiConfirmation(dto: DigiConfirmationBroadcastDto) {
    const existing = lastBeacons.value.get(dto.radioId)
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
    return lastBeacons.value.get(radioId)
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
