// WebAudio alert chirp — no audio asset needed. Browsers keep the context
// suspended until the user has interacted with the page; if audio still can't
// play, fail silently — the toast and browser notification carry the alert.
let audioContext: AudioContext | null = null

export function playAlertSound() {
  try {
    audioContext ??= new AudioContext()
    if (audioContext.state === 'suspended') void audioContext.resume()

    const now = audioContext.currentTime
    const tones: Array<{ freq: number; start: number; duration: number }> = [
      { freq: 880, start: 0, duration: 0.18 },
      { freq: 660, start: 0.22, duration: 0.18 },
      { freq: 880, start: 0.44, duration: 0.28 },
    ]

    for (const tone of tones) {
      const osc = audioContext.createOscillator()
      const gain = audioContext.createGain()
      osc.type = 'sine'
      osc.frequency.value = tone.freq
      gain.gain.setValueAtTime(0, now + tone.start)
      gain.gain.linearRampToValueAtTime(0.3, now + tone.start + 0.02)
      gain.gain.linearRampToValueAtTime(0, now + tone.start + tone.duration)
      osc.connect(gain)
      gain.connect(audioContext.destination)
      osc.start(now + tone.start)
      osc.stop(now + tone.start + tone.duration + 0.05)
    }
  } catch {
    /* audio unavailable */
  }
}
