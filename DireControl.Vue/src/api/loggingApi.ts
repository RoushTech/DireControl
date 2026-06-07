import http from './axios'

export interface LogLevelDto {
  category: string
  level: string
}

export interface LogLevelsResponse {
  overrides: LogLevelDto[]
  availableLevels: string[]
  commonCategories: string[]
}

export async function getLogLevels(): Promise<LogLevelsResponse> {
  const { data } = await http.get<LogLevelsResponse>('/api/v0/logging/levels')
  return data
}

/** Pass `level: null` to clear the override (inherit appsettings). */
export async function setLogLevel(category: string, level: string | null): Promise<void> {
  await http.put('/api/v0/logging/levels', { category, level })
}
