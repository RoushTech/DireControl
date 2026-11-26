/**
 * The one worst-of health model. Pure: every input is passed in, nothing is
 * read from stores or the clock, so it can be unit-tested state by state
 * (see __tests__/healthModel.spec.ts) and the status strip, status panel,
 * banners and kiosk chip can never disagree.
 *
 * RF and APRS-IS are separate lanes on purpose: internet traffic flowing says
 * nothing about whether the radio hears anything.
 */
import { ModemStates, type ModemState } from '@/api/modemApi'
import { AprsIsStates, type AprsIsState } from '@/api/statusApi'

export type HealthLevel = 'ok' | 'warn' | 'err' | 'info' | 'off'
export type LaneKey = 'link' | 'rf' | 'tx' | 'is'

export interface LaneAction {
  label: string
  /** Router location to open. */
  to: string
}

export interface Lane {
  level: HealthLevel
  /** Short lane name shown in the strip: LIVE / RF / TX / IS. */
  key: string
  /** Value shown next to the key in the strip ("14 s", "Silent 23 min"). */
  value: string
  /** One-line explanation for the status panel. */
  detail: string
  /** True when the hub is down: the value is frozen as of the drop. */
  stale?: boolean
  action?: LaneAction
  /** TX lane only: a frame is on air right now. */
  onAir?: boolean
}

/** The subset of ModemStatusDto the model reads. */
export interface ModemHealthInput {
  state: ModemState
  errorMessage: string | null
  lastDecodedAt: string | null
  runningSince: string | null
  carrierDetected: boolean
  carrierSince: string | null
  preamblesSinceCarrier: number
  carrierStuck: boolean
  pttStuck: boolean
  transmitting: boolean
  csmaWaitingSince: string | null
  txQueueDepth: number
  txEnabled: boolean
  txError: string | null
}

export interface RadioHealthInput {
  radioId: string
  name: string
  /** Radio is configured to use the built-in sound modem (else an external TNC). */
  modemEnabled: boolean
  /** Radio is configured to transmit. */
  txConfigured: boolean
  /** Live modem status, or null when none has arrived. */
  modem: ModemHealthInput | null
}

export interface HealthInput {
  /** Epoch ms (server-corrected). */
  now: number
  apiOffline: boolean
  hubState: 'connecting' | 'connected' | 'disconnected' | 'resyncing'
  /** Epoch ms the hub dropped; null while connected or before the first connect. */
  hubDownSince: number | null
  /** The hub has connected at least once this page load. */
  hubEverConnected: boolean
  /** Radios have been fetched (distinguishes "none" from "not yet"). */
  radiosLoaded: boolean
  /** Active radios only. */
  radios: RadioHealthInput[]
  /** Our callsign; null until settings load. */
  callsign: string | null
  externalTncConnected: boolean
  /** Epoch ms of the last RF packet seen on the hub (external-TNC radios). */
  lastRfPacketAt: number | null
  aprsIs: {
    state: AprsIsState
    lastError: string | null
    serverName: string | null
    /** Epoch ms; null when nothing has arrived. */
    lastPacketAt: number | null
  } | null
  /** No RF decode for this long while the modem runs → RF silent. */
  rfSilentMs: number
}

export interface RadioHealth {
  radioId: string
  name: string
  rf: Lane
  tx: Lane
}

export interface Health {
  lanes: Record<LaneKey, Lane>
  worst: HealthLevel
  label: string
  /** Hub connected (or resyncing) — what is shown is current. */
  live: boolean
  /** No radio, or the callsign is still the N0CALL default. */
  setupNeeded: boolean
  /** Why nothing can be transmitted right now, or null when TX is possible. */
  txBlockedReason: string | null
  /** The same, with the link that fixes it. */
  txBlocked: TxBlock | null
  /** Per-radio lanes; the strip shows the worst of these. */
  radios: RadioHealth[]
}

