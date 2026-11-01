import http from './axios'
import type {
  AllMessagePacketDto,
  InboxMessageDto,
  MessageCopyDto,
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

export async function getAllMessages(
  params: GetAllMessagesParams = {},
): Promise<PaginatedResponse<AllMessagePacketDto>> {
  const { data } = await http.get<PaginatedResponse<AllMessagePacketDto>>('/api/v0/messages/all', {
    params,
  })
  return data
}

/** Every received copy of an inbound message, oldest first, with its route. */
export async function getMessageCopies(id: number): Promise<MessageCopyDto[]> {
  const { data } = await http.get<MessageCopyDto[]>(`/api/v0/messages/${id}/copies`)
  return data
}

export async function markMessageRead(id: number): Promise<InboxMessageDto> {
  const { data } = await http.put<InboxMessageDto>(`/api/v0/messages/${id}/read`)
  return data
}

/** Sends a message; long text is split server-side, so every part sent comes back. */
export async function sendMessage(request: SendMessageRequest): Promise<InboxMessageDto[]> {
  const { data } = await http.post<InboxMessageDto[]>('/api/v0/messages/send', request)
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
