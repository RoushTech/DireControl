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