/** Carrier held this long with no preamble is noise or open squelch, not a transmission. */
export const CARRIER_HELD_MS = 10_000

const RANK: Record<HealthLevel, number> = { err: 4, warn: 3, info: 2, off: 1, ok: 0 }
const LANE_ORDER: LaneKey[] = ['link', 'rf', 'tx', 'is']

export const isN0Call = (callsign: string | null) =>
  !!callsign && callsign.trim().toUpperCase().startsWith('N0CALL')

const ms = (iso: string | null) => (iso ? new Date(iso).getTime() : null)

/** Compact elapsed time: "14 s", "23 min", "2 h 5 m", "3 d". */
export function agoShort(since: number | null, now: number): string {
  if (since == null) return 'never'
  const s = Math.max(0, Math.round((now - since) / 1000))
  if (s < 60) return `${s} s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} min`
  const h = Math.floor(m / 60)
  if (h < 48) return `${h} h ${m % 60} m`
  return `${Math.floor(h / 24)} d`
}

/** Running duration: "6 s", "1 m 12 s". */
export function durationShort(msElapsed: number): string {
  const s = Math.max(0, Math.round(msElapsed / 1000))
  return s < 60 ? `${s} s` : `${Math.floor(s / 60)} m ${s % 60} s`
}

// For rolling radios up into one lane, "off" (receive-only, modem disabled)
// ranks below "ok": a working transmitter must not be hidden by an RX-only one.
const ROLLUP_RANK: Record<HealthLevel, number> = { err: 4, warn: 3, info: 2, ok: 1, off: 0 }

function worstOf(lanes: Lane[]): Lane | undefined {
  let worst: Lane | undefined
  for (const l of lanes) if (!worst || ROLLUP_RANK[l.level] > ROLLUP_RANK[worst.level]) worst = l
  return worst
}

/** Carrier held past CARRIER_HELD_MS with no preamble (or the backend flags it stuck). */
export function carrierHeldMs(m: ModemHealthInput | null, now: number): number | null {
  if (!m || m.state !== ModemStates.Running || !m.carrierDetected) return null
  const since = ms(m.carrierSince)
  if (since == null) return m.carrierStuck ? CARRIER_HELD_MS : null
  const held = now - since
  if (m.carrierStuck || (held > CARRIER_HELD_MS && m.preamblesSinceCarrier === 0)) return held
  return null
}

function rfLane(r: RadioHealthInput, x: HealthInput): Lane {
  const m = r.modem
  const name = r.name
  if (!r.modemEnabled) {
    // External TNC carries this radio's RF; all we can see is whether it is
    // connected and when an RF packet last arrived.
    if (!x.externalTncConnected)
      return {
        level: 'warn',
        key: 'RF',
        value: 'No TNC',
        detail: `${name}: external TNC not connected`,
        action: { label: 'External TNC', to: '/settings/rf' },
      }
    const last = x.lastRfPacketAt
    if (last != null && x.now - last > x.rfSilentMs)
      return {
        level: 'warn',
        key: 'RF',
        value: `Silent ${agoShort(last, x.now)}`,
        detail: `${name}: no RF packet from the TNC for ${agoShort(last, x.now)}`,
      }
    return {
      level: 'ok',
      key: 'RF',
      value: last != null ? agoShort(last, x.now) : 'TNC',
      detail: `${name}: external TNC connected`,
    }
  }
  if (!m || m.state === ModemStates.Unknown)
    return { level: 'off', key: 'RF', value: 'No data', detail: `${name}: no modem status yet` }
  if (m.state === ModemStates.Error) {
    if (m.pttStuck)
      return {
        level: 'err',
        key: 'RF',
        value: 'Blind (TX)',
        detail: `${name}: receiver blind while PTT is keyed`,
        action: { label: 'Open Radio', to: '/radio' },
      }
    return {
      level: 'err',
      key: 'RF',
      value: 'Modem error',
      detail: `${name}: ${m.errorMessage ?? 'modem stopped'}`,
      action: { label: 'Open Radio', to: '/radio' },
    }
  }
  if (m.state === ModemStates.Disabled)
    return { level: 'off', key: 'RF', value: 'Modem off', detail: `${name}: modem not running` }
  if (m.pttStuck)
    return {
      level: 'err',
      key: 'RF',
      value: 'Blind (TX)',
      detail: `${name}: receiver blind while PTT is keyed`,
    }
  const held = carrierHeldMs(m, x.now)
  if (held != null)
    return {
      level: 'warn',
      key: 'RF',
      value: `Carrier held ${durationShort(held)}`,
      detail: `${name}: carrier detect held with no preambles`,
      action: { label: 'Open Radio', to: '/radio' },
    }
  const lastDecode = ms(m.lastDecodedAt)
  const since = lastDecode ?? ms(m.runningSince)
  if (since != null && x.now - since > x.rfSilentMs)
    return {
      level: 'warn',
      key: 'RF',
      value: `Silent ${agoShort(since, x.now)}`,
      detail: lastDecode
        ? `${name}: no RF decode for ${agoShort(since, x.now)}`
        : `${name}: nothing decoded since the modem started ${agoShort(since, x.now)} ago`,
      action: { label: 'Open Radio', to: '/radio' },
    }
  return {
    level: 'ok',
    key: 'RF',
    value: lastDecode != null ? agoShort(lastDecode, x.now) : 'Listening',
    detail: lastDecode != null ? `${name}: decoding` : `${name}: listening, nothing decoded yet`,
  }
}

