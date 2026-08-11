import http from './axios'
import type { TerminalSessionOrigin } from './terminalApi'

export const PmsMessageTypes = {
  Unknown: 0,
  Private: 1,
  Bulletin: 2,
} as const
export type PmsMessageType = (typeof PmsMessageTypes)[keyof typeof PmsMessageTypes]

export const pmsMessageTypeLabels: Record<PmsMessageType, string> = {
  [PmsMessageTypes.Unknown]: 'Unknown',
  [PmsMessageTypes.Private]: 'Private',
  [PmsMessageTypes.Bulletin]: 'Bulletin',
}

export interface PmsMessageDto {
  id: number
  type: PmsMessageType
  fromCallsign: string
  toCallsign: string
  subject: string | null
  body: string | null
  createdAt: string
  readAt: string | null
  isKilled: boolean
  origin: TerminalSessionOrigin
}

export interface PmsMessagesPageDto {
  messages: PmsMessageDto[]
  totalCount: number
}

export async function getPmsMessages(params: {
  page?: number
  pageSize?: number
  includeKilled?: boolean
}): Promise<PmsMessagesPageDto> {
  const { data } = await http.get<PmsMessagesPageDto>('/api/v0/pms/messages', { params })
  return data
}

export async function createPmsMessage(request: {
  type: PmsMessageType
  toCallsign: string
  subject?: string
  body?: string
}): Promise<PmsMessageDto> {
  const { data } = await http.post<PmsMessageDto>('/api/v0/pms/messages', request)
  return data
}

/** Marks the message killed (BBS-style soft delete) without removing it. */
export async function killPmsMessage(id: number): Promise<void> {
  await http.post(`/api/v0/pms/messages/${id}/kill`)
}

export async function deletePmsMessage(id: number): Promise<void> {
  await http.delete(`/api/v0/pms/messages/${id}`)
}
