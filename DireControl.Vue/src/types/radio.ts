export interface RadioDto {
  id: string
  name: string
  callsign: string
  ssid: string | null
  fullCallsign: string
  direwolfPort: number
  notes: string | null
  isActive: boolean
  expectedIntervalSeconds: number
  lastBeaconedAt: string | null
  secondsSinceBeacon: number | null
  confirmationCount: number
  beaconCount: number
}

export interface CreateRadioRequest {
  name: string
  callsign: string
  ssid: string | null
  direwolfPort: number
  notes: string | null
  expectedIntervalSeconds: number
}

export interface UpdateRadioRequest {
  name: string
  callsign: string
  ssid: string | null
  direwolfPort: number
  notes: string | null
  expectedIntervalSeconds: number
}

export interface DigiConfirmationDto {
  digipeater: string
  confirmedAt: string
  secondsAfterBeacon: number
  lat: number | null
  lon: number | null
  aliasUsed: string | null
}

export interface LastBeaconDto {
  radioId: string
  radioName: string
  fullCallsign: string
  beaconedAt: string | null
  secondsSinceBeacon: number | null
  latitude: number | null
  longitude: number | null
  pathUsed: string | null
  comment: string | null
  confirmations: DigiConfirmationDto[]
}

export interface OwnBeaconHistoryItemDto {
  id: number
  beaconedAt: string
  latitude: number | null
  longitude: number | null
  pathUsed: string | null
  hopCount: number
  confirmations: DigiConfirmationDto[]
}

export interface OwnBeaconBroadcastDto {
  radioId: string
  fullCallsign: string
  beaconedAt: string
  lat: number | null
  lon: number | null
  pathUsed: string | null
}

export interface DigiConfirmationBroadcastDto {
  radioId: string
  fullCallsign: string
  digipeater: string
  confirmedAt: string
  secondsAfterBeacon: number
  lat: number | null
  lon: number | null
}