function txLane(r: RadioHealthInput, x: HealthInput): Lane {
  const m = r.modem
  const name = r.name
  if (!r.txConfigured)
    return { level: 'off', key: 'TX', value: 'None', detail: `${name}: receive only` }
  if (m?.pttStuck)
    return {
      level: 'err',
      key: 'TX',
      value: 'PTT stuck',
      detail: `${name}: unkey failed and the radio may still be transmitting`,
      action: { label: 'Open Radio', to: '/radio' },
    }
  if (r.modemEnabled) {
    if (!m || m.state === ModemStates.Error)
      return {
        level: m ? 'err' : 'off',
        key: 'TX',
        value: 'Unavailable',
        detail: m ? `${name}: no transmitter (modem error)` : `${name}: no modem status yet`,
      }
    if (m.state !== ModemStates.Running || !m.txEnabled)
      return { level: 'off', key: 'TX', value: 'Off', detail: `${name}: modem not transmitting` }
  } else if (!x.externalTncConnected) {
    return { level: 'err', key: 'TX', value: 'Unavailable', detail: `${name}: external TNC down` }
  }
  if (isN0Call(x.callsign))
    return {
      level: 'warn',
      key: 'TX',
      value: 'Blocked',
      detail: 'Transmit is blocked until the callsign is set',
      action: { label: 'Set callsign', to: '/settings/station' },
    }
  if (m?.transmitting)
    return { level: 'ok', key: 'TX', value: 'On air', detail: `${name}: transmitting`, onAir: true }
  const waiting = ms(m?.csmaWaitingSince ?? null)
  if (waiting != null)
    return {
      level: 'warn',
      key: 'TX',
      value: `Waiting ${durationShort(x.now - waiting)}`,
      detail: `${name}: waiting for a clear channel`,
    }
  if (m?.txError)
    return { level: 'warn', key: 'TX', value: 'TX error', detail: `${name}: ${m.txError}` }
  const depth = m?.txQueueDepth ?? 0
  return {
    level: 'ok',
    key: 'TX',
    value: depth ? `${depth} queued` : 'Idle',
    detail: `${name}: ready`,
  }
}

