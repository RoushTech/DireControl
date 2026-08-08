import http from './axios'

export const ModemStates = {
  Unknown: 0,
  Disabled: 1,
  Running: 2,
  Error: 3,
} as const
export type ModemState = (typeof ModemStates)[keyof typeof ModemStates]

export const modemStateLabels: Record<ModemState, string> = {
  [ModemStates.Unknown]: 'Unknown',
  [ModemStates.Disabled]: 'Disabled',
  [ModemStates.Running]: 'Running',
  [ModemStates.Error]: 'Error',
}

export const PttMethods = {
  Unknown: 0,
  None: 1,
  SerialRtsDtr: 2,
  Cm108: 3,
  Gpio: 4,
  Rigctld: 5,
} as const
export type PttMethod = (typeof PttMethods)[keyof typeof PttMethods]

export interface ModemStatusDto {
  radioId: string
  radioName: string
  fullCallsign: string
  channel: number
  state: ModemState
  captureDevice: string | null
  errorMessage: string | null
  audioLevel: number
  carrierDetected: boolean
  decodedFrames: number
  invalidFrames: number
  txEnabled: boolean
  transmitting: boolean
  transmittedFrames: number
  rigFrequencyHz: number | null
  decodedByProfile: Record<string, number> | null
}

export interface RfServicesPayload {
  digipeaterEnabled: boolean
  digipeaterMaxWideN: number
  digipeaterFillInOnly: boolean
  kissServerEnabled: boolean
  kissServerPort: number
  rfToIsGatingEnabled: boolean
  isToRfGatingEnabled: boolean
  isToRfPath: string
  isToRfRecentHeardMinutes: number
}

export async function updateRfServices(payload: RfServicesPayload): Promise<void> {
  await http.put('/api/v0/settings/rf-services', payload)
}

export interface ModemDeviceDto {
  name: string
  description: string
  supportsCapture: boolean
  supportsPlayback: boolean
}

export interface HidDeviceDto {
  path: string
  name: string
}

export interface ModemDevicesDto {
  audio: ModemDeviceDto[]
  serialPorts: string[]
  hidDevices: HidDeviceDto[]
}

export interface ModemLevelDto {
  radioId: string
  channel: number
  audioLevel: number
  carrierDetected: boolean
  transmitting: boolean
}

export interface ModemSpectrumDto {
  radioId: string
  bins: number[] | string
}

/** SignalR's JSON protocol delivers byte[] as base64 — normalise either form. */
export function decodeSpectrumPayload(bins: number[] | string): number[] {
  return typeof bins === 'string' ? Array.from(atob(bins), (c) => c.charCodeAt(0)) : bins
}

export async function getModemStatus(): Promise<ModemStatusDto[]> {
  const { data } = await http.get<ModemStatusDto[]>('/api/v0/modem/status')
  return data
}

export async function getModemDevices(): Promise<ModemDevicesDto> {
  const { data } = await http.get<ModemDevicesDto>('/api/v0/modem/devices')
  return data
}

export async function restartModem(): Promise<void> {
  await http.post('/api/v0/modem/restart')
}
