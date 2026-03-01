import http from './axios'

export interface CoverageGridSquareDto {
  gridSquare: string
  lat: number
  lon: number
  packetCount: number
}

export interface PacketPositionDto {
  latitude: number
  longitude: number
}

export async function getCoverageGridSquares(): Promise<CoverageGridSquareDto[]> {
  const { data } = await http.get<CoverageGridSquareDto[]>('/api/v0/analysis/coverage')
  return data
}

export async function getPacketPositions(): Promise<PacketPositionDto[]> {
  const { data } = await http.get<PacketPositionDto[]>('/api/v0/packets/positions')
  return data
}