function isLane(x: HealthInput): Lane {
  const s = x.aprsIs
  if (!s || s.state === AprsIsStates.Unknown)
    return { level: 'off', key: 'IS', value: '—', detail: 'APRS-IS status not loaded' }
  switch (s.state) {
    case AprsIsStates.Disabled:
      return { level: 'off', key: 'IS', value: 'Off', detail: 'APRS-IS disabled' }
    case AprsIsStates.AuthFailed:
      return {
        level: 'err',
        key: 'IS',
        value: 'Auth failed',
        detail: s.lastError ? `Login rejected: ${s.lastError}` : 'APRS-IS login rejected',
        action: { label: 'Fix passcode', to: '/settings/aprsis' },
      }
    case AprsIsStates.Connecting:
      return { level: 'warn', key: 'IS', value: 'Connecting…', detail: 'Connecting to APRS-IS' }
    case AprsIsStates.Disconnected:
      return {
        level: 'warn',
        key: 'IS',
        value: 'Disconnected',
        detail: s.lastError ? `Disconnected: ${s.lastError}` : 'APRS-IS disconnected, retrying',
      }
    default:
      return {
        level: 'ok',
        key: 'IS',
        value: s.lastPacketAt != null ? agoShort(s.lastPacketAt, x.now) : 'Connected',
        detail: `Connected to ${s.serverName ?? 'APRS-IS'}`,
      }
  }
}

function linkLane(x: HealthInput): Lane {
  if (x.apiOffline)
    return {
      level: 'err',
      key: 'LIVE',
      value: 'API offline',
      detail: 'Backend unreachable: retrying',
    }
  switch (x.hubState) {
    case 'connected':
      return { level: 'ok', key: 'LIVE', value: 'Live', detail: 'Live feed connected' }
    case 'resyncing':
      return {
        level: 'warn',
        key: 'LIVE',
        value: 'Resyncing…',
        detail: 'Reconnected: refetching messages, alerts and radios',
      }
    default:
      if (!x.hubEverConnected)
        return { level: 'info', key: 'LIVE', value: 'Connecting', detail: 'Opening the live feed' }
      return {
        level: 'err',
        key: 'LIVE',
        value:
          x.hubDownSince != null ? `Offline ${durationShort(x.now - x.hubDownSince)}` : 'Offline',
        detail: 'Live feed lost: reconnecting',
      }
  }
}

/** Why nothing can go to air, or null. Mirrors the server-side checks. */
function txBlocked(x: HealthInput, radios: RadioHealth[]): TxBlock | null {
  if (!x.radiosLoaded) return null
  if (x.radios.length === 0)
    return {
      reason: 'No radio is configured. Add one in Setup.',
      fix: { label: 'Add a radio', to: '/settings/radios' },
    }
  if (!x.radios.some((r) => r.txConfigured))
    return {
      reason: 'No transmitter is configured. Enable TX on a radio with PTT.',
      fix: { label: 'Radio settings', to: '/settings/radios' },
    }
  if (isN0Call(x.callsign))
    return {
      reason: 'Set your callsign before transmitting.',
      fix: { label: 'Set callsign', to: '/settings/station' },
    }
  const usable = radios.filter((r) => r.tx.level !== 'off' && r.tx.level !== 'err')
  if (usable.length > 0) return null
  const openRadio = { label: 'Open Radio', to: '/radio' }
  if (radios.some((r) => r.tx.value === 'PTT stuck'))
    return { reason: 'PTT is stuck keyed. Force unkey on the Radio page first.', fix: openRadio }
  if (x.radios.some((r) => r.modem?.state === ModemStates.Error))
    return { reason: 'The modem is in error, so nothing can go to air.', fix: openRadio }
  return { reason: 'No transmitter is running.', fix: openRadio }
}

/** Why transmit is impossible, with the link that fixes it. */
export interface TxBlock {
  reason: string
  fix: LaneAction
}

