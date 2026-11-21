/**
 * One colour rule for every receive-level meter (Radio page and the radio
 * editor's calibration box): grey when there is effectively no audio, green
 * in the working range, red when close to clipping.
 */
export function audioLevelColor(level: number): string {
  if (level > 0.9) return 'error'
  if (level > 0.05) return 'success'
  return 'grey'
}
