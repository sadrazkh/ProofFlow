<script setup lang="ts">
import { t } from '../lib/i18n';

/**
 * The editable rows behind query parameters, headers and form fields.
 *
 * The always-present blank row at the bottom is the whole interaction: there is no "add" button to
 * find, typing into the last row grows the table, and a row emptied of both name and value removes
 * itself. `enabled` exists so a row someone is experimenting with can be kept without being sent —
 * deleting and retyping a header is how people lose the one that mattered.
 */

export type KeyValueRow = { name: string; value: string; enabled: boolean };

const rows = defineModel<KeyValueRow[]>({ required: true });

defineProps<{
  namePlaceholder?: string;
  valuePlaceholder?: string;
  label: string;
}>();

function onInput(index: number): void {
  const row = rows.value[index];
  if (!row) return;

  // Typing in the last row grows the table.
  if (index === rows.value.length - 1 && (row.name || row.value)) {
    rows.value.push({ name: '', value: '', enabled: true });
  }

  // An emptied row disappears, unless it is the trailing blank one that always stays.
  if (!row.name && !row.value && rows.value.length > 1 && index < rows.value.length - 1) {
    rows.value.splice(index, 1);
  }
}

function remove(index: number): void {
  rows.value.splice(index, 1);
  if (rows.value.length === 0) rows.value.push({ name: '', value: '', enabled: true });
}
</script>

<template>
  <table class="table kv-table">
    <caption class="sr-only">{{ label }}</caption>
    <thead>
      <tr>
        <th class="kv-enabled"><span class="sr-only">{{ t('request.enabled') }}</span></th>
        <th>{{ t('common.name') }}</th>
        <th>{{ t('variable.value') }}</th>
        <th class="kv-actions"><span class="sr-only">{{ t('action.delete') }}</span></th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="(row, index) in rows" :key="index">
        <td class="kv-enabled">
          <input
            v-model="row.enabled"
            class="checkbox"
            type="checkbox"
            :aria-label="t('request.enabled')"
            :disabled="!row.name && !row.value"
          />
        </td>
        <td>
          <input
            v-model="row.name"
            class="input input-mono kv-input"
            :placeholder="namePlaceholder"
            :aria-label="t('common.name')"
            @input="onInput(index)"
          />
        </td>
        <td>
          <input
            v-model="row.value"
            class="input input-mono kv-input"
            :placeholder="valuePlaceholder"
            :aria-label="t('variable.value')"
            @input="onInput(index)"
          />
        </td>
        <td class="kv-actions">
          <button
            v-if="row.name || row.value"
            type="button"
            class="btn btn-ghost btn-icon btn-sm has-tip"
            :data-tip="t('action.delete')"
            :aria-label="t('action.delete')"
            @click="remove(index)"
          >
            <i data-lucide="trash-2" aria-hidden="true"></i>
          </button>
        </td>
      </tr>
    </tbody>
  </table>
</template>
