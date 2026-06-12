import type { TileProviderConfig } from '@/types/map'

export const TILE_PROVIDERS: Record<string, TileProviderConfig> = {
  // Light
  osm: {
    name: 'OpenStreetMap',
    url: 'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
    attribution:
      '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    theme: 'light',
    group: 'light',
  },
  stadiaAlidadeSmooth: {
    name: 'Stadia Alidade Smooth',
    url: 'https://tiles.stadiamaps.com/tiles/alidade_smooth/{z}/{x}/{y}{r}.png',
    attribution:
      '&copy; <a href="https://stadiamaps.com/">Stadia Maps</a> &copy; <a href="https://openmaptiles.org/">OpenMapTiles</a> &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    theme: 'light',
    group: 'light',
  },
  cartoLight: {
    name: 'Carto Light',
    url: 'https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png',
    attribution:
      '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors &copy; <a href="https://carto.com/">CARTO</a>',
    theme: 'light',
    group: 'light',
  },
  topo: {
    name: 'OpenTopoMap',
    url: 'https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png',
    attribution:
      '&copy; <a href="https://opentopomap.org">OpenTopoMap</a> (<a href="https://creativecommons.org/licenses/by-sa/3.0/">CC-BY-SA</a>)',
    theme: 'light',
    group: 'light',
  },
  esriTopo: {
    name: 'Esri World Topo',
    url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}',
    attribution:
      'Esri, HERE, Garmin, Intermap, &copy; OpenStreetMap contributors',
    theme: 'light',
    group: 'light',
  },
  // Dark
  cartoDark: {
    name: 'Carto Dark Matter',
    url: 'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',
    attribution:
      '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors &copy; <a href="https://carto.com/">CARTO</a>',
    theme: 'dark',
    group: 'dark',
  },
  stadiaAlidadeDark: {
    name: 'Stadia Alidade Dark',
    url: 'https://tiles.stadiamaps.com/tiles/alidade_smooth_dark/{z}/{x}/{y}{r}.png',
    attribution:
      '&copy; <a href="https://stadiamaps.com/">Stadia Maps</a> &copy; <a href="https://openmaptiles.org/">OpenMapTiles</a> &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    theme: 'dark',
    group: 'dark',
  },
  jawgDark: {
    name: 'Jawg Dark',
    url: 'https://tile.jawg.io/jawg-dark/{z}/{x}/{y}{r}.png?access-token={apiKey}',
    attribution:
      '&copy; <a href="https://www.jawg.io">Jawg Maps</a> &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    theme: 'dark',
    group: 'dark',
    requiresApiKey: true,
    apiKeyParam: 'jawg',
  },
  // Satellite
  satellite: {
    name: 'Esri World Imagery',
    url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
    attribution:
      '&copy; Esri &mdash; Source: Esri, Maxar, Earthstar Geographics',
    theme: 'dark',
    group: 'satellite',
  },
  // Specialist
  openRailwayMap: {
    name: 'OpenRailwayMap',
    url: 'https://{s}.tiles.openrailwaymap.org/standard/{z}/{x}/{y}.png',
    attribution:
      '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors, Style: &copy; <a href="https://www.openrailwaymap.org/">OpenRailwayMap</a>',
    theme: 'light',
    group: 'specialist',
  },
}
