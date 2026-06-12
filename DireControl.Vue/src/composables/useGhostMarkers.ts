import type { Ref, ShallowRef } from 'vue'
import L from 'leaflet'
import { createAprsIcon, parseAprsSymbol } from '@/utils/aprsIcon'
import { estimatePosition } from '@/utils/estimatedPosition'
import type { StationDto } from '@/types/station'

export function useGhostMarkers(
  map: ShallowRef<L.Map | undefined>,
  deps: {
    showGhostMarkers: Ref<boolean>
    stationCache: Map<string, StationDto>
    isMobileStation: (station: StationDto) => boolean
  },
) {
  const { showGhostMarkers, stationCache, isMobileStation } = deps

  // Estimated position (ghost marker) state
  const ghostLayers = new Map<string, L.LayerGroup>()
  let ghostUpdateInterval: ReturnType<typeof setInterval> | null = null

  // Estimated Position (Ghost Markers)

  function removeGhostLayer(callsign: string) {
    const existing = ghostLayers.get(callsign)
    if (existing) {
      existing.remove()
      ghostLayers.delete(callsign)
    }
  }

  function updateGhostLayers() {
    if (!map.value) return
    // Skip the rebuild entirely while ghosts are hidden; toggling them back on
    // calls this directly.
    if (!showGhostMarkers.value) return
    for (const [callsign, station] of stationCache) {
      if (!isMobileStation(station) || station.lastLat == null || station.lastLon == null) {
        removeGhostLayer(callsign)
        continue
      }
      const est = estimatePosition(
        station.lastLat,
        station.lastLon,
        station.lastHeading,
        station.lastSpeed,
        station.lastSeen,
      )
      if (!est) {
        removeGhostLayer(callsign)
        continue
      }
      // Rebuild ghost layer for this station
      removeGhostLayer(callsign)
      const group = L.layerGroup()

      // Dashed connector: real position → estimated position
      L.polyline(
        [[station.lastLat, station.lastLon], [est.lat, est.lon]],
        { color: '#9E9E9E', weight: 2, dashArray: '6 5', opacity: 0.7 },
      ).addTo(group)

      // Uncertainty circle around estimated position
      L.circle([est.lat, est.lon], {
        radius: est.uncertaintyRadiusMeters,
        color: '#9E9E9E',
        weight: 1,
        dashArray: '4 4',
        fillOpacity: 0.05,
        opacity: 0.5,
      }).addTo(group)

      // Ghost marker: faded APRS icon
      const { table, code } = parseAprsSymbol(station.symbol)
      const ghostIcon = createAprsIcon(table, code, station.lastHeading, false, 0.45, false)
      const elapsedStr = est.elapsedMinutes < 1 ? '<1m' : `${Math.round(est.elapsedMinutes)}m`
      L.marker([est.lat, est.lon], { icon: ghostIcon })
        .bindTooltip(`Est. — ${elapsedStr} ago`, {
          permanent: true,
          direction: 'top',
          className: 'ghost-label',
          offset: [0, -14],
        })
        .addTo(group)

      ghostLayers.set(callsign, group)
      if (showGhostMarkers.value) {
        group.addTo(map.value)
      }
    }
  }

  function toggleGhostMarkers() {
    showGhostMarkers.value = !showGhostMarkers.value
    if (!map.value) return
    if (showGhostMarkers.value) {
      updateGhostLayers()
    } else {
      for (const group of ghostLayers.values()) {
        group.remove()
      }
      ghostLayers.clear()
    }
  }

  // Initial ghost render + periodic update every 30 s
  function startGhostUpdates() {
    updateGhostLayers()
    ghostUpdateInterval = setInterval(updateGhostLayers, 30_000)
  }

  function cleanup() {
    if (ghostUpdateInterval) {
      clearInterval(ghostUpdateInterval)
      ghostUpdateInterval = null
    }
    for (const group of ghostLayers.values()) {
      group.remove()
    }
    ghostLayers.clear()
  }

  return {
    removeGhostLayer,
    updateGhostLayers,
    toggleGhostMarkers,
    startGhostUpdates,
    cleanup,
  }
}
