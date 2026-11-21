import { RetryState, type InboxMessageDto } from '@/types/message'

/**
 * Multi-part messages: the server's MessageSplitter sends long text as
 * numbered APRS messages with a " (n/m)" suffix (APRSdroid convention). Each
 * part is its own outbound row; later parts are held until the previous
 * part is ACKed.
 */
export interface PartInfo {
  n: number
  m: number
  /** The part's text without the "(n/m)" marker. */
  text: string
}

const SUFFIX = /^(.*?)\s*\((\d{1,2})\/(\d{1,2})\)$/s
const PREFIX = /^\((\d{1,2})\/(\d{1,2})\)\s*(.*)$/s

export function parsePart(body: string): PartInfo | null {
  let n: number, m: number, text: string
  const s = SUFFIX.exec(body)
  if (s) {
    ;[n, m, text] = [Number(s[2]), Number(s[3]), s[1] ?? '']
  } else {
    const p = PREFIX.exec(body)
    if (!p) return null
    ;[n, m, text] = [Number(p[1]), Number(p[2]), p[3] ?? '']
  }
  return m >= 2 && n >= 1 && n <= m ? { n, m, text } : null
}

export interface PartRow {
  n: number
  /** Null for a part the server never queued (the send was cut short). */
  msg: InboxMessageDto | null
  text: string
}

export type ThreadItem =
  | { kind: 'single'; key: string; msg: InboxMessageDto }
  | { kind: 'multi'; key: string; total: number; parts: PartRow[] }

/**
 * Folds consecutive outbound parts of one compose ("(1/3)", "(2/3)", …) into
 * one item. Parts of one send share a timestamp, so their order within the
 * run isn't trusted: a run continues while the next outbound message is a
 * part of the same total that the group doesn't have yet. Gaps (parts the
 * server never queued) become rows with no message.
 */
export function groupThread(
  messages: InboxMessageDto[],
  isOutbound: (m: InboxMessageDto) => boolean,
): ThreadItem[] {
  const out: ThreadItem[] = []
  let open: { total: number; parts: PartRow[] } | null = null

  const close = () => {
    if (!open) return
    const key = `multi-${Math.min(...open.parts.map((p) => p.msg!.id))}`
    const parts: PartRow[] = []
    for (let n = 1; n <= open.total; n++) {
      parts.push(open.parts.find((p) => p.n === n) ?? { n, msg: null, text: '' })
    }
    out.push({ kind: 'multi', key, total: open.total, parts })
    open = null
  }

  for (const msg of messages) {
    const info = isOutbound(msg) ? parsePart(msg.body) : null
    if (info && open && info.m === open.total && !open.parts.some((p) => p.n === info.n)) {
      open.parts.push({ n: info.n, msg, text: info.text })
      continue
    }
    close()
    if (info) {
      open = { total: info.m, parts: [{ n: info.n, msg, text: info.text }] }
      continue
    }
    out.push({ kind: 'single', key: `msg-${msg.id}`, msg })
  }
  close()
  return out
}

/** Parts that the operator can push out again with a per-message retry. */
export function isResendable(msg: InboxMessageDto | null): msg is InboxMessageDto {
  return !!msg && (msg.retryState === RetryState.Failed || msg.retryState === RetryState.Cancelled)
}

/** A later part waiting for the previous part's ACK; never transmitted yet. */
export function isHeld(msg: InboxMessageDto): boolean {
  return msg.retryState === RetryState.Pending && msg.lastSentAt === null
}

export function retryBadge(msg: InboxMessageDto): { color: string; text: string } {
  switch (msg.retryState) {
    case RetryState.Retrying:
      return { color: 'warning', text: `Attempt ${msg.retryCount + 1}/${msg.maxRetries}` }
    case RetryState.Acknowledged:
      return { color: 'success', text: 'Acknowledged' }
    case RetryState.Failed:
      return { color: 'error', text: `Failed after ${msg.retryCount} attempts` }
    case RetryState.Cancelled:
      return { color: 'default', text: 'Cancelled' }
    default:
      // A later part of a multi-part message waits for the previous part's ACK.
      return isHeld(msg)
        ? { color: 'info', text: 'Queued: waits for previous part' }
        : { color: 'info', text: 'Pending' }
  }
}
