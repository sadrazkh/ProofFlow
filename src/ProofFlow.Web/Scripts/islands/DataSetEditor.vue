<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, onMounted, ref } from 'vue';
import { api, ApiError } from '../lib/api';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';
import { PASTE_FORMATS, type DataRow, type DataSetDraft, type ParsedPaste } from './dataTypes';

/**
 * Where the inputs come from.
 *
 * The paste box is the point of this screen, not the table. The data arrives out of a spreadsheet,
 * or a database client, or a chat message with forty identifiers one per line — and asking somebody
 * to convert that into a particular format before the tool will take it is asking them to do the
 * parsing by hand.
 *
 * So it guesses, shows what it read, and lets the guess be overruled. Nothing is imported until
 * somebody looks at the preview and presses the button, because the difference between a
 * comma-separated file and a plain list is one comma inside one value — and getting it wrong makes
 * a data set that runs perfectly against the wrong inputs.
 */

const props = defineProps<{
  projectId: string;
  dataSetId: string | null;
  versionId: string | null;
  canManage: boolean;
}>();

const columns = ref<string[]>([]);
const rows = ref<DataRow[]>([]);
const keyColumn = ref<string>('');
const name = ref('');
const description = ref('');

const paste = ref('');
const forcedFormat = ref<string>('');
const preview = ref<ParsedPaste | null>(null);
const parsing = ref(false);
const saving = ref(false);

const dirty = ref(false);
const saved = ref('');

const isNew = computed(() => props.dataSetId === null);
const canSave = computed(() =>
  props.canManage && !saving.value && rows.value.length > 0 && (!isNew.value || name.value.trim().length > 0));

/**
 * Keys as they would be stored, so a duplicate is visible before it is saved.
 *
 * A duplicate key means two approved answers for one input, which the baseline cannot hold. The
 * server disambiguates rather than refusing, and saying so here is what stops somebody discovering
 * it a thousand rows later.
 */
const duplicateKeys = computed(() => {
  if (!keyColumn.value) return [];

  const seen = new Set<string>();
  const duplicates = new Set<string>();

  for (const row of rows.value) {
    const key = row[keyColumn.value] ?? '';
    if (key.length === 0) continue;
    if (seen.has(key)) duplicates.add(key);
    seen.add(key);
  }

  return [...duplicates];
});

const blankKeys = computed(() =>
  keyColumn.value ? rows.value.filter((row) => !(row[keyColumn.value] ?? '').length).length : 0);

onMounted(() => {
  if (props.versionId) void load();
  window.addEventListener('beforeunload', warnIfDirty);
});

function warnIfDirty(event: BeforeUnloadEvent): void {
  if (dirty.value) event.preventDefault();
}

async function load(): Promise<void> {
  try {
    const draft = await api.get<DataSetDraft>(
      `/projects/${props.projectId}/datasets/${props.dataSetId}/versions/${props.versionId}/rows`);

    columns.value = draft.columns;
    rows.value = draft.rows.map((row) => ({ ...row }));
    keyColumn.value = draft.keyColumn ?? '';
    description.value = draft.description ?? '';
    saved.value = snapshot();
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  }
}

function snapshot(): string {
  return JSON.stringify({ columns: columns.value, rows: rows.value, key: keyColumn.value });
}

function touch(): void {
  dirty.value = snapshot() !== saved.value;
}

/** Asks the server what the paste is. The parser lives there so both halves agree on one answer. */
async function parse(): Promise<void> {
  if (paste.value.trim().length === 0) {
    preview.value = null;
    return;
  }

  parsing.value = true;

  try {
    preview.value = await api.post<ParsedPaste>(
      `/projects/${props.projectId}/datasets/parse`,
      { text: paste.value, format: forcedFormat.value || null });
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    parsing.value = false;
  }
}

/**
 * Takes the preview into the table.
 *
 * Replaces rather than appends, and says so on the button. Appending is the friendlier-sounding
 * default and the one that silently doubles a set when somebody pastes twice.
 */
function applyPreview(): void {
  if (!preview.value) return;

  columns.value = [...preview.value.columns];
  rows.value = preview.value.rows.map((row) => ({ ...row }));

  if (!columns.value.includes(keyColumn.value)) keyColumn.value = columns.value[0] ?? '';

  preview.value = null;
  paste.value = '';
  touch();
}

function addRow(): void {
  rows.value = [...rows.value, Object.fromEntries(columns.value.map((column) => [column, '']))];
  touch();
}

function removeRow(index: number): void {
  rows.value = rows.value.filter((_, position) => position !== index);
  touch();
}

function addColumn(): void {
  const named = `column${columns.value.length + 1}`;
  columns.value = [...columns.value, named];
  rows.value = rows.value.map((row) => ({ ...row, [named]: '' }));
  touch();
}

