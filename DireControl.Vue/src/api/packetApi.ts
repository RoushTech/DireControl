import http from './axios'

export interface PacketSettingsPayload {
  connectedModeInboundEnabled: boolean
  connectedModeMaxSessions: number
  connectedModeDefaultPaclen: number
  connectedModeWindowSize: number
  connectedModeT1Seconds: number
  connectedModeRetries: number
  connectedModePreferMod128: boolean
  pmsEnabled: boolean
  pmsSsid: number
  pmsBannerText: string
  pmsRetentionDays: number
  agwpeServerEnabled: boolean
  agwpeServerPort: number
  agwpeServerBindAddress: string
  terminalTranscriptRetentionDays: number
}

/** Updates the connected-mode / PMS / AGWPE packet settings. */
export async function updatePacketSettings(payload: PacketSettingsPayload): Promise<void> {
  await http.put('/api/v0/settings/packet', payload)
}
