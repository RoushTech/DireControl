import { defineStore } from 'pinia'
import { ref } from 'vue'

export const useStationSelectionStore = defineStore('stationSelection', () => {
  const selectedCallsign = ref<string | null>(null)

  function selectStation(callsign: string) {
    selectedCallsign.value = callsign
  }

  function deselect() {
    selectedCallsign.value = null
  }

  return { selectedCallsign, selectStation, deselect }
})
