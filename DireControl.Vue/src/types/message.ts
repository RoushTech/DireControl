import type { PacketSource } from './packet'
import type { HeardVia } from './station'

export enum RetryState {
  Pending = 0,
  Retrying = 1,
  Acknowledged = 2,
  Failed = 3,
  Cancelled = 4,
}

export interface InboxMessageDto {
  id: number
  fromCallsign: string
  toCallsign: string
  body: string
  messageId: string
  pathUsed: string | null
  receivedAt: string
  /** Inbound: copies heard; above 1 means the sender retransmitted. */
  receiveCount: number
  lastReceivedAt: string | null
  /** Inbound: how the first copy reached us; null when its packet is unknown. */
  source: PacketSource | null
  heardVia: HeardVia | null
  /** Inbound: via-path of the first copy, tocall excluded. */
  path: string | null
  isRead: boolean
  ackSent: boolean
  replySent: boolean
  retryCount: number
  maxRetries: number
  nextRetryAt: string | null
  retryState: RetryState
  lastSentAt: string | null
}

export interface AllMessagePacketDto {
  packetId: number
  fromCallsign: string
  toCallsign: string
  body: string
  messageId: string | null
  receivedAt: string
  rawPacket: string
}

export interface PaginatedResponse<T> {
  page: number
  pageSize: number
  totalCount: number
  items: T[]
}

export interface SendMessageRequest {
  toCallsign: string
  body: string
  path?: string
}

export interface MessageAckDto {
  id: number
  messageId: string
}

export interface MessageRetriedDto {
  id: number
  retryCount: number
  maxRetries: number
  nextRetryAt: string | null
  lastSentAt: string | null
}

export interface MessageAcknowledgedDto {
  id: number
  messageId: string
}

export interface MessageFailedDto {
  id: number
  toCallsign: string
  retryCount: number
}

/** One received copy of an inbound message; retries can take different routes. */
export interface MessageCopyDto {
  packetId: number
  receivedAt: string
  source: PacketSource
  heardVia: HeardVia
  path: string
  hopCount: number
}
