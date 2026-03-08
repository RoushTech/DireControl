import http from './axios'
import type {
  AllMessagePacketDto,
  InboxMessageDto,
  PaginatedResponse,
  SendMessageRequest,
} from '@/types/message'

export async function getInboxMessages(): Promise<InboxMessageDto[]> {
  const { data } = await http.get<InboxMessageDto[]>('/api/v0/messages/inbox')
  return data
}

export interface GetAllMessagesParams {
  page?: number
  pageSize?: number
  sender?: string
  addressee?: string
  text?: string
}

export async function getAllMessages(params: GetAllMessagesParams = {}): Promise<PaginatedResponse<AllMessagePacketDto>> {
  const { data } = await http.get<PaginatedResponse<AllMessagePacketDto>>('/api/v0/messages/all', { params })
  return data
}

export async function markMessageRead(id: number): Promise<InboxMessageDto> {
  const { data } = await http.put<InboxMessageDto>(`/api/v0/messages/${id}/read`)
  return data
}

export async function sendMessage(request: SendMessageRequest): Promise<InboxMessageDto> {
  const { data } = await http.post<InboxMessageDto>('/api/v0/messages/send', request)
  return data
}

export async function retryMessage(id: number): Promise<InboxMessageDto> {
  const { data } = await http.post<InboxMessageDto>(`/api/v0/messages/${id}/retry`)
  return data
}

export async function resetMessage(id: number): Promise<InboxMessageDto> {
  const { data } = await http.post<InboxMessageDto>(`/api/v0/messages/${id}/reset`)
  return data
}

export async function cancelMessage(id: number): Promise<InboxMessageDto> {
  const { data } = await http.post<InboxMessageDto>(`/api/v0/messages/${id}/cancel`)
  return data
}