export function deriveHealth(x: HealthInput): Health {
  const radios: RadioHealth[] = x.radios.map((r) => ({
    radioId: r.radioId,
    name: r.name,
    rf: rfLane(r, x),
    tx: txLane(r, x),
  }))

  const lanes: Record<LaneKey, Lane> = {
    link: linkLane(x),
    rf:
      radios.length === 0
        ? x.radiosLoaded
          ? {
              level: 'info',
              key: 'RF',
              value: 'No radio',
              detail: 'No radio configured',
              action: { label: 'Add a radio', to: '/setup' },
            }
          : { level: 'off', key: 'RF', value: '—', detail: 'Loading radios' }
        : worstOf(radios.map((r) => r.rf))!,
    tx:
      radios.length === 0
        ? { level: 'off', key: 'TX', value: 'None', detail: 'No transmitter configured' }
        : // On air beats idle when several radios are fine.
          (worstOf(radios.map((r) => r.tx)) ?? radios[0]!.tx),
    is: isLane(x),
  }
  if (lanes.tx.level === 'ok') {
    const onAir = radios.find((r) => r.tx.onAir)
    if (onAir) lanes.tx = onAir.tx
  }

  const live = !x.apiOffline && (x.hubState === 'connected' || x.hubState === 'resyncing')
  // Nothing shown is current while the hub is down: grey it, don't lie.
  if (!live && (x.hubEverConnected || x.apiOffline))
    for (const k of ['rf', 'is', 'tx'] as const)
      lanes[k] = { ...lanes[k], level: lanes[k].level === 'err' ? 'err' : 'off', stale: true }

  const setupNeeded = x.radiosLoaded && (x.radios.length === 0 || isN0Call(x.callsign))

  let worst: HealthLevel = 'ok'
  let which: LaneKey | null = null
  for (const k of LANE_ORDER) {
    const lv = lanes[k].level
    if (lv !== 'off' && RANK[lv] > RANK[worst]) {
      worst = lv
      which = k
    }
  }

  const anyPttStuck = radios.some((r) => r.tx.value === 'PTT stuck')
  const anyModemError = x.radios.some((r) => r.modem?.state === ModemStates.Error)
  let label = 'All normal'
  if (which === 'link')
    label = x.apiOffline
      ? 'API offline'
      : x.hubState === 'resyncing'
        ? 'Resyncing'
        : x.hubEverConnected
          ? 'Not live'
          : 'Connecting'
  else if (which === 'rf' || which === 'tx') {
    if (anyPttStuck) label = 'PTT stuck'
    else if (anyModemError) label = 'Modem error'
    else if (which === 'rf')
      label = lanes.rf.value.startsWith('Carrier')
        ? 'Carrier held'
        : lanes.rf.value === 'No TNC'
          ? 'TNC down'
          : lanes.rf.level === 'info'
            ? 'Setup needed'
            : 'RF silent'
    else
      label =
        lanes.tx.value === 'Blocked'
          ? 'Setup needed'
          : lanes.tx.value.startsWith('Waiting')
            ? 'TX waiting'
            : 'TX error'
  } else if (which === 'is')
    label = lanes.is.level === 'err' ? 'APRS-IS auth failed' : 'APRS-IS down'

  if (setupNeeded && which !== 'link' && worst !== 'err') {
    label = 'Setup needed'
    if (RANK[worst] < RANK.info) worst = 'info'
  }

  const block = txBlocked(x, radios)
  return {
    lanes,
    worst,
    label,
    live,
    setupNeeded,
    txBlockedReason: block?.reason ?? null,
    txBlocked: block,
    radios,
  }
}

// ─── Banners ─────────────────────────────────────────────────────────────────

export type BannerAction =
  | { label: string; to: string }
  | {
      label: string
      run: 'hubReconnect' | 'restartModem' | 'forceUnkey' | 'stopModem' | 'isReconnect'
      radioId?: string
    }

export interface HealthBanner {
  id: string
  level: 'err' | 'warn' | 'info'
  title: string
  text: string
  actions: BannerAction[]
}

/**
 * One banner per failure, most severe first. While the feed is down only the
 * feed banner shows — everything else is frozen and would be stale.
 */
