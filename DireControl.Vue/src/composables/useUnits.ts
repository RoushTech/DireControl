import { computed, ref } from 'vue'

const UNIT_STORAGE_KEY = 'preferences.distanceUnit'

function readUnit(): 'km' | 'mi' {
  try {
    const v = localStorage.getItem(UNIT_STORAGE_KEY)
    if (v === 'km' || v === 'mi') return v
  } catch {
    /* private window */
  }
  return navigator.language?.startsWith('en-US') ? 'mi' : 'km'
}

/** One preference drives every unit: miles → imperial (ft, mph, °F), km → metric (m, km/h, °C). */
const distanceUnit = ref<'km' | 'mi'>(readUnit())

/**
 * Unit-aware formatting. Inputs are always in what APRS carries: distance in
 * km (computed), altitude in feet (`/A=`), course speed in knots, weather wind
 * in mph and temperature in °F.
 */
export function useUnits() {
  const imperial = computed(() => distanceUnit.value === 'mi')

  function formatDistance(km: number): string {
    if (imperial.value) {
      const miles = km * 0.621371
      return miles < 10 ? `${miles.toFixed(1)} mi` : `${Math.round(miles)} mi`
    }
    return km < 10 ? `${km.toFixed(1)} km` : `${Math.round(km)} km`
  }

  function formatAltitude(feet: number): string {
    return imperial.value
      ? `${Math.round(feet).toLocaleString()} ft`
      : `${Math.round(feet * 0.3048).toLocaleString()} m`
  }

  /** Course speed (APRS knots) → mph or km/h. */
  function formatSpeedKnots(knots: number): string {
    return imperial.value
      ? `${Math.round(knots * 1.15078)} mph`
      : `${Math.round(knots * 1.852)} km/h`
  }

  /** Wind as converted to mph by the parser. */
  const windUnit = computed(() => (imperial.value ? 'mph' : 'km/h'))
  function windFromMph(mph: number): number {
    return imperial.value ? mph : mph * 1.60934
  }
  function formatWindMph(mph: number): string {
    return `${windFromMph(mph).toFixed(1)} ${windUnit.value}`
  }

  const tempUnit = computed(() => (imperial.value ? '°F' : '°C'))
  function tempFromF(f: number): number {
    return imperial.value ? f : ((f - 32) * 5) / 9
  }
  function formatTempF(f: number): string {
    return `${tempFromF(f).toFixed(1)} ${tempUnit.value}`
  }

  function setDistanceUnit(unit: 'km' | 'mi') {
    distanceUnit.value = unit
    try {
      localStorage.setItem(UNIT_STORAGE_KEY, unit)
    } catch {
      /* lasts for this page load */
    }
  }

  return {
    distanceUnit,
    imperial,
    formatDistance,
    formatAltitude,
    formatSpeedKnots,
    windUnit,
    windFromMph,
    formatWindMph,
    tempUnit,
    tempFromF,
    formatTempF,
    setDistanceUnit,
  }
}
