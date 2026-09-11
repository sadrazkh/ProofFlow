<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, ref } from 'vue';
import { api, ApiError } from '../lib/api';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';
import { PASTE_FORMATS, type ParsedPaste } from './dataTypes';

/**
 * The other door to an endpoint's inputs.
 *
 * The dropdown beside this one only works when the set already exists, and the data that would
 * become one is almost always sitting on somebody's clipboard while they read this page. Sending
 * them to the data-set screens to create it, paste it, save it and come back is four navigations
 * to answer one question — and the Test button refuses for the whole of that trip.
 *
 * It is not a second parser. The preview comes from the same `datasets/parse` action the data-set
 * editor uses, and the confirm sends the text back rather than the rows, so the thing that decides
 * what a paste means stays in one place. The preview is not skippable for the same reason it is
 * not skippable there: the difference between a comma-separated table and a plain list is one
 * comma inside one value, and guessing wrong makes a set that runs perfectly against the wrong
 * inputs.
 */

const props = defineProps<{
  projectId: string;
  endpointId: string;

  /** What the set is called unless the reader says otherwise. */
  endpointName: string;

  /**
   * Column names this endpoint's request asks each row for, read out of the stored request.
   *
   * Empty is ordinary — an endpoint can sweep rows without naming a column, and one that names
   * none simply gets no hint.
   */
  needs: string[];
}>();

const name = ref(props.endpointName);
const paste = ref('');
const forcedFormat = ref('');
const preview = ref<ParsedPaste | null>(null);
const keyColumn = ref('');
const parsing = ref(false);
const saving = ref(false);

/** The server's last refusal, kept beside the fields rather than in a toast that floats away. */
const refusal = ref('');

/**
 * Columns the request asks for that the paste does not have.
 *
 * The commonest way this goes wrong, and the quietest: the reference resolves to nothing, every
 * row calls the same wrong address, and the sweep reports hundreds of identical failures that read
 * like the API being down. Said before anything is written, not after.
 */
const missing = computed(() => {
  const read = preview.value;
  return read ? props.needs.filter((column) => !read.columns.includes(column)) : [];
});

const canSave = computed(() =>
  !saving.value && (preview.value?.rows.length ?? 0) > 0 && name.value.trim().length > 0);

function forget(): void {
  preview.value = null;
  refusal.value = '';
}

async function parse(): Promise<void> {
  if (paste.value.trim().length === 0) {
    forget();
    return;
  }

  parsing.value = true;
  refusal.value = '';

  try {
    const read = await api.post<ParsedPaste>(
      `/projects/${props.projectId}/datasets/parse`,
      { text: paste.value, format: forcedFormat.value || null });

    preview.value = read;

    // The first column, as the data-set editor also settles on. Rows keyed by position are honest
    // for five hand-typed values and treacherous for two thousand imported ones, because position
    // changes the first time anybody sorts the list and re-points every approved answer.
    keyColumn.value = read.columns.includes(keyColumn.value) ? keyColumn.value : (read.columns[0] ?? '');
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    parsing.value = false;
  }
}

/**
 * Makes the set, freezes version one, and points the endpoint at it.
 *
 * The format goes back with the text so the server reads it exactly as the preview did, rather
 * than guessing a second time and possibly guessing differently.
 */
async function save(): Promise<void> {
  const read = preview.value;
  if (!read) return;

  saving.value = true;
  refusal.value = '';

  try {
    const done = await api.post<{ url: string }>(
      `/projects/${props.projectId}/endpoints/${props.endpointId}/inputs/paste`,
      {
        name: name.value.trim(),
        text: paste.value,
        format: read.format,
        keyColumn: keyColumn.value || null,
      });

    location.assign(done.url);
  } catch (error) {
    // Shown in place. A duplicate name is a refusal about the field directly above it, and a
    // toast in the corner is the wrong place to say so.
    refusal.value = error instanceof ApiError ? error.message : t('error.body');
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <section class="endpoint-paste stack-2">
    <h3 class="section-title">
      <Icon name="clipboard-paste" />{{ t('endpoint.inputs.paste.title') }}
    </h3>
    <p class="section-help">{{ t('endpoint.inputs.paste.help') }}</p>

    <p v-if="needs.length" class="section-help">
      {{ t('endpoint.inputs.paste.needs', needs.join(', ')) }}
    </p>

    <label class="field">
      <span class="field-label">{{ t('common.name') }}</span>
      <input v-model="name" class="input" dir="auto" :placeholder="endpointName" />
    </label>

    <textarea
      v-model="paste"
      class="textarea input-mono"
      rows="4"
      dir="ltr"
      :aria-label="t('endpoint.inputs.paste.title')"
      :placeholder="t('dataset.pastePlaceholder')"
      @input="forget"
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

      <p v-if="missing.length" class="response-notice">
        <Icon name="triangle-alert" />{{ t('endpoint.inputs.paste.missing', missing.join(', ')) }}
      </p>

      <!-- Lines that could not be read, with their numbers. Never silently dropped. -->
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

      <div class="row wrap">
        <label v-if="preview.columns.length" class="field field-inline">
          <span class="field-label">{{ t('dataset.keyColumn') }}</span>
          <select v-model="keyColumn" class="select">
            <option value="">{{ t('dataset.byPosition') }}</option>
            <option v-for="column in preview.columns" :key="column" :value="column">{{ column }}</option>
          </select>
        </label>

        <span class="grow"></span>

        <button type="button" class="btn btn-primary btn-sm" :disabled="!canSave" @click="save">
          <Icon name="table-2" />
          {{ saving ? t('common.saving') : t('endpoint.inputs.paste.action', preview.rows.length) }}
        </button>
      </div>
    </div>

    <p v-if="refusal" class="response-notice" role="alert">
      <Icon name="triangle-alert" />{{ refusal }}
    </p>
  </section>
</template>
