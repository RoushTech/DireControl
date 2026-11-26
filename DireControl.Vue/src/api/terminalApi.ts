import http from './axios'

// ─── Enums (serialized as numbers, per project convention) ───────────────────

export const TerminalSessionOrigins = {
  Unknown: 0,
  Outbound: 1,
  Inbound: 2,
  Agwpe: 3,
  LocalPms: 4,
} as const
export type TerminalSessionOrigin =
  (typeof TerminalSessionOrigins)[keyof typeof TerminalSessionOrigins]

export const terminalSessionOriginLabels: Record<TerminalSessionOrigin, string> = {
  [TerminalSessionOrigins.Unknown]: 'Unknown',
  [TerminalSessionOrigins.Outbound]: 'Outbound',
  [TerminalSessionOrigins.Inbound]: 'Inbound',
  [TerminalSessionOrigins.Agwpe]: 'AGWPE',
  [TerminalSessionOrigins.LocalPms]: 'Local PMS',
}

export const TerminalSessionStates = {
  Unknown: 0,
  Connecting: 1,
  Connected: 2,
  Disconnecting: 3,
  Disconnected: 4,
  Error: 5,
} as const
export type TerminalSessionState =
  (typeof TerminalSessionStates)[keyof typeof TerminalSessionStates]

export const terminalSessionStateLabels: Record<TerminalSessionState, string> = {
  [TerminalSessionStates.Unknown]: 'Unknown',
  [TerminalSessionStates.Connecting]: 'Connecting',
  [TerminalSessionStates.Connected]: 'Connected',
  [TerminalSessionStates.Disconnecting]: 'Disconnecting',
  [TerminalSessionStates.Disconnected]: 'Disconnected',
  [TerminalSessionStates.Error]: 'Error',
}

export const TranscriptDirections = {
  Unknown: 0,
  Received: 1,
  Sent: 2,
} as const
export type TranscriptDirection = (typeof TranscriptDirections)[keyof typeof TranscriptDirections]

// ─── Base64 <-> bytes helpers (terminal data travels base64-encoded) ─────────

export function bytesToBase64(bytes: Uint8Array): string {
  let binary = ''
  for (const b of bytes) binary += String.fromCharCode(b)
  return btoa(binary)
}

export function base64ToBytes(b64: string): Uint8Array {
  return Uint8Array.from(atob(b64), (c) => c.charCodeAt(0))
}

/**
 * Packet streams end lines with a bare CR (some hosts use LF or CRLF), but a
 * bare CR only returns the xterm cursor to column 0 — lines would overwrite
 * each other. Returns a stateful transform that normalizes every line ending
 * to CRLF, keeping a CR|LF pair split across two chunks a single break.
 */
export function createCrlfNormalizer(): (bytes: Uint8Array) => Uint8Array {
  let swallowLf = false
  return (bytes) => {
    const out: number[] = []
    for (const b of bytes) {
      if (b === 0x0a) {
        if (swallowLf) {
          swallowLf = false
          continue // second half of a CRLF pair — already emitted
        }
        out.push(0x0d, 0x0a)
        continue
      }
      swallowLf = false
      if (b === 0x0d) {
        out.push(0x0d, 0x0a)
        swallowLf = true
        continue
      }
      out.push(b)
    }
    return Uint8Array.from(out)
  }
}

// ─── Sessions ────────────────────────────────────────────────────────────────

export interface TerminalSessionStatsDto {
  vs: number
  vr: number
  va: number
  outstandingIFrames: number
  retryCount: number
  sendQueueDepth: number
  bytesIn: number
  bytesOut: number
}

export interface TerminalSessionDto {
  id: string
  origin: TerminalSessionOrigin
  state: TerminalSessionState
  channel: number
  localCallsign: string
  remoteCallsign: string
  digiPath: string | null
  startedAt: string
  endReason: string | null
  stats: TerminalSessionStatsDto
}

export interface OpenTerminalSessionRequest {
  channel: number
  remoteCallsign: string
  localCallsign?: string
  digiPath?: string
  mod128?: boolean
  pacLen?: number
  windowSize?: number
  maxRetries?: number
  t1Seconds?: number
}

export async function getSessions(): Promise<TerminalSessionDto[]> {
  const { data } = await http.get<TerminalSessionDto[]>('/api/v0/terminal/sessions')
  return data
}

export async function openSession(
  request: OpenTerminalSessionRequest,
): Promise<TerminalSessionDto> {
  const { data } = await http.post<TerminalSessionDto>('/api/v0/terminal/sessions', request)
  return data
}

