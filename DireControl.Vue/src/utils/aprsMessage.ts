// Mirror of the server's MessageSplitter, used only to preview how many APRS
// messages a long text will become. The server does the real split.

export const APRS_MAX_PART_LENGTH = 67
export const APRS_MAX_PARTS = 10

function sanitize(text: string): string {
  return text
    .replace(/\{/g, '(')
    .replace(/\}/g, ')')
    .replace(/\|/g, '/')
    .replace(/~/g, '-')
    .replace(/[\r\n\t]/g, ' ')
    .split(' ')
    .filter((w) => w.length > 0)
    .join(' ')
}

function wrap(text: string, width: number): string[] {
  const chunks: string[] = []
  let current = ''
  for (const word of text.split(' ')) {
    let remaining = word
    while (remaining.length > width) {
      if (current) {
        chunks.push(current)
        current = ''
      }
      chunks.push(remaining.slice(0, width))
      remaining = remaining.slice(width)
    }
    if (current && current.length + 1 + remaining.length > width) {
      chunks.push(current)
      current = ''
    }
    current = current ? `${current} ${remaining}` : remaining
  }
  if (current) chunks.push(current)
  return chunks
}

/** Number of APRS messages the text will be sent as (0 for empty text). */
export function aprsPartCount(text: string): number {
  const clean = sanitize(text)
  if (!clean) return 0
  if (clean.length <= APRS_MAX_PART_LENGTH) return 1
  let count = 2
  for (;;) {
    const chunks = wrap(clean, APRS_MAX_PART_LENGTH - ` (${count}/${count})`.length)
    if (chunks.length <= count) return chunks.length
    count = chunks.length
  }
}
