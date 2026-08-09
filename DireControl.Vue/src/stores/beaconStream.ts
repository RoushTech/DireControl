import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import type { PacketBroadcastDto } from '@/types/packet'
import { PacketSource, parsedTypeFromString } from '@/types/packet'
import { usePacketHubStore } from '@/stores/packetHub'

export type SourceFilter = '' | 'rf' | 'is'

const MAX_DISPLAYED = 200

export const useBeaconStreamStore = defineStore('beaconStream', () => {
  const displayedPackets = ref<PacketBroadcastDto[]>([])
  const pendingPackets = ref<PacketBroadcastDto[]>([])
  const paused = ref(false)

  // Filters — one search box matches callsign and summary text.
  const searchFilter = ref('')
  const typeFilter = ref<string>('') // '' = all
  const sourceFilter = ref<SourceFilter>('')

  const pendingCount = computed(() => pendingPackets.value.length)

  function addPacket(p: PacketBroadcastDto) {
    // Multiple views push into this store from their own hub connections, and a
    // REST seed can overlap live delivery — drop anything already present.
    if (
      displayedPackets.value.some((x) => x.id === p.id) ||
      pendingPackets.value.some((x) => x.id === p.id)
    ) {
      return
    }
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

  const hasActiveFilters = computed(
    () => searchFilter.value.trim() !== '' || typeFilter.value !== '' || sourceFilter.value !== '',
  )

  function clearFilters() {
    searchFilter.value = ''
    typeFilter.value = ''
    sourceFilter.value = ''
  }

  const filteredPackets = computed(() => {
    let list = displayedPackets.value
    const q = searchFilter.value.trim().toLowerCase()
    if (q) {
      list = list.filter(
        (p) => p.callsign.toLowerCase().includes(q) || p.summary.toLowerCase().includes(q),
      )
    }
    const tf = typeFilter.value
    if (tf) {
      list = list.filter((p) => {
        const pt = parsedTypeFromString(p.parsedType)
        return pt === Number(tf)
      })
    }
    if (sourceFilter.value === 'rf') {
      list = list.filter((p) => p.source !== PacketSource.AprsIs)
    } else if (sourceFilter.value === 'is') {
      list = list.filter((p) => p.source === PacketSource.AprsIs)
    }
    return list
  })

  // Store-level hub subscriptions — the stream buffer fills from app start, so
  // opening the Beacon Stream view shows history instead of starting cold.
  const hub = usePacketHubStore()
  hub.on('packetReceived', (p: PacketBroadcastDto) => addPacket(p))
  hub.on('packetSourceUpgraded', (upgrade: { id: number; source: PacketBroadcastDto['source'] }) =>
    upgradeSource(upgrade.id, upgrade.source),
  )

  return {
    displayedPackets,
    pendingCount,
    paused,
    searchFilter,
    typeFilter,
    sourceFilter,
    hasActiveFilters,
    clearFilters,
    filteredPackets,
    addPacket,
    pause,
    unpause,
    seedFromApi,
    upgradeSource,
  }
})