export function deriveBanners(
  x: HealthInput,
  h: Health,
  opts: { csmaMaxSeconds: number; setupDone: number; setupTotal: number; setupDismissed: boolean },
): HealthBanner[] {
  const out: HealthBanner[] = []
  if (x.apiOffline) {
    out.push({
      id: 'api',
      level: 'err',
      title: 'Backend unreachable.',
      text: 'Everything on screen is frozen. Retrying…',
      actions: [],
    })
    return out
  }
  if (!h.live && x.hubEverConnected) {
    if (x.hubState === 'resyncing')
      out.push({
        id: 'hub',
        level: 'warn',
        title: 'Reconnected.',
        text: 'Resyncing messages, alerts and radio status missed while offline…',
        actions: [],
      })
    else
      out.push({
        id: 'hub',
        level: 'err',
        title: `Live feed lost ${x.hubDownSince != null ? durationShort(x.now - x.hubDownSince) + ' ago' : ''}.`,
        text: 'Everything on screen is frozen as of the drop. Reconnecting…',
        actions: [{ label: 'Reconnect now', run: 'hubReconnect' }],
      })
    return out
  }
  if (h.live && x.hubState === 'resyncing')
    out.push({
      id: 'hub',
      level: 'warn',
      title: 'Reconnected.',
      text: 'Resyncing messages, alerts and radio status missed while offline…',
      actions: [],
    })

  if (h.setupNeeded && !opts.setupDismissed)
    out.push({
      id: 'setup',
      level: 'info',
      title: 'Finish setting up DireControl.',
      text: `${opts.setupDone} of ${opts.setupTotal} steps done.${
        isN0Call(x.callsign) ? ` Transmit stays blocked while the callsign is ${x.callsign}.` : ''
      }`,
      actions: [{ label: 'Open setup checklist', to: '/setup' }],
    })

  for (const r of x.radios) {
    const m = r.modem
    if (!m) continue
    if (m.pttStuck)
      out.push({
        id: `ptt-${r.radioId}`,
        level: 'err',
        title: `PTT stuck on ${r.name}.`,
        text: `Unkey failed${m.txError ? ` (${m.txError})` : ''}. The radio may still be transmitting.`,
        actions: [
          { label: 'Force unkey', run: 'forceUnkey', radioId: r.radioId },
          { label: 'Stop modem', run: 'stopModem', radioId: r.radioId },
        ],
      })
    else if (m.state === ModemStates.Error)
      out.push({
        id: `modem-${r.radioId}`,
        level: 'err',
        title: `Sound modem stopped on ${r.name}.`,
        text: `${m.errorMessage ?? 'The modem failed.'} RF receive and transmit are down; retrying automatically.`,
        actions: [
          { label: 'Restart modem', run: 'restartModem' },
          { label: 'Open Radio', to: '/radio' },
        ],
      })
  }

  for (const rh of h.radios) {
    if (rh.rf.value.startsWith('Carrier held'))
      out.push({
        id: `carrier-${rh.radioId}`,
        level: 'warn',
        title: `Carrier detect held ${rh.rf.value.slice('Carrier held '.length)} on ${rh.name}`,
        text: `with no packets decoded. Frames wait for a clear channel and are discarded after ${opts.csmaMaxSeconds} s. Check squelch and RX level.`,
        actions: [
          { label: 'Open Radio', to: '/radio' },
          { label: 'Diagnostics', to: '/radio/diagnostics' },
        ],
      })
    else if (rh.rf.value.startsWith('Silent'))
      out.push({
        id: `silent-${rh.radioId}`,
        level: 'warn',
        title: `No RF decode for ${rh.rf.value.slice('Silent '.length)} on ${rh.name}`,
        text:
          x.aprsIs?.state === AprsIsStates.Connected
            ? 'while the modem is running. The map still looks busy because APRS-IS is flowing.'
            : 'while the modem is running. Check the audio level, squelch and capture device.',
        actions: [
          { label: 'Check RX level', to: '/radio' },
          { label: 'Diagnostics', to: '/radio/diagnostics' },
        ],
      })
  }

  if (x.aprsIs?.state === AprsIsStates.AuthFailed)
    out.push({
      id: 'is-auth',
      level: 'err',
      title: 'APRS-IS login rejected.',
      text: `The passcode doesn't match ${x.callsign ?? 'the callsign'}, so RF→IS gating has stopped.${
        x.aprsIs.lastError ? ` (${x.aprsIs.lastError})` : ''
      }`,
      actions: [
        { label: 'Fix passcode', to: '/settings/aprsis' },
        { label: 'Reconnect', run: 'isReconnect' },
      ],
    })

  const rank = { err: 0, warn: 1, info: 2 }
  return out.sort((a, b) => rank[a.level] - rank[b.level])
}

