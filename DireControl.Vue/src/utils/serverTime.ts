// Offset between the server clock (the source of every timestamp in the app)
// and this client's clock, so "time ago" can't go negative from clock skew.
let offsetMs = 0

// Estimate the offset from a timed /about call. Using the midpoint of the round
// trip cancels out network latency (Cristian's algorithm).
export function recordServerSync(serverTimeIso: string, requestStart: number, responseEnd: number): void {
  const clientMid = requestStart + (responseEnd - requestStart) / 2
  offsetMs = new Date(serverTimeIso).getTime() - clientMid
}

// Current time on the server's clock, in epoch ms.
export function serverNow(): number {
  return Date.now() + offsetMs
}
