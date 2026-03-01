export interface TileProviderConfig {
  name: string
  url: string
  attribution: string
  theme: 'light' | 'dark'
  group: 'light' | 'dark' | 'satellite' | 'specialist'
  /** Provider requires a user-supplied API key before it can be used */
  requiresApiKey?: boolean
  /** localStorage key used to retrieve the API key for this provider */
  apiKeyParam?: string
}
