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

export interface ExternalTncPayload {
  direwolfEnabled: boolean
  direwolfHost: string
  direwolfPort: number
  direwolfReconnectDelaySeconds: number
}

/** Updates the external KISS/TCP TNC (Direwolf) client settings. */
export async function updateExternalTnc(payload: ExternalTncPayload): Promise<void> {
  await http.put('/api/v0/settings/external-tnc', payload)
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

/** Live TX audio-level (gain) adjustment — persists and applies without a restart. */
export async function setModemTxLevel(radioId: string, txAudioLevelPct: number): Promise<void> {
  await http.put(`/api/v0/modem/${radioId}/tx-level`, { txAudioLevelPct })
}

export const TestToneKinds = {
  Mark: 1,
  Space: 2,
  Alternating: 3,
} as const
export type TestToneKind = (typeof TestToneKinds)[keyof typeof TestToneKinds]

/** Transmits a TX calibration test tone on the radio's modem for `durationMs`. */
export async function sendTestTone(
  radioId: string,
  kind: TestToneKind,
  durationMs = 2000,
): Promise<void> {
  await http.post(`/api/v0/modem/${radioId}/test-tone`, { kind, durationMs })
}

/** One captured off-air recording available for offline replay. */
export interface AudioCaptureDto {
  name: string
  sizeBytes: number
  capturedAtUtc: string
  durationSeconds: number
  /** "missed-decode" or "manual". */
  reason: string
}

/** Starts recording a radio's raw off-air audio at the modem's own sample rate. */
export async function startAudioRecording(radioId: string): Promise<void> {
  await http.post(`/api/v0/modem/${radioId}/record`)
}

export async function stopAudioRecording(radioId: string): Promise<void> {
  await http.delete(`/api/v0/modem/${radioId}/record`)
}

export async function getAudioCaptures(): Promise<AudioCaptureDto[]> {
  const { data } = await http.get<AudioCaptureDto[]>('/api/v0/modem/captures')
  return data
}

export async function deleteAudioCapture(name: string): Promise<void> {
  await http.delete(`/api/v0/modem/captures/${encodeURIComponent(name)}`)
}

/** Radio IDs with a manual recording in progress, per the server. */
export async function getRecordingRadios(): Promise<string[]> {
  const { data } = await http.get<string[]>('/api/v0/modem/recording')
  return data
}

/** State of automatic capture of failed decodes. */
export interface AudioCaptureSettingsDto {
  enabled: boolean
  captureCount: number
}

export async function getAudioCaptureSettings(): Promise<AudioCaptureSettingsDto> {
  const { data } = await http.get<AudioCaptureSettingsDto>('/api/v0/modem/capture')
  return data
}

export async function setAudioCaptureEnabled(enabled: boolean): Promise<void> {
  await http.put('/api/v0/modem/capture', { enabled })
}

/**
 * The log pack is a zip of tens of megabytes, so it is fetched by navigating to
 * this URL rather than through Axios — buffering it as a blob would be wasteful
 * and would lose the browser's own download progress.
 */
export const AUDIO_LOG_PACK_URL = '/api/v0/modem/captures/pack'
