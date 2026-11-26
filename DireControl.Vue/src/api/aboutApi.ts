import http from './axios'

export interface AboutDto {
  version: string
  /** Source commit of the build, when known. */
  commit: string | null
  serverTime: string
}

export async function getAbout(): Promise<AboutDto> {
  const { data } = await http.get<AboutDto>('/api/v0/about')
  return data
}