// ─── First-run checklist ─────────────────────────────────────────────────────

export interface SetupStep {
  key: 'callsign' | 'home' | 'radio' | 'ptt' | 'aprsis'
  label: string
  done: boolean
  /** Needs attention (N0CALL, auth failed), not merely not done yet. */
  bad?: boolean
  optional?: boolean
  detail: string
  to: string
}

export interface SetupInput {
  callsign: string | null
  homeSet: boolean
  /** Active radios with the fields the checklist reads. */
  radios: Array<{ name: string; modemEnabled: boolean; txEnabled: boolean; captureDevice: string }>
  /** A PTT test on an audio radio was confirmed as keying the radio. */
  pttTested: boolean
  aprsIsState: AprsIsState
  aprsIsServerName: string | null
}

export function deriveSetupSteps(x: SetupInput): SetupStep[] {
  const n0 = isN0Call(x.callsign) || !x.callsign
  const audio = x.radios.find((r) => r.modemEnabled)
  const anyRadio = x.radios.length > 0
  const txRadio = x.radios.find((r) => r.txEnabled)
  return [
    {
      key: 'callsign',
      label: 'Callsign',
      done: !n0,
      bad: n0,
      detail: n0
        ? `Still the default ${x.callsign ?? 'N0CALL'}. Transmit is blocked until you change it.`
        : x.callsign!,
      to: '/settings/station',
    },
    {
      key: 'home',
      label: 'Home position',
      done: x.homeSet,
      detail: x.homeSet ? 'Set' : 'Not set. Beacons, range rings and distances need it.',
      to: '/settings/station',
    },
    {
      key: 'radio',
      label: 'Radio and audio',
      done: anyRadio,
      detail: audio
        ? `${audio.name} · capture ${audio.captureDevice}`
        : anyRadio
          ? `${x.radios[0]!.name} (external TNC)`
          : 'No radio yet. Add one and pick its sound card.',
      to: '/settings/radios',
    },
    {
      key: 'ptt',
      label: 'PTT test',
      done: !!txRadio && (x.pttTested || !audio),
      detail: !txRadio
        ? 'Enable TX on a radio, then key it for 1 s to prove PTT works.'
        : x.pttTested || !audio
          ? `Keyed OK · ${txRadio.name}`
          : 'Key the radio for 1 s to prove PTT works before the first beacon.',
      to: '/settings/radios',
    },
    {
      key: 'aprsis',
      label: 'APRS-IS (optional)',
      optional: true,
      done: x.aprsIsState === AprsIsStates.Connected,
      bad: x.aprsIsState === AprsIsStates.AuthFailed,
      detail:
        x.aprsIsState === AprsIsStates.Connected
          ? `Connected · ${x.aprsIsServerName ?? 'APRS-IS'}`
          : x.aprsIsState === AprsIsStates.AuthFailed
            ? 'Login rejected. Check the passcode.'
            : 'Off. Turn on to see stations beyond RF range and to iGate.',
      to: '/settings/aprsis',
    },
  ]
}
