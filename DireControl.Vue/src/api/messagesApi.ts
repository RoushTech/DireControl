import http from './axios'
import type {
  AllMessagePacketDto,
  InboxMessageDto,
  SendMessageRequest,
} from '@/types/message'

export async function getInboxMessages(): Promise<InboxMessageDto[]> {
  const { data } = await http.get<InboxMessageDto[]>('/api/v0/messages/inbox')
  return data
}

export async function getAllMessages(): Promise<AllMessagePacketDto[]> {
  const { data } = await http.get<AllMessagePacketDto[]>('/api/v0/messages/all')
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
