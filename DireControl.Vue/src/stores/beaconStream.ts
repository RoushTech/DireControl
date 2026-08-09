import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import type { PacketBroadcastDto } from '@/types/packet'
import { parsedTypeFromString } from '@/types/packet'

const MAX_DISPLAYED = 200

export const useBeaconStreamStore = defineStore('beaconStream', () => {
  const displayedPackets = ref<PacketBroadcastDto[]>([])
  const pendingPackets = ref<PacketBroadcastDto[]>([])
  const paused = ref(false)

  // Filters
  const callsignFilter = ref('')
  const typeFilter = ref<string>('') // '' = all
  const textFilter = ref('')

  const pendingCount = computed(() => pendingPackets.value.length)

  function addPacket(p: PacketBroadcastDto) {
    if (paused.value) {
      pendingPackets.value.unshift(p)
    } else {
      displayedPackets.value.unshift(p)
      if (displayedPackets.value.length > MAX_DISPLAYED) {
        displayedPackets.value.splice(MAX_DISPLAYED)
      }
    }
  }

  function pause() {
    paused.value = true
  }

  function unpause() {
    paused.value = false
    if (pendingPackets.value.length > 0) {
      const combined = [...pendingPackets.value, ...displayedPackets.value]
      displayedPackets.value = combined.slice(0, MAX_DISPLAYED)
      pendingPackets.value = []
    }
  }

  /** Seed with packets from the REST API on initial load (newest first, matching addPacket). */
  function seedFromApi(packets: PacketBroadcastDto[]) {
    if (displayedPackets.value.length === 0) {
      displayedPackets.value = packets.slice(0, MAX_DISPLAYED)
    }
  }

  /**
   * A packet first heard via APRS-IS was subsequently heard on RF — update it
   * in place so the stream reflects what the radio actually decoded.
   */
  function upgradeSource(id: number, source: PacketBroadcastDto['source']) {
    for (const list of [displayedPackets.value, pendingPackets.value]) {
      const entry = list.find((p) => p.id === id)
      if (entry) entry.source = source
    }
  }

  const filteredPackets = computed(() => {
    let list = displayedPackets.value
    const cs = callsignFilter.value.trim().toUpperCase()
    if (cs) list = list.filter((p) => p.callsign.toUpperCase().includes(cs))
    const tf = typeFilter.value
    if (tf) {
      list = list.filter((p) => {
        const pt = parsedTypeFromString(p.parsedType)
        return pt === Number(tf)
      })
    }
    const tx = textFilter.value.trim().toLowerCase()
    if (tx) list = list.filter((p) => p.summary.toLowerCase().includes(tx))
    return list
  })

  return {
    displayedPackets,
    pendingCount,
    paused,
    callsignFilter,
    typeFilter,
    textFilter,
    filteredPackets,
    addPacket,
    pause,
    unpause,
    seedFromApi,
    upgradeSource,
  }
})