function renameColumn(index: number, next: string): void {
  const previous = columns.value[index];
  if (!previous || !next || previous === next || columns.value.includes(next)) return;

  columns.value = columns.value.map((column, position) => (position === index ? next : column));
  rows.value = rows.value.map((row) => {
    const { [previous]: value, ...rest } = row;
    return { ...rest, [next]: value ?? '' };
  });

  if (keyColumn.value === previous) keyColumn.value = next;
  touch();
}

function removeColumn(index: number): void {
  const gone = columns.value[index];
  if (!gone) return;

  columns.value = columns.value.filter((_, position) => position !== index);
  rows.value = rows.value.map((row) => {
    const { [gone]: _removed, ...rest } = row;
    return rest;
  });

  if (keyColumn.value === gone) keyColumn.value = columns.value[0] ?? '';
  touch();
}

async function save(): Promise<void> {
  saving.value = true;

  const draft: DataSetDraft = {
    columns: columns.value,
    rows: rows.value,
    keyColumn: keyColumn.value || null,
    description: description.value || null,
  };

  try {
    if (isNew.value) {
      const created = await api.post<{ url: string }>(
        `/projects/${props.projectId}/datasets`,
        { name: name.value.trim(), description: description.value || null, draft });

      dirty.value = false;
      location.assign(created.url);
      return;
    }

    const version = await api.post<{ number: number; rows: number }>(
      `/projects/${props.projectId}/datasets/${props.dataSetId}/versions`, draft);

    saved.value = snapshot();
    dirty.value = false;
    toast(t('dataset.versionSaved', version.number, version.rows), 'success');
    location.reload();
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <div class="dataset-editor stack">
    <div v-if="isNew" class="card card-pad stack">
      <label class="field">
        <span class="field-label">{{ t('common.name') }}</span>
        <input v-model="name" class="input" dir="auto" :placeholder="t('dataset.namePlaceholder')" />
      </label>
      <label class="field">
        <span class="field-label">
          {{ t('common.description') }}
          <span class="field-optional">{{ t('common.optional') }}</span>
        </span>
        <input v-model="description" class="input" dir="auto" />
      </label>
    </div>

    <!--
      The paste box first, above the table. It is how the data actually arrives, and putting the
      table first would make the commonest path look like the exception.
    -->
    <section v-if="canManage" class="card card-pad stack-2">
      <h2 class="section-title">
        <Icon name="clipboard-paste" />{{ t('dataset.pasteTitle') }}
      </h2>
      <p class="section-help">{{ t('dataset.pasteHelp') }}</p>

      <textarea
        v-model="paste"
        class="textarea input-mono"
        rows="5"
        dir="ltr"
        :placeholder="t('dataset.pastePlaceholder')"
        @input="preview = null"
      ></textarea>

      <div class="row wrap">
        <button type="button" class="btn btn-secondary btn-sm" :disabled="parsing || !paste.trim()" @click="parse">
          <Icon name="wand-sparkles" />
          {{ parsing ? t('dataset.reading') : t('dataset.read') }}
        </button>

        <label class="field field-inline">
          <span class="field-label">{{ t('dataset.readAs') }}</span>
          <select v-model="forcedFormat" class="select" @change="parse">
            <option value="">{{ t('dataset.detect') }}</option>
            <option v-for="format in PASTE_FORMATS" :key="format" :value="format">
              {{ t(`dataset.format.${format}`) }}
            </option>
          </select>
        </label>
      </div>

      <!-- The guess, shown before it becomes rows. -->
      <div v-if="preview" class="paste-preview stack-2">
        <p class="row wrap">
          <span class="badge badge-accent">{{ t(`dataset.format.${preview.format}`) }}</span>
          <span class="text-xs subtle">
            {{ t('dataset.previewSummary', preview.rows.length, preview.columns.length) }}
          </span>
        </p>

        <div v-if="preview.rows.length" class="table-wrap">
          <table class="table">
            <caption class="sr-only">{{ t('dataset.previewTitle') }}</caption>
            <thead>
              <tr><th v-for="column in preview.columns" :key="column">{{ column }}</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in preview.rows.slice(0, 5)" :key="index">
                <td v-for="column in preview.columns" :key="column" class="mono">{{ row[column] }}</td>
              </tr>
            </tbody>
          </table>
        </div>

        <!-- Rows that could not be read, with their line numbers. Never silently dropped. -->
        <details v-if="preview.problems.length" class="paste-problems">
          <summary>{{ t('dataset.problems', preview.problems.length) }}</summary>
          <ul>
            <li v-for="problem in preview.problems" :key="problem.line">
              <span class="tabular">{{ problem.line }}</span>
              <code class="mono" dir="ltr">{{ problem.text }}</code>
              <span class="subtle">{{ problem.reason }}</span>
            </li>
          </ul>
        </details>

        <button
          type="button"
          class="btn btn-primary btn-sm"
          :disabled="preview.rows.length === 0"
          @click="applyPreview"
        >
          <Icon name="table-2" />{{ t('dataset.replaceWith', preview.rows.length) }}
        </button>
      </div>
    </section>

    <section class="card">
      <div class="card-header">
        <div>
          <h2 class="card-title">{{ t('dataset.rows') }}</h2>
          <p class="card-subtitle">{{ t('dataset.rowCount', rows.length, columns.length) }}</p>
        </div>

        <label v-if="columns.length" class="field field-inline">
          <span class="field-label">{{ t('dataset.keyColumn') }}</span>
          <select v-model="keyColumn" class="select" :disabled="!canManage" @change="touch">
            <option value="">{{ t('dataset.byPosition') }}</option>
            <option v-for="column in columns" :key="column" :value="column">{{ column }}</option>
          </select>
        </label>
      </div>

      <!--
        Said before saving, not after. A duplicate key means two approved answers for one input,
        and finding that out a thousand rows later is finding it out too late.
      -->
      <p v-if="duplicateKeys.length" class="response-notice">
        <Icon name="triangle-alert" />
        {{ t('dataset.duplicateKeys', duplicateKeys.length, duplicateKeys.slice(0, 3).join(', ')) }}
      </p>
      <p v-if="blankKeys" class="response-notice">
        <Icon name="triangle-alert" />{{ t('dataset.blankKeys', blankKeys) }}
      </p>
      <p v-if="columns.length && !keyColumn" class="response-notice">
        <Icon name="info" />{{ t('dataset.noKeyColumn') }}
      </p>

      <div v-if="rows.length === 0" class="empty empty-inline">
        <div class="empty-art"><Icon name="table-2" /></div>
        <p class="empty-body">{{ t('dataset.noRows') }}</p>
      </div>

      <div v-else class="table-wrap">
        <table class="table data-table">
          <caption class="sr-only">{{ t('dataset.rows') }}</caption>
          <thead>
            <tr>
              <th class="data-ordinal"><span class="sr-only">{{ t('dataset.position') }}</span></th>
              <th v-for="(column, index) in columns" :key="column">
                <div class="data-head">
                  <input
                    class="input input-mono data-column"
                    :value="column"
                    :disabled="!canManage"
                    :aria-label="t('dataset.columnName', column)"
                    @change="renameColumn(index, ($event.target as HTMLInputElement).value.trim())"
                  />
                  <span v-if="column === keyColumn" class="badge badge-accent">{{ t('dataset.key') }}</span>
                  <button
                    v-if="canManage"
                    type="button"
                    class="btn btn-ghost btn-icon btn-sm has-tip"
                    :data-tip="t('dataset.removeColumn')"
                    :aria-label="t('dataset.removeColumnNamed', column)"
                    @click="removeColumn(index)"
                  >
                    <Icon name="x" />
                  </button>
                </div>
              </th>
              <th class="data-actions"><span class="sr-only">{{ t('action.delete') }}</span></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(row, index) in rows.slice(0, 200)" :key="index">
              <td class="data-ordinal tabular subtle">{{ index + 1 }}</td>
              <td v-for="column in columns" :key="column">
                <input
                  v-model="row[column]"
                  class="input input-mono"
                  dir="ltr"
                  :disabled="!canManage"
                  :aria-label="t('dataset.cell', column, index + 1)"
                  @input="touch"
                />
              </td>
              <td class="data-actions">
                <button
                  v-if="canManage"
                  type="button"
                  class="btn btn-ghost btn-icon btn-sm has-tip"
                  :data-tip="t('action.delete')"
                  :aria-label="t('dataset.removeRow', index + 1)"
                  @click="removeRow(index)"
                >
                  <Icon name="trash-2" />
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!--
        Only the first two hundred rows are editable in place. A two-thousand-row set is imported,
        not typed, and rendering two thousand rows of inputs is how this screen stops responding.
      -->
      <p v-if="rows.length > 200" class="response-notice">
        <Icon name="info" />{{ t('dataset.showingFirst', 200, rows.length) }}
      </p>

      <div class="card-footer">
        <template v-if="canManage">
          <button type="button" class="btn btn-ghost btn-sm" @click="addRow">
            <Icon name="plus" />{{ t('dataset.addRow') }}
          </button>
          <button type="button" class="btn btn-ghost btn-sm" @click="addColumn">
            <Icon name="plus" />{{ t('dataset.addColumn') }}
          </button>
        </template>

        <span class="grow"></span>

        <span v-if="dirty" class="text-xs subtle">
          <Icon name="circle-dot" />{{ t('common.unsaved') }}
        </span>

        <button type="button" class="btn btn-primary" :disabled="!canSave" @click="save">
          <Icon name="save" />
          {{ saving ? t('common.saving') : (isNew ? t('action.create') : t('dataset.saveVersion')) }}
        </button>
      </div>
    </section>
  </div>
</template>
