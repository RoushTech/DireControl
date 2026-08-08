import type { PttMethod } from '@/api/modemApi'

/** Per-radio sound modem + PTT configuration (audio feed). */
export interface RadioModemConfig {
  modemEnabled: boolean
  modemCaptureDevice: string
  modemPlaybackDevice: string
  txEnabled: boolean
  txAudioLevelPct: number
  txDelayMs: number
  txTailMs: number
  txPersistence: number
  txSlotTimeMs: number
  pttMethod: PttMethod
  pttSerialPort: string | null
  pttSerialUseRts: boolean
  pttSerialUseDtr: boolean
  pttHidDevice: string | null
  pttHidPin: number
  pttGpioChip: number
  pttGpioLine: number
  pttGpioActiveLow: boolean
  pttRigctldHost: string
  pttRigctldPort: number
}

export function defaultRadioModemConfig(): RadioModemConfig {
  return {
    modemEnabled: false,
    modemCaptureDevice: 'default',
    modemPlaybackDevice: 'default',
    txEnabled: false,
    txAudioLevelPct: 80,
    txDelayMs: 300,
    txTailMs: 50,
    txPersistence: 63,
    txSlotTimeMs: 100,
    pttMethod: 1,
    pttSerialPort: null,
    pttSerialUseRts: true,
    pttSerialUseDtr: false,
    pttHidDevice: null,
    pttHidPin: 3,
    pttGpioChip: 0,
    pttGpioLine: 0,
    pttGpioActiveLow: false,
    pttRigctldHost: 'localhost',
    pttRigctldPort: 4532,
  }
}

export interface RadioDto {
  id: string
  name: string
  callsign: string
  ssid: string | null
  fullCallsign: string
  channelNumber: number
  notes: string | null
  beaconPath: string | null
  beaconSymbol: string | null
  beaconComment: string | null
  isActive: boolean
  frequencyMhz: number | null
  mode: string | null
  modem: RadioModemConfig
  expectedIntervalSeconds: number
  autoBeaconEnabled: boolean
  autoBeaconIntervalSeconds: number
  lastBeaconedAt: string | null
  secondsSinceBeacon: number | null
  confirmationCount: number
  beaconCount: number
}

export interface CreateRadioRequest {
  name: string
  callsign: string
  ssid: string | null
  channelNumber: number
  notes: string | null
  beaconPath: string | null
  beaconSymbol: string | null
  beaconComment: string | null
  expectedIntervalSeconds: number
  autoBeaconEnabled: boolean
  autoBeaconIntervalSeconds: number
  frequencyMhz: number | null
  mode: string | null
  modem: RadioModemConfig
}

export interface UpdateRadioRequest {
  name: string
  callsign: string
  ssid: string | null
  channelNumber: number
  notes: string | null
  beaconPath: string | null
  beaconSymbol: string | null
  beaconComment: string | null
  expectedIntervalSeconds: number
  autoBeaconEnabled: boolean
  autoBeaconIntervalSeconds: number
  frequencyMhz: number | null
  mode: string | null
  modem: RadioModemConfig
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
  heard: boolean
  confirmations: DigiConfirmationDto[]
}

export interface OwnBeaconHistoryItemDto {
  id: number
  beaconedAt: string
  latitude: number | null
  longitude: number | null
  pathUsed: string | null
  hopCount: number
  heard: boolean
  confirmations: DigiConfirmationDto[]
}

export interface OwnBeaconBroadcastDto {
  radioId: string
  beaconId: number
  fullCallsign: string
  beaconedAt: string
  lat: number | null
  lon: number | null
  pathUsed: string | null
  heard: boolean
}

export interface BeaconConfirmedHeardDto {
  radioId: string
  beaconId: number
}

export interface DigiConfirmationBroadcastDto {
  radioId: string
  beaconId: number
  fullCallsign: string
  digipeater: string
  confirmedAt: string
  secondsAfterBeacon: number
  lat: number | null
  lon: number | null
}
