import http from './axios'

export interface StatusDto {
  direwolfConnected: boolean
  apiOnline: boolean
  aprsIsState: string
  aprsIsServerName: string | null
  aprsIsFilter: string
  aprsIsSessionPacketCount: number
  aprsIsFirstDisconnectedAt: string | null
  aprsIsLastConnectAttemptAt: string | null
  aprsIsFailedAttempts: number
  aprsIsLastError: string | null
}

export async function getStatus(): Promise<StatusDto> {
  const { data } = await http.get<StatusDto>('/api/v0/status')
  return data
}

export async function reconnectAprsIs(): Promise<void> {
  await http.post('/api/v0/status/aprs-is/reconnect')
}
