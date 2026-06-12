import { ref, shallowRef, watch, type Ref, type ShallowRef } from 'vue'
import L from 'leaflet'
import { getWeatherManifest, getWeatherStatus, type WeatherManifest } from '@/api/weatherApi'

// Weather overlay state
export interface WeatherLayerStatus {
  available: boolean
  frameCount?: number
  lastUpdated?: string
  reason?: string
}
export interface WeatherStatus {
  radar: WeatherLayerStatus
  wind: WeatherLayerStatus
  lightning: WeatherLayerStatus
}

export function useWeatherOverlays(
  map: ShallowRef<L.Map | undefined>,
  prefs: {
    showRadar: Ref<boolean>
    showWind: Ref<boolean>
    showLightning: Ref<boolean>
    radarOpacity: Ref<number>
    windOpacity: Ref<number>
    lightningOpacity: Ref<number>
  },
) {
  const { showRadar, showWind, showLightning, radarOpacity, windOpacity, lightningOpacity } = prefs

  let radarManifest: WeatherManifest | null = null
  const weatherStatus = shallowRef<WeatherStatus | null>(null)
  let radarFrameLayers: L.TileLayer[] = []
  let radarFrameMeta: { time: number }[] = []
  let radarFrameReady: boolean[] = []
  let currentRadarFrame = 0
  let radarAnimTimeout: ReturnType<typeof setTimeout> | null = null
  let radarRefreshInterval: ReturnType<typeof setInterval> | null = null
  const radarPlaying = ref(false)
  const radarTimestamp = ref('')
  const radarFrameCount = ref(0)
  const radarCurrentIdx = ref(0)
  const radarLoading = ref(false)
  const radarFrameInterval = ref(500)  // ms between frames
  const radarControlsVisible = ref(false)
  const windControlsVisible = ref(false)
  const lightningControlsVisible = ref(false)
  let radarControlsHideTimer: ReturnType<typeof setTimeout> | null = null
  let windControlsHideTimer: ReturnType<typeof setTimeout> | null = null
  let lightningControlsHideTimer: ReturnType<typeof setTimeout> | null = null
  let windLayer: L.TileLayer | null = null
  let lightningLayer: L.TileLayer | null = null
  let lightningRefreshInterval: ReturnType<typeof setInterval> | null = null

  async function fetchWeatherStatus(): Promise<WeatherStatus | null> {
    try {
      weatherStatus.value = await getWeatherStatus()
      return weatherStatus.value
    } catch (err) {
      console.error('Failed to fetch weather status:', err)
      return null
    }
  }

  // Weather Overlays

  function ensureWeatherPane() {
    if (!map.value) return
    if (!map.value.getPane('weatherPane')) {
      const pane = map.value.createPane('weatherPane')
      pane.style.zIndex = '450'  // above overlayPane (400), below markerPane (600)
      pane.style.pointerEvents = 'none'
    }
  }

  // RainViewer

  async function fetchRadarManifest(): Promise<WeatherManifest | null> {
    try {
      return await getWeatherManifest()
    } catch (err) {
      console.error('Failed to fetch radar manifest:', err)
      return null
    }
  }

  function buildRadarLayer(framePath: string, manifest: WeatherManifest): L.TileLayer {
    const stripped = framePath.startsWith('/') ? framePath.slice(1) : framePath
    const zoomOffset = manifest.tileSize === 512 ? -1 : 0
    return L.tileLayer(
      `/api/weather/radar/tile/{z}/{x}/{y}/${stripped}`,
      { opacity: 0, tileSize: manifest.tileSize, zoomOffset, zIndex: 10, pane: 'weatherPane', maxNativeZoom: manifest.maxNativeZoom, maxZoom: 19 },
    )
  }

  function formatRadarTime(unixSeconds: number): string {
    return new Date(unixSeconds * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) + ' local'
  }

  function showRadarFrame(idx: number) {
    if (!map.value || radarFrameLayers.length === 0) return
    const clampedIdx = Math.max(0, Math.min(idx, radarFrameLayers.length - 1))
    radarFrameLayers.forEach((layer, i) => {
      if (i === clampedIdx) {
        if (!map.value!.hasLayer(layer)) layer.addTo(map.value!)
        layer.setOpacity(radarOpacity.value)
      } else if (map.value!.hasLayer(layer)) {
        // Fade to transparent rather than removing — prevents the blank-frame flicker
        // caused by Leaflet's per-tile CSS fade-in when re-adding a layer.
        layer.setOpacity(0)
      }
    })
    currentRadarFrame = clampedIdx
    radarCurrentIdx.value = clampedIdx
    if (radarFrameMeta[clampedIdx]) {
      radarTimestamp.value = formatRadarTime(radarFrameMeta[clampedIdx]!.time)
    }
  }

  async function playRadar() {
    if (radarPlaying.value) return
    radarPlaying.value = true
    keepRadarControlsVisible()

    const advance = async () => {
      if (!radarPlaying.value || radarFrameLayers.length === 0) return
      const next = (currentRadarFrame + 1) % radarFrameLayers.length
      const layer = radarFrameLayers[next]!
      // Add the next frame to the map invisibly so its tiles start loading
      if (!map.value!.hasLayer(layer)) {
        layer.addTo(map.value!)
        layer.setOpacity(0)
      }
      // Wait for all visible tiles on that frame to finish loading (up to 3 s)
      if (!radarFrameReady[next]) {
        await new Promise<void>(resolve => {
          const done = () => {
            radarFrameReady[next] = true
            clearTimeout(loadTimeout)
            // Wait for Leaflet's per-tile opacity fade-in (200 ms) to complete before
            // revealing the frame, otherwise tiles appear partially transparent on first play.
            setTimeout(resolve, 250)
          }
          layer.once('load', done)
          const loadTimeout = setTimeout(() => { layer.off('load', done); resolve() }, 3000)
        })
      }
      if (!radarPlaying.value) return
      showRadarFrame(next)
      radarAnimTimeout = setTimeout(advance, radarFrameInterval.value)
    }

    radarAnimTimeout = setTimeout(advance, radarFrameInterval.value)
  }

  function pauseRadar() {
    radarPlaying.value = false
    if (radarAnimTimeout) {
      clearTimeout(radarAnimTimeout)
      radarAnimTimeout = null
    }
  }

  function stepRadarFrame(delta: -1 | 1) {
    pauseRadar()
    const next = (currentRadarFrame + delta + radarFrameLayers.length) % radarFrameLayers.length
    showRadarFrame(next)
    keepRadarControlsVisible()
  }

  // Weather controls auto-hide

  function keepRadarControlsVisible() {
    if (!showRadar.value) return
    radarControlsVisible.value = true
    if (radarControlsHideTimer) clearTimeout(radarControlsHideTimer)
    radarControlsHideTimer = setTimeout(() => { radarControlsVisible.value = false }, 5000)
  }

  function keepWindControlsVisible() {
    if (!showWind.value) return
    windControlsVisible.value = true
    if (windControlsHideTimer) clearTimeout(windControlsHideTimer)
    windControlsHideTimer = setTimeout(() => { windControlsVisible.value = false }, 5000)
  }

  function keepLightningControlsVisible() {
    if (!showLightning.value) return
    lightningControlsVisible.value = true
    if (lightningControlsHideTimer) clearTimeout(lightningControlsHideTimer)
    lightningControlsHideTimer = setTimeout(() => { lightningControlsVisible.value = false }, 5000)
  }

  function clearRadarLayers() {
    pauseRadar()
    for (const layer of radarFrameLayers) {
      layer.remove()
    }
    radarFrameLayers = []
    radarFrameMeta = []
    radarFrameReady = []
    radarFrameCount.value = 0
    radarTimestamp.value = ''
    radarCurrentIdx.value = 0
  }

  async function enableRadar() {
    if (!map.value) return
    radarLoading.value = true
    ensureWeatherPane()
    try {
      radarManifest = await fetchRadarManifest()
      if (!radarManifest) { showRadar.value = false; return }
      const allFrames = [
        ...radarManifest.radar.past,
        ...(radarManifest.radar.nowcast ?? []),
      ]
      radarFrameMeta = allFrames.map(f => ({ time: f.time }))
      radarFrameReady = Array.from({ length: allFrames.length }, () => false)
      radarFrameLayers = allFrames.map(f => buildRadarLayer(f.path, radarManifest!))
      radarFrameCount.value = radarFrameLayers.length
      // Start on the last historical frame so we see the most recent real data first
      showRadarFrame(radarManifest.radar.past.length - 1)
      keepRadarControlsVisible()
      radarRefreshInterval = setInterval(async () => {
        if (!showRadar.value) return
        const wasPlaying = radarPlaying.value
        pauseRadar()
        clearRadarLayers()
        radarManifest = await fetchRadarManifest()
        if (!radarManifest) return
        const refreshedFrames = [
          ...radarManifest.radar.past,
          ...(radarManifest.radar.nowcast ?? []),
        ]
        radarFrameMeta = refreshedFrames.map(f => ({ time: f.time }))
        radarFrameReady = Array.from({ length: refreshedFrames.length }, () => false)
        radarFrameLayers = refreshedFrames.map(f => buildRadarLayer(f.path, radarManifest!))
        radarFrameCount.value = radarFrameLayers.length
        showRadarFrame(radarManifest.radar.past.length - 1)
        if (wasPlaying) playRadar()
      }, 5 * 60 * 1000)
    } finally {
      radarLoading.value = false
    }
  }

  function disableRadar() {
    pauseRadar()
    clearRadarLayers()
    if (radarRefreshInterval) {
      clearInterval(radarRefreshInterval)
      radarRefreshInterval = null
    }
    radarManifest = null
    radarControlsVisible.value = false
    if (radarControlsHideTimer) { clearTimeout(radarControlsHideTimer); radarControlsHideTimer = null }
  }

  async function toggleRadar() {
    showRadar.value = !showRadar.value
    if (showRadar.value) {
      await enableRadar()
    } else {
      disableRadar()
    }
  }

  // Wind (OpenWeatherMap)

  function enableWind() {
    if (!map.value) return
    disableWind()
    ensureWeatherPane()
    windLayer = L.tileLayer(
      '/api/weather/wind/tile/{z}/{x}/{y}',
      { opacity: windOpacity.value, zIndex: 11, pane: 'weatherPane', maxNativeZoom: 18, maxZoom: 19 },
    ).addTo(map.value)
    keepWindControlsVisible()
  }

  function disableWind() {
    if (windLayer) {
      windLayer.remove()
      windLayer = null
    }
    windControlsVisible.value = false
    if (windControlsHideTimer) { clearTimeout(windControlsHideTimer); windControlsHideTimer = null }
  }

  async function toggleWind() {
    showWind.value = !showWind.value
    if (showWind.value && weatherStatus.value?.wind.available) {
      enableWind()
    } else {
      showWind.value = false
      disableWind()
    }
  }

  // Lightning (Tomorrow.io)

  function buildLightningLayer(): L.TileLayer {
    return L.tileLayer(
      '/api/weather/lightning/tile/{z}/{x}/{y}',
      { opacity: lightningOpacity.value, zIndex: 12, pane: 'weatherPane', maxNativeZoom: 6, maxZoom: 19 },
    )
  }

  function enableLightning() {
    if (!map.value) return
    disableLightning()
    ensureWeatherPane()
    lightningLayer = buildLightningLayer().addTo(map.value)
    keepLightningControlsVisible()
    // Refresh every 5 minutes so Leaflet fetches fresh tiles from the backend cache
    lightningRefreshInterval = setInterval(() => {
      if (!showLightning.value || !map.value) return
      lightningLayer?.remove()
      lightningLayer = buildLightningLayer().addTo(map.value!)
    }, 5 * 60 * 1000)
  }

  function disableLightning() {
    if (lightningLayer) {
      lightningLayer.remove()
      lightningLayer = null
    }
    if (lightningRefreshInterval) {
      clearInterval(lightningRefreshInterval)
      lightningRefreshInterval = null
    }
    lightningControlsVisible.value = false
    if (lightningControlsHideTimer) { clearTimeout(lightningControlsHideTimer); lightningControlsHideTimer = null }
  }

  async function toggleLightning() {
    showLightning.value = !showLightning.value
    if (showLightning.value && weatherStatus.value?.lightning.available) {
      enableLightning()
    } else {
      showLightning.value = false
      disableLightning()
    }
  }

  // Watch: weather overlay opacity — apply immediately to live layers
  watch(radarOpacity, (v) => radarFrameLayers[currentRadarFrame]?.setOpacity(v))
  watch(windOpacity, (v) => windLayer?.setOpacity(v))
  watch(lightningOpacity, (v) => lightningLayer?.setOpacity(v))

  function cleanup() {
    disableRadar()
    disableWind()
    disableLightning()
    if (radarControlsHideTimer) clearTimeout(radarControlsHideTimer)
    if (windControlsHideTimer) clearTimeout(windControlsHideTimer)
    if (lightningControlsHideTimer) clearTimeout(lightningControlsHideTimer)
  }

  return {
    weatherStatus,
    radarPlaying,
    radarTimestamp,
    radarFrameCount,
    radarCurrentIdx,
    radarLoading,
    radarFrameInterval,
    radarControlsVisible,
    windControlsVisible,
    lightningControlsVisible,
    fetchWeatherStatus,
    enableRadar,
    enableWind,
    enableLightning,
    toggleRadar,
    toggleWind,
    toggleLightning,
    playRadar,
    pauseRadar,
    stepRadarFrame,
    keepRadarControlsVisible,
    keepWindControlsVisible,
    keepLightningControlsVisible,
    cleanup,
  }
}
