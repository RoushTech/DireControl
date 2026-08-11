<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  base64ToBytes,
  bytesToBase64,
  createMacro,
  deleteMacro,
  listMacros,
  updateMacro,
  type TerminalMacroDto,
} from '@/api/terminalApi'
import { apiErrorDetail } from '@/api/axios'

const emit = defineEmits<{
  (e: 'macro', bytes: Uint8Array): void
}>()

const macros = ref<TerminalMacroDto[]>([])
const sortedMacros = computed(() => [...macros.value].sort((a, b) => a.sortOrder - b.sortOrder))

async function load() {
  try {
    macros.value = await listMacros()
  } catch {
    /* the bar just stays empty */
  }
}

onMounted(load)

function fireMacro(macro: TerminalMacroDto) {
  const payload = base64ToBytes(macro.payloadBase64)
  if (!macro.appendCr) {
    emit('macro', payload)
    return
  }
  const bytes = new Uint8Array(payload.length + 1)
  bytes.set(payload)
  bytes[payload.length] = 0x0d
  emit('macro', bytes)
}

// ── Manage dialog ────────────────────────────────────────────────────────────
const manageOpen = ref(false)
const editingId = ref<number | null>(null)
const editorOpen = ref(false)
const mLabel = ref('')
const mPayloadText = ref('')
const mAppendCr = ref(true)
const mSortOrder = ref(0)
const saving = ref(false)
const manageError = ref('')

function openAdd() {
  editingId.value = null
  mLabel.value = ''
  mPayloadText.value = ''
  mAppendCr.value = true
  mSortOrder.value =
    macros.value.length > 0 ? Math.max(...macros.value.map((m) => m.sortOrder)) + 1 : 0
  manageError.value = ''
  editorOpen.value = true
}

function openEdit(macro: TerminalMacroDto) {
  editingId.value = macro.id
  mLabel.value = macro.label
  mPayloadText.value = new TextDecoder().decode(base64ToBytes(macro.payloadBase64))
  mAppendCr.value = macro.appendCr
  mSortOrder.value = macro.sortOrder
  manageError.value = ''
  editorOpen.value = true
}

async function saveMacro() {
  if (!mLabel.value.trim()) return
  saving.value = true
  manageError.value = ''
  const request = {
    label: mLabel.value.trim(),
    sortOrder: mSortOrder.value,
    payloadBase64: bytesToBase64(new TextEncoder().encode(mPayloadText.value)),
    appendCr: mAppendCr.value,
  }
  try {
    if (editingId.value !== null) {
      const updated = await updateMacro(editingId.value, request)
      const idx = macros.value.findIndex((m) => m.id === editingId.value)
      if (idx !== -1) macros.value[idx] = updated
    } else {
      macros.value.push(await createMacro(request))
    }
    editorOpen.value = false
  } catch (e: unknown) {
    manageError.value = apiErrorDetail(e)
  } finally {
    saving.value = false
  }
}

async function removeMacro(macro: TerminalMacroDto) {
  manageError.value = ''
  try {
    await deleteMacro(macro.id)
    macros.value = macros.value.filter((m) => m.id !== macro.id)
  } catch (e: unknown) {
    manageError.value = apiErrorDetail(e)
  }
}
</script>

<template>
  <div class="macro-bar">
    <v-chip
      v-for="macro in sortedMacros"
      :key="macro.id"
      size="small"
      variant="tonal"
      color="primary"
      class="macro-chip"
      @click="fireMacro(macro)"
    >
      {{ macro.label }}
    </v-chip>
    <span v-if="sortedMacros.length === 0" class="text-caption text-medium-emphasis">
      No macros yet — add canned commands for one-click sending.
    </span>
    <v-spacer />
    <v-btn
      icon="mdi-pencil-outline"
      size="x-small"
      variant="text"
      title="Manage macros"
      @click="manageOpen = true"
    />

    <!-- Manage dialog -->
    <v-dialog v-model="manageOpen" max-width="560">
      <v-card>
        <v-card-title class="d-flex align-center ga-2">
          <v-icon>mdi-lightning-bolt-outline</v-icon>
          Terminal Macros
          <v-spacer />
          <v-btn size="small" color="primary" prepend-icon="mdi-plus" @click="openAdd">Add</v-btn>
        </v-card-title>
        <v-card-text>
          <v-alert v-if="manageError" type="error" variant="tonal" density="compact" class="mb-3">
            {{ manageError }}
          </v-alert>
          <v-table v-if="sortedMacros.length > 0" density="compact">
            <thead>
              <tr>
                <th style="width: 60px">Order</th>
                <th>Label</th>
                <th>CR</th>
                <th class="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="macro in sortedMacros" :key="macro.id">
                <td>{{ macro.sortOrder }}</td>
                <td>{{ macro.label }}</td>
                <td>
                  <v-icon v-if="macro.appendCr" size="14" color="success">mdi-check</v-icon>
                  <span v-else class="text-medium-emphasis">—</span>
                </td>
                <td class="text-right text-no-wrap">
                  <v-btn icon="mdi-pencil" size="x-small" variant="text" @click="openEdit(macro)" />
                  <v-btn
                    icon="mdi-close"
                    size="x-small"
                    variant="text"
                    color="error"
                    @click="removeMacro(macro)"
                  />
                </td>
              </tr>
            </tbody>
          </v-table>
          <div v-else class="text-center text-medium-emphasis py-4">No macros defined.</div>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="manageOpen = false">Close</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Add / edit dialog -->
    <v-dialog v-model="editorOpen" max-width="480">
      <v-card>
        <v-card-title>{{ editingId !== null ? 'Edit Macro' : 'Add Macro' }}</v-card-title>
        <v-card-text>
          <v-text-field
            v-model="mLabel"
            label="Label"
            placeholder="e.g. LIST"
            hide-details="auto"
            :rules="[(v: string) => v.trim().length > 0 || 'Required']"
            class="mb-3"
          />
          <v-textarea
            v-model="mPayloadText"
            label="Payload"
            variant="outlined"
            density="compact"
            rows="2"
            hide-details="auto"
            hint="Edited as text; stored base64-encoded as raw bytes."
            persistent-hint
            class="mb-3"
          />
          <div class="d-flex align-center ga-4">
            <v-checkbox
              v-model="mAppendCr"
              label="Append CR (Enter)"
              density="compact"
              hide-details
            />
            <v-text-field
              v-model.number="mSortOrder"
              label="Sort order"
              type="number"
              hide-details
              style="max-width: 120px"
            />
          </div>
          <v-alert v-if="manageError" type="error" variant="tonal" density="compact" class="mt-3">
            {{ manageError }}
          </v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="editorOpen = false">Cancel</v-btn>
          <v-btn color="primary" :loading="saving" :disabled="!mLabel.trim()" @click="saveMacro">
            Save
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </div>
</template>

<style scoped>
.macro-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  overflow-x: auto;
  flex-shrink: 0;
}

.macro-chip {
  cursor: pointer;
  font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
  flex-shrink: 0;
}
</style>
