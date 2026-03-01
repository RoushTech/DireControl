import axios from 'axios'
import type {
  AllMessagePacketDto,
  InboxMessageDto,
  SendMessageRequest,
} from '@/types/message'

const http = axios.create({
  baseURL: '/',
})

export async function getInboxMessages(): Promise<InboxMessageDto[]> {
  const { data } = await http.get<InboxMessageDto[]>('/api/messages/inbox')
  return data
}

export async function getAllMessages(): Promise<AllMessagePacketDto[]> {
  const { data } = await http.get<AllMessagePacketDto[]>('/api/messages/all')
  return data
}

export async function markMessageRead(id: number): Promise<InboxMessageDto> {
  const { data } = await http.put<InboxMessageDto>(`/api/messages/${id}/read`)
  return data
}

export async function sendMessage(request: SendMessageRequest): Promise<InboxMessageDto> {
  const { data } = await http.post<InboxMessageDto>('/api/messages/send', request)
  return data
}
