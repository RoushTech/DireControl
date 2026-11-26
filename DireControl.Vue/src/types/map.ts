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
  /** Highest zoom the provider actually serves tiles for (default 19) */
  maxZoom?: number
  /** Extra class on the tile container — used to render a light basemap dark via CSS */
  className?: string
}
