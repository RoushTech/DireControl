<script setup lang="ts">
defineProps<{
  providers: Record<string, { name: string; url: string; attribution: string }>
  selected: string
}>()

const emit = defineEmits<{
  'update:selected': [key: string]
}>()
</script>

<template>
  <div class="tile-switcher">
    <v-select
      :model-value="selected"
      :items="
        Object.entries(providers).map(([key, p]) => ({
          title: p.name,
          value: key,
        }))
      "
      density="compact"
      variant="solo"
      hide-details
      bg-color="rgba(30, 30, 30, 0.85)"
      style="min-width: 180px; max-width: 200px"
      @update:model-value="(v: string) => emit('update:selected', v)"
    />
  </div>
</template>

<style scoped>
.tile-switcher {
  position: absolute;
  top: 10px;
  right: 10px;
  z-index: 1000;
}
</style>
