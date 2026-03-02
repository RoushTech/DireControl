import { ref } from 'vue'

const UNIT_STORAGE_KEY = 'preferences.distanceUnit'
const defaultUnit: 'km' | 'mi' = navigator.language?.startsWith('en-US') ? 'mi' : 'km'
const distanceUnit = ref<'km' | 'mi'>(
  (localStorage.getItem(UNIT_STORAGE_KEY) as 'km' | 'mi' | null) ?? defaultUnit,
)

export function useUnits() {
  function formatDistance(km: number): string {
    if (distanceUnit.value === 'mi') {
      const miles = km * 0.621371
      return miles < 10 ? `${miles.toFixed(1)} mi` : `${Math.round(miles)} mi`
    }
    return km < 10 ? `${km.toFixed(1)} km` : `${Math.round(km)} km`
  }

  function setDistanceUnit(unit: 'km' | 'mi') {
    distanceUnit.value = unit
    localStorage.setItem(UNIT_STORAGE_KEY, unit)
  }

  return { distanceUnit, formatDistance, setDistanceUnit }
}
