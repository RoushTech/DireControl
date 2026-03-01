<script setup lang="ts">
import { computed } from 'vue'
import type { TileProviderConfig } from '@/types/map'

const props = defineProps<{
  providers: Record<string, TileProviderConfig>
  selected: string
  apiKeys: Record<string, string>
}>()

const emit = defineEmits<{
  'update:selected': [key: string]
}>()

const GROUP_ORDER = ['light', 'dark', 'satellite', 'specialist'] as const
const GROUP_LABELS: Record<(typeof GROUP_ORDER)[number], string> = {
  light: 'Light',
  dark: 'Dark',
  satellite: 'Satellite',
  specialist: 'Specialist',
}

type ProviderItem = {
  title: string
  value: string
  disabled: boolean
  requiresApiKey: boolean
}

type SelectItem =
  | { type: 'subheader'; title: string }
  | { type: 'divider' }
  | ProviderItem

const items = computed<SelectItem[]>(() => {
  const result: SelectItem[] = []
  for (const group of GROUP_ORDER) {
    const groupProviders = Object.entries(props.providers).filter(([, p]) => p.group === group)
    if (groupProviders.length === 0) continue
    if (result.length > 0) result.push({ type: 'divider' })
    result.push({ type: 'subheader', title: GROUP_LABELS[group] })
    for (const [key, p] of groupProviders) {
      const available = !p.requiresApiKey || !p.apiKeyParam || !!props.apiKeys[p.apiKeyParam]
      result.push({
        title: p.name,
        value: key,
        disabled: !available,
        requiresApiKey: !!p.requiresApiKey,
      })
    }
  }
  return result
})

function isDisabledKeyProvider(item: unknown): boolean {
  if (!item || typeof item !== 'object') return false
  const it = item as Record<string, unknown>
  return !!it['requiresApiKey'] && !!it['disabled']
}

function onSelect(v: unknown) {
  if (typeof v === 'string') emit('update:selected', v)
}
</script>

<template>
  <div class="tile-switcher">
    <v-select
      :model-value="selected"
      :items="items"
      density="compact"
      variant="solo"
      hide-details
      bg-color="rgba(30, 30, 30, 0.85)"
      style="min-width: 190px; max-width: 220px"
      @update:model-value="onSelect"
    >
      <template #item="{ item, props: itemProps }">
        <v-list-item v-bind="itemProps">
          <template v-if="isDisabledKeyProvider(item)" #append>
            <v-tooltip text="Add API key in Settings to enable" location="left">
              <template #activator="{ props: tipProps }">
                <v-icon v-bind="tipProps" size="14" color="warning" class="ml-1">mdi-key-outline</v-icon>
              </template>
            </v-tooltip>
          </template>
        </v-list-item>
      </template>
    </v-select>
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
