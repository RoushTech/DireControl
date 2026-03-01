import http from './axios'

export interface AboutDto {
  version: string
}

export async function getAbout(): Promise<AboutDto> {
  const { data } = await http.get<AboutDto>('/api/v0/about')
  return data
}