/** Opens a loopback session against our own PMS mailbox (503 when PMS is disabled). */
export async function openLocalPmsSession(): Promise<TerminalSessionDto> {
  const { data } = await http.post<TerminalSessionDto>('/api/v0/terminal/sessions/local-pms')
  return data
}

export async function closeSession(id: string, abort = false): Promise<void> {
  await http.delete(`/api/v0/terminal/sessions/${id}`, { params: { abort } })
}

// ─── Transcripts ─────────────────────────────────────────────────────────────

export interface TerminalTranscriptSummaryDto {
  id: number
  sessionId: string
  origin: TerminalSessionOrigin
  channel: number
  localCallsign: string
  remoteCallsign: string
  digiPath: string | null
  startedAt: string
  endedAt: string | null
  endReason: string | null
  bytesIn: number
  bytesOut: number
}

export interface TerminalTranscriptChunkDto {
  timestamp: string
  direction: TranscriptDirection
  dataBase64: string
}

export interface TerminalTranscriptDetailDto {
  summary: TerminalTranscriptSummaryDto
  chunks: TerminalTranscriptChunkDto[]
}

export async function getTranscripts(params: {
  page?: number
  pageSize?: number
  callsign?: string
}): Promise<TerminalTranscriptSummaryDto[]> {
  const { data } = await http.get<TerminalTranscriptSummaryDto[]>('/api/v0/terminal/transcripts', {
    params,
  })
  return data
}

export async function getTranscript(id: number): Promise<TerminalTranscriptDetailDto> {
  const { data } = await http.get<TerminalTranscriptDetailDto>(`/api/v0/terminal/transcripts/${id}`)
  return data
}

export async function deleteTranscript(id: number): Promise<void> {
  await http.delete(`/api/v0/terminal/transcripts/${id}`)
}

// ─── Presets ─────────────────────────────────────────────────────────────────

export interface TerminalPresetDto {
  id: number
  name: string | null
  remoteCallsign: string
  channel: number
  digiPath: string | null
  localCallsign: string | null
  isPinned: boolean
  lastUsedAt: string | null
  useCount: number
  mod128: boolean | null
  pacLen: number | null
  windowSize: number | null
  maxRetries: number | null
  t1Seconds: number | null
}

export interface SaveTerminalPresetRequest {
  name?: string
  remoteCallsign: string
  channel: number
  digiPath?: string
  localCallsign?: string
  isPinned: boolean
  mod128?: boolean
  pacLen?: number
  windowSize?: number
  maxRetries?: number
  t1Seconds?: number
}

export async function listPresets(): Promise<TerminalPresetDto[]> {
  const { data } = await http.get<TerminalPresetDto[]>('/api/v0/terminal/presets')
  return data
}

export async function createPreset(request: SaveTerminalPresetRequest): Promise<TerminalPresetDto> {
  const { data } = await http.post<TerminalPresetDto>('/api/v0/terminal/presets', request)
  return data
}

export async function updatePreset(
  id: number,
  request: SaveTerminalPresetRequest,
): Promise<TerminalPresetDto> {
  const { data } = await http.put<TerminalPresetDto>(`/api/v0/terminal/presets/${id}`, request)
  return data
}

export async function deletePreset(id: number): Promise<void> {
  await http.delete(`/api/v0/terminal/presets/${id}`)
}

// ─── Macros ──────────────────────────────────────────────────────────────────

export interface TerminalMacroDto {
  id: number
  label: string
  sortOrder: number
  payloadBase64: string
  appendCr: boolean
}

export interface SaveTerminalMacroRequest {
  label: string
  sortOrder: number
  payloadBase64: string
  appendCr: boolean
}

export async function listMacros(): Promise<TerminalMacroDto[]> {
  const { data } = await http.get<TerminalMacroDto[]>('/api/v0/terminal/macros')
  return data
}

export async function createMacro(request: SaveTerminalMacroRequest): Promise<TerminalMacroDto> {
  const { data } = await http.post<TerminalMacroDto>('/api/v0/terminal/macros', request)
  return data
}

export async function updateMacro(
  id: number,
  request: SaveTerminalMacroRequest,
): Promise<TerminalMacroDto> {
  const { data } = await http.put<TerminalMacroDto>(`/api/v0/terminal/macros/${id}`, request)
  return data
}

export async function deleteMacro(id: number): Promise<void> {
  await http.delete(`/api/v0/terminal/macros/${id}`)
}
