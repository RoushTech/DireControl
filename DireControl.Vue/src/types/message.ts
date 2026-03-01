export interface InboxMessageDto {
  id: number
  fromCallsign: string
  toCallsign: string
  body: string
  messageId: string
  receivedAt: string
  isRead: boolean
  ackSent: boolean
  replySent: boolean
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
}

export interface MessageAckDto {
  id: number
  messageId: string
}
