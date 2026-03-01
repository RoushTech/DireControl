import axios from 'axios'

const http = axios.create({
  baseURL: 'http://localhost:5010',
})

export interface StatusDto {
  direwolfConnected: boolean
  apiOnline: boolean
}

export async function getStatus(): Promise<StatusDto> {
  const { data } = await http.get<StatusDto>('/api/status')
  return data
}
