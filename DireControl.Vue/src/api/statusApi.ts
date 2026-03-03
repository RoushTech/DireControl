import http from './axios'

export interface StatusDto {
  direwolfConnected: boolean
  apiOnline: boolean
  aprsIsState: string
  aprsIsServerName: string | null
  aprsIsFilter: string
  aprsIsSessionPacketCount: number
}

export async function getStatus(): Promise<StatusDto> {
  const { data } = await http.get<StatusDto>('/api/v0/status')
  return data
}
