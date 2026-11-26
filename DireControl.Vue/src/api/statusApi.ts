import http from './axios'
import type { ModemState } from './modemApi'

/** Mirrors DireControl.Enums.AprsIsConnectionState (numeric JSON). */
export const AprsIsStates = {
  Unknown: 0,
  Disabled: 1,
  Connecting: 2,
  Connected: 3,
  AuthFailed: 4,
  Disconnected: 5,
} as const
export type AprsIsState = (typeof AprsIsStates)[keyof typeof AprsIsStates]

export const aprsIsStateLabels: Record<AprsIsState, string> = {
  [AprsIsStates.Unknown]: 'Unknown',
  [AprsIsStates.Disabled]: 'Disabled',
  [AprsIsStates.Connecting]: 'Connecting…',
  [AprsIsStates.Connected]: 'Connected',
  [AprsIsStates.AuthFailed]: 'Auth failed',
  [AprsIsStates.Disconnected]: 'Disconnected',
}

export interface StatusDto {
  /** The external KISS TCP TNC client (Direwolf etc.) is connected. */
  externalTncConnected: boolean
  aprsIsState: AprsIsState
  /** The server verified our login on the current connection. */
  aprsIsVerified: boolean
  /** Last line received from APRS-IS — IS freshness, never RF health. */
  lastIsPacketAt: string | null
  aprsIsServerName: string | null
  aprsIsFilter: string
  aprsIsSessionPacketCount: number
  aprsIsFirstDisconnectedAt: string | null
  aprsIsLastConnectAttemptAt: string | null
  aprsIsFailedAttempts: number
  aprsIsLastError: string | null
  modemState: ModemState
  /** Newest RF decode across running modems (APRS-IS traffic never sets this). */
  modemLastDecodedAt: string | null
  modemRunningSince: string | null
  digipeatedFrames: number
  kissServerClients: number
  rfToIsGatedLines: number
}

export async function getStatus(): Promise<StatusDto> {
  const { data } = await http.get<StatusDto>('/api/v0/status')
  return data
}

export async function reconnectAprsIs(): Promise<void> {
  await http.post('/api/v0/status/aprs-is/reconnect')
}
