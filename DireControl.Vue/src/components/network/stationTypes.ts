import { StationType } from '@/types/station'

export const stationTypeLabel: Record<StationType, string> = {
  [StationType.Fixed]: 'Fixed',
  [StationType.Mobile]: 'Mobile',
  [StationType.Weather]: 'Weather',
  [StationType.Digipeater]: 'Digipeater',
  [StationType.IGate]: 'IGate',
  [StationType.Unknown]: 'Unknown',
  [StationType.Gateway]: 'Gateway',
}

export const stationTypeColor: Record<StationType, string> = {
  [StationType.Fixed]: 'blue',
  [StationType.Mobile]: 'green',
  [StationType.Weather]: 'teal',
  [StationType.Digipeater]: 'orange',
  [StationType.IGate]: 'purple',
  [StationType.Unknown]: 'grey',
  [StationType.Gateway]: 'deep-purple',
}
