<script setup lang="ts">
import { ref } from 'vue'

const props = withDefaults(
  defineProps<{
    width?: number
    height?: number
  }>(),
  { width: 342, height: 80 },
)

const canvas = ref<HTMLCanvasElement | null>(null)

/** Maps a 0-255 spectrum magnitude to a dark-blue → yellow heat colour. */
function waterfallColor(v: number): [number, number, number] {
  if (v < 64) return [0, 0, Math.floor(v * 2.5)]
  if (v < 128) return [0, Math.floor((v - 64) * 4), 160]
  if (v < 192) return [Math.floor((v - 128) * 4), 255, Math.floor(160 - (v - 128) * 2.5)]
  return [255, 255, Math.floor((v - 192) * 4)]
}

/** Draws one spectrum row (newest on top). Exposed to the parent. */
function drawRow(bins: number[]) {
  const el = canvas.value
  if (!el) return
  const ctx = el.getContext('2d')
  if (!ctx) return

  // Scroll the existing image down one row, then draw the new row at the top.
  ctx.drawImage(el, 0, 0, el.width, el.height - 1, 0, 1, el.width, el.height - 1)

  const row = ctx.createImageData(el.width, 1)
  for (let x = 0; x < el.width; x++) {
    const bin = Math.min(bins.length - 1, Math.floor((x / el.width) * bins.length))
    const [r, g, b] = waterfallColor(bins[bin] ?? 0)
    row.data[x * 4] = r
    row.data[x * 4 + 1] = g
    row.data[x * 4 + 2] = b
    row.data[x * 4 + 3] = 255
  }
  ctx.putImageData(row, 0, 0)
}

defineExpose({ drawRow })
</script>

<template>
  <div>
    <canvas ref="canvas" :width="props.width" :height="props.height" class="waterfall-canvas" />
    <div
      class="d-flex justify-space-between text-caption text-medium-emphasis"
      :style="{ maxWidth: `${props.width}px` }"
    >
      <span>0</span><span>1k</span><span>2k</span><span>3k</span><span>4 kHz</span>
    </div>
  </div>
</template>

<style scoped>
.waterfall-canvas {
  display: block;
  width: 100%;
  border-radius: 4px;
  background: #000010;
}
</style>
