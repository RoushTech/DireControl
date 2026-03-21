import { ref, watch, type Ref } from 'vue'

function pref<T>(key: string, defaultVal: T): Ref<T> {
  const stored = localStorage.getItem(`mapPrefs.${key}`)
  const ref_ = ref<T>(stored !== null ? (JSON.parse(stored) as T) : defaultVal)
  watch(ref_, (val) => localStorage.setItem(`mapPrefs.${key}`, JSON.stringify(val)))
  return ref_ as Ref<T>
}

export function useMapPrefs() {
  return {
    tracks: pref('tracks', true),
    estPos: pref('estPos', true),
    stale: pref('stale', false),
    zones: pref('zones', false),
    heatmap: pref('heatmap', false),
    coverage: pref('coverage', false),
    rangeRings: pref('rangeRings', false),
    ringDistance: pref('ringDistance', 25),
    ringPanelOpen: pref('ringPanelOpen', false),
    radar: pref('radar', false),
    wind: pref('wind', false),
    lightning: pref('lightning', false),
    radarOpacity: pref('radarOpacity', 0.6),
    windOpacity: pref('windOpacity', 0.7),
    lightningOpacity: pref('lightningOpacity', 0.8),
  }
}
