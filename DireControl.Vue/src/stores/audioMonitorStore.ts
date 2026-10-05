import { defineStore } from 'pinia'
import { ref, watch } from 'vue'
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
  type ISubscription,
} from '@microsoft/signalr'

export type AudioMonitorState = 'idle' | 'connecting' | 'playing' | 'error'

/** Wire format of the PCM frames, reported by the hub so it is never hardcoded. */
interface AudioStreamFormat {
  sampleRate: number
  frameSamples: number
  encoding: string
}

const VOLUME_STORAGE_KEY = 'direcontrol-audio-volume'

/**
 * Playback buffer depth to aim for after an underrun. Long enough to absorb
 * normal network jitter, short enough that listening still feels live.
 */
const TARGET_BUFFER_SEC = 0.18

/**
 * Buffer depth past which frames are dropped to catch back up. Transmit
 * monitoring needs a far looser cap: a keyup is rendered in one go and arrives
 * faster than real time, so a tight cap would discard the middle of our own
 * packet rather than play it.
 */
const MAX_BUFFER_RX_SEC = 0.6
const MAX_BUFFER_TX_SEC = 2.5

/**
 * How many discarded frames within SKIP_WINDOW_MS count as audibly skipping.
 * Isolated drops are routine — one happens whenever playback re-primes, and
 * transmit bursts cause them by design — so a single drop is not worth telling
 * anyone about. A run of them inside a few seconds is the audible kind.
 */
const SKIP_THRESHOLD = 3
const SKIP_WINDOW_MS = 5000

/** Quiet period after which the skipping warning clears itself. */
const SKIP_CLEAR_MS = 5000

/**
 * Live "listen to the radio" audio, available from anywhere in the app.
 *
 * Audio rides its own connection to /hubs/audio rather than the shared packet
 * hub, so a continuous 16 kB/s frame stream can never delay packet or alert
 * events. Playback schedules each frame as an AudioBufferSourceNode on a
 * running cursor — no AudioWorklet asset to load, and drift is corrected by
 * re-priming on underrun and dropping on overrun.
 */
export const useAudioMonitorStore = defineStore('audioMonitor', () => {
  const state = ref<AudioMonitorState>('idle')
  /** Radio currently being listened to, or null when stopped. */
  const radioId = ref<string | null>(null)
  /** Peak sample level of the most recent frame, 0..1 — a listening-side meter. */
  const level = ref(0)
  /**
   * True while audio is dropping out often enough to actually hear. Isolated
   * drops are normal and deliberately not surfaced.
   */
  const audioSkipping = ref(false)
  const errorMessage = ref<string | null>(null)

  /**
   * Off by default: raw unsquelched audio is the point for debugging a radio's
   * audio path, where you need to hear hiss, hum, and signals too weak to decode.
   */
  const squelchGated = ref(false)
  const includeTx = ref(false)
  const muted = ref(false)
  const volume = ref(readStoredVolume())

  let connection: HubConnection | null = null
  let subscription: ISubscription<string> | null = null
  let ctx: AudioContext | null = null
  let gainNode: GainNode | null = null
  let nextStartAt = 0
  let format: AudioStreamFormat = { sampleRate: 8000, frameSamples: 512, encoding: 'pcm_s16le' }
  /** Frames scheduled but not yet played, so a re-prime can cancel them. */
  const scheduled = new Set<AudioBufferSourceNode>()

  let dropWindowStart = 0
  let dropsInWindow = 0
  let skipClearTimer: ReturnType<typeof setTimeout> | null = null

  /**
   * Records a discarded frame and decides whether it amounts to audible
   * skipping — a sustained run inside a short window, not one lost chunk.
   */
  function noteDroppedFrame() {
    const now = Date.now()
    if (now - dropWindowStart > SKIP_WINDOW_MS) {
      dropWindowStart = now
      dropsInWindow = 0
    }

    if (++dropsInWindow < SKIP_THRESHOLD) return

    audioSkipping.value = true
    if (skipClearTimer) clearTimeout(skipClearTimer)
    skipClearTimer = setTimeout(() => {
      audioSkipping.value = false
      skipClearTimer = null
    }, SKIP_CLEAR_MS)
  }

  function resetSkipTracking() {
    if (skipClearTimer) clearTimeout(skipClearTimer)
    skipClearTimer = null
    dropWindowStart = 0
    dropsInWindow = 0
    audioSkipping.value = false
  }

  function readStoredVolume(): number {
    try {
      const stored = localStorage.getItem(VOLUME_STORAGE_KEY)
      if (stored === null) return 0.8
      return Math.min(1, Math.max(0, Number.parseFloat(stored)))
    } catch {
      return 0.8
    }
  }

  function applyGain() {
    if (gainNode) gainNode.gain.value = muted.value ? 0 : volume.value
  }

  watch([volume, muted], () => {
    applyGain()
    try {
      localStorage.setItem(VOLUME_STORAGE_KEY, String(volume.value))
    } catch {
      /* private window or blocked site data — volume just won't persist */
    }
  })

  // Stream parameters are fixed for the life of a stream, so changing either
  // one re-subscribes. The audio context and connection stay up, making the
  // swap inaudible apart from a brief re-prime.
  watch([squelchGated, includeTx], () => {
    if (state.value === 'playing' && radioId.value) void subscribeToStream(radioId.value)
  })

  function ensureConnection(): HubConnection {
    if (connection) return connection
    connection = new HubConnectionBuilder()
      .withUrl('/hubs/audio')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.onreconnected(() => {
      // The old stream died with the old transport. Only re-subscribe if the
      // audio context is still alive — otherwise frames would stream from the
      // server forever with nothing to play them.
      if (ctx && radioId.value) void subscribeToStream(radioId.value)
    })
    connection.onclose(() => {
      if (state.value === 'idle') return
      // Automatic reconnect has given up. Release the output device rather than
      // leaving a silent context holding it until the user next interacts.
      void teardownAudio()
      state.value = 'error'
      errorMessage.value = 'Audio connection lost.'
    })
    return connection
  }

  /** base64 (how SignalR's JSON protocol carries byte[]) to 16-bit samples. */
  function decodeFrame(base64: string): Int16Array {
    const raw = atob(base64)
    const bytes = new Uint8Array(raw.length)
    for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i)
    return new Int16Array(bytes.buffer, 0, bytes.length >> 1)
  }

  function playFrame(pcm: Int16Array) {
    if (!ctx || !gainNode) return

    const buffer = ctx.createBuffer(1, pcm.length, format.sampleRate)
    const channel = buffer.getChannelData(0)
    let peak = 0
    for (let i = 0; i < pcm.length; i++) {
      const sample = (pcm[i] ?? 0) / 32768
      channel[i] = sample
      const magnitude = Math.abs(sample)
      if (magnitude > peak) peak = magnitude
    }
    level.value = peak

    const now = ctx.currentTime
    const maxBuffer = includeTx.value ? MAX_BUFFER_TX_SEC : MAX_BUFFER_RX_SEC

    if (nextStartAt < now + 0.01) {
      // First frame, or the queue ran dry — re-prime rather than scheduling in
      // the past, which the browser would play immediately and clip.
      nextStartAt = now + TARGET_BUFFER_SEC
    } else if (nextStartAt > now + maxBuffer) {
      // Frames are arriving faster than real time. Dropping without advancing
      // the cursor lets the already-scheduled audio drain and the depth shrink.
      noteDroppedFrame()
      return
    }

    const source = ctx.createBufferSource()
    source.buffer = buffer
    source.connect(gainNode)
    source.onended = () => {
      scheduled.delete(source)
    }
    source.start(nextStartAt)
    scheduled.add(source)
    nextStartAt += buffer.duration
  }

  /**
   * Cancels audio that is queued but not yet heard.  Without this, restarting a
   * stream leaves up to a buffer's worth of old frames scheduled, which play on
   * top of the new ones.
   */
  function flushScheduled() {
    for (const source of scheduled) {
      source.onended = null
      source.stop()
      source.disconnect()
    }
    scheduled.clear()
    nextStartAt = 0
  }

  async function subscribeToStream(id: string) {
    const conn = ensureConnection()
    subscription?.dispose()
    subscription = null
    flushScheduled()

    subscription = conn
      .stream<string>('Listen', id, squelchGated.value, includeTx.value)
      .subscribe({
        next: (frame) => playFrame(decodeFrame(frame)),
        error: (err: unknown) => {
          state.value = 'error'
          errorMessage.value = err instanceof Error ? err.message : 'Audio stream failed.'
        },
        complete: () => {
          if (state.value === 'playing') void stop()
        },
      })
  }

  /**
   * Starts listening to a radio. Must be called from a user gesture — browsers
   * refuse to start an AudioContext without one.
   */
  async function listen(id: string) {
    if (state.value !== 'idle') await stop()

    radioId.value = id
    state.value = 'connecting'
    errorMessage.value = null
    resetSkipTracking()

    try {
      ctx = new AudioContext()
      await ctx.resume()
      gainNode = ctx.createGain()
      applyGain()
      gainNode.connect(ctx.destination)

      const conn = ensureConnection()
      if (conn.state === HubConnectionState.Disconnected) await conn.start()
      format = await conn.invoke<AudioStreamFormat>('Format')

      await subscribeToStream(id)
      state.value = 'playing'
    } catch (err: unknown) {
      await teardownAudio()
      radioId.value = null
      state.value = 'error'
      errorMessage.value = err instanceof Error ? err.message : 'Could not start audio.'
    }
  }

  async function teardownAudio() {
    subscription?.dispose()
    subscription = null
    flushScheduled()
    gainNode?.disconnect()
    gainNode = null
    if (ctx) {
      // Release the output device rather than leaving a silent context running.
      try {
        await ctx.close()
      } catch {
        /* already closed */
      }
      ctx = null
    }
    nextStartAt = 0
    level.value = 0
    // Also cancels the pending clear timer, so it cannot fire after stopping.
    resetSkipTracking()
  }

  async function stop() {
    await teardownAudio()
    radioId.value = null
    state.value = 'idle'
    errorMessage.value = null
  }

  async function toggle(id: string) {
    if (state.value !== 'idle' && radioId.value === id) await stop()
    else await listen(id)
  }

  return {
    state,
    radioId,
    level,
    audioSkipping,
    errorMessage,
    squelchGated,
    includeTx,
    muted,
    volume,
    listen,
    stop,
    toggle,
  }
})
