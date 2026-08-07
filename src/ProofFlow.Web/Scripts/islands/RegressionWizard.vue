<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, onMounted, ref, watch } from 'vue';
import { api, ApiError } from '../lib/api';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';
import type { ParsedPaste } from './dataTypes';

/**
 * The path from "I have an endpoint" to "I have a regression test", for somebody who does not
 * write code.
 *
 * Nine steps, one question each. The alternative — a single page with every field on it — is what
 * this product exists to replace: it is faster for the person who already knows the answers and
 * impassable for the person who does not, which is the person the brief names.
 *
 * Two rules shape it. Nothing is lost by leaving: every step writes to the browser, so closing the
 * tab at step six and coming back tomorrow resumes at step six. And no step lies about what it did
 * — the sweep step reports the sweep it actually ran, including the rows that failed.
 */

const props = defineProps<{
  projectId: string;
  environments: { id: string; name: string; baseUrl: string | null; isProduction: boolean }[];
  baselines: { id: string; name: string }[];
  dataSets: { id: string; name: string; currentVersionId: string | null; rowCount: number }[];
  canRun: boolean;
  canManage: boolean;
}>();

/**
 * The nine steps.
 *
 * Named by the question each one asks rather than by the object it edits, because "Which responses
 * were right?" is a question somebody can answer and "Sample review configuration" is not.
 */
const STEPS = [
  'endpoint', 'environment', 'send', 'baseline', 'data', 'sweep', 'review', 'rules', 'done',
] as const;

type Step = typeof STEPS[number];

const STORAGE = computed(() => `proofflow-wizard-${props.projectId}`);

const step = ref<Step>('endpoint');
const method = ref('GET');
const url = ref('');
const environmentId = ref(props.environments[0]?.id ?? '');
const baselineId = ref('');
const baselineName = ref('');
const dataSetId = ref('');

/**
 * The version to sweep, held separately from the data-set list.
 *
 * The list came with the page. A set created at step five is not in it, so looking the version up
 * by id would leave step six permanently unable to start — the exact path this wizard exists to
 * make possible.
 */
const dataSetVersionId = ref('');
const dataSetRowCount = ref(0);
const paste = ref('');
const preview = ref<ParsedPaste | null>(null);
const keyColumn = ref('');
const sessionId = ref('');
const sweep = ref<{ totalRows: number; differing: number; failed: number; url: string } | null>(null);
const busy = ref(false);

const index = computed(() => STEPS.indexOf(step.value));
const environment = computed(() => props.environments.find((e) => e.id === environmentId.value));
const dataSet = computed(() => props.dataSets.find((d) => d.id === dataSetId.value));

/** Whichever is known: the freshly created version, or the one the chosen set points at. */
const versionToSweep = computed(() => dataSetVersionId.value || dataSet.value?.currentVersionId || '');
const rowsToSweep = computed(() => dataSetRowCount.value || dataSet.value?.rowCount || 0);

/**
 * Whether the step's question has been answered.
 *
 * Checked per step rather than as one big validity flag, so "Next" is disabled with a reason the
 * reader can see rather than mysteriously inert.
 */
const ready = computed(() => {
  switch (step.value) {
    case 'endpoint': return url.value.trim().length > 0;
    case 'environment': return environmentId.value.length > 0 || url.value.startsWith('http');
    case 'send': return true;
    case 'baseline': return baselineId.value.length > 0 || baselineName.value.trim().length > 0;
    case 'data': return versionToSweep.value.length > 0;
    case 'sweep': return sweep.value !== null;
    default: return true;
  }
});

onMounted(restore);
watch([step, method, url, environmentId, baselineId, baselineName,
       dataSetId, dataSetVersionId, sessionId, sweep], remember);

/**
 * Keeps the wizard in the browser, not on the server.
 *
 * A half-finished wizard is not a thing the project owns — saving it as a draft row would fill the
 * project with abandoned attempts nobody meant to keep. But losing nine steps of work to a
 * misclick would be its own small cruelty, so it survives a reload.
 */
function remember(): void {
  try {
    localStorage.setItem(STORAGE.value, JSON.stringify({
      step: step.value, method: method.value, url: url.value,
      environmentId: environmentId.value, baselineId: baselineId.value,
      baselineName: baselineName.value, dataSetId: dataSetId.value,
      dataSetVersionId: dataSetVersionId.value, dataSetRowCount: dataSetRowCount.value,
      sessionId: sessionId.value, sweep: sweep.value,
    }));
  } catch {
    // A full or disabled storage is not worth interrupting anyone over.
  }
}

function restore(): void {
  try {
    const saved = localStorage.getItem(STORAGE.value);
    if (!saved) return;

    const state = JSON.parse(saved);
    if (STEPS.includes(state.step)) step.value = state.step;
    method.value = state.method ?? 'GET';
    url.value = state.url ?? '';
    if (props.environments.some((e) => e.id === state.environmentId)) environmentId.value = state.environmentId;
    if (props.baselines.some((b) => b.id === state.baselineId)) baselineId.value = state.baselineId;
    baselineName.value = state.baselineName ?? '';
    dataSetId.value = state.dataSetId ?? '';
    dataSetVersionId.value = state.dataSetVersionId ?? '';
    dataSetRowCount.value = state.dataSetRowCount ?? 0;
    sessionId.value = state.sessionId ?? '';
    sweep.value = state.sweep ?? null;
  } catch {
    // A blob from an older shape is discarded rather than allowed to break the page.
  }
}

function forget(): void {
  try {
    localStorage.removeItem(STORAGE.value);
  } catch {
    // Nothing to do about it, and nothing worth saying.
  }
}

function go(to: Step): void {
  // Backwards is always allowed; forwards only past a step that has been answered. A rail that
  // cannot be walked back is a rail that traps somebody on step seven.
  if (STEPS.indexOf(to) <= index.value || ready.value) step.value = to;
}

function next(): void {
  if (!ready.value) return;
  const target = STEPS[index.value + 1];
  if (target) step.value = target;
}

function back(): void {
  const target = STEPS[index.value - 1];
  if (target) step.value = target;
}

/**
 * Defines the baseline from the endpoint answered in step one.
 *
 * Not "send it and save what came back", because a sample-based test's request contains
 * {{dataset.current.…}} and there is no current row in the request lab. What this makes is a
 * baseline with no whole-response version — the answers live per input, written later by approving
 * what the sweep captured.
 */
async function defineBaseline(): Promise<void> {
  if (baselineName.value.trim().length === 0) return;
  busy.value = true;

  try {
    const created = await api.post<{ baselineId: string }>(
      `/projects/${props.projectId}/baselines/define`,
      {
        name: baselineName.value.trim(),
        method: method.value,
        url: url.value,
        environmentId: environmentId.value || null,
      });

    baselineId.value = created.baselineId;
    toast(t('wizard.baselineCreated'), 'success');
    next();
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    busy.value = false;
  }
}

async function parse(): Promise<void> {
  if (paste.value.trim().length === 0) return;
  busy.value = true;

  try {
    preview.value = await api.post<ParsedPaste>(
      `/projects/${props.projectId}/datasets/parse`, { text: paste.value, format: null });

    keyColumn.value = preview.value.columns[0] ?? '';
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    busy.value = false;
  }
}

async function createDataSet(): Promise<void> {
  if (!preview.value) return;
  busy.value = true;

  try {
    const created = await api.post<{ dataSetId: string; versionId: string }>(
      `/projects/${props.projectId}/datasets`,
      {
        name: t('wizard.dataSetName', baselineName.value || t('wizard.dataSetFallbackName')),
        draft: {
          columns: preview.value.columns,
          rows: preview.value.rows,
          keyColumn: keyColumn.value || null,
        },
      });

    dataSetId.value = created.dataSetId;
    dataSetVersionId.value = created.versionId;
    dataSetRowCount.value = preview.value.rows.length;
    toast(t('wizard.dataSetCreated', preview.value.rows.length), 'success');
    next();
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    busy.value = false;
  }
}

async function runSweep(): Promise<void> {
  const version = versionToSweep.value;
  if (!version || !baselineId.value) return;

  busy.value = true;

  try {
    const result = await api.post<{ sessionId: string; url: string; totalRows: number; differing: number; failed: number }>(
      `/projects/${props.projectId}/captures/start`,
      {
        baselineId: baselineId.value,
        dataSetVersionId: version,
        environmentId: environmentId.value || null,
        mode: 'Capture',
      });

    sessionId.value = result.sessionId;
    sweep.value = result;
    next();
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    busy.value = false;
  }
}

/** Leaving keeps the answers; only finishing clears them. Both say so on the button. */
function leave(): void {
  forget();
  location.assign(`/projects/${props.projectId}`);
}

function finish(): void {
  forget();
  location.assign(`/projects/${props.projectId}/captures${sessionId.value ? `/${sessionId.value}` : ''}`);
}
</script>

<template>
  <div class="wizard">
    <!--
      The rail. Vertical on a desktop where there is room for the step names, and a compact bar on
      a phone where there is not — the same nine steps either way, because a wizard that hides how
      many are left is a wizard nobody trusts to end.
    -->
    <nav class="wizard-rail" :aria-label="t('wizard.steps')">
      <ol>
        <li
          v-for="(name, position) in STEPS"
          :key="name"
          :class="{ 'is-current': name === step, 'is-done': position < index }"
        >
          <button type="button" :disabled="position > index && !ready" @click="go(name)">
            <span class="wizard-mark" aria-hidden="true">
              <Icon v-if="position < index" name="check" :size="14" />
              <span v-else class="tabular">{{ position + 1 }}</span>
            </span>
            <span class="wizard-name">{{ t(`wizard.step.${name}`) }}</span>
          </button>
        </li>
      </ol>
    </nav>

    <section class="card card-pad wizard-panel stack">
      <header class="stack-2">
        <p class="text-xs subtle tabular">{{ t('wizard.progress', index + 1, STEPS.length) }}</p>
        <h2 class="card-title">{{ t(`wizard.title.${step}`) }}</h2>
        <p class="section-help">{{ t(`wizard.help.${step}`) }}</p>
      </header>

      <div v-if="step === 'endpoint'" class="stack">
        <div class="row wrap">
          <label class="field">
            <span class="field-label">{{ t('request.method') }}</span>
            <select v-model="method" class="select">
              <option v-for="verb in ['GET', 'POST', 'PUT', 'PATCH', 'DELETE']" :key="verb">{{ verb }}</option>
            </select>
          </label>
          <label class="field grow">
            <span class="field-label">{{ t('request.url') }}</span>
            <input v-model="url" class="input input-mono" dir="ltr"
                   placeholder="{{environment.baseUrl}}/records/{{dataset.current.id}}" />
          </label>
        </div>
        <p class="field-hint">{{ t('wizard.urlHint') }}</p>
      </div>

      <div v-else-if="step === 'environment'" class="stack">
        <label class="field">
          <span class="field-label">{{ t('environment.title') }}</span>
          <select v-model="environmentId" class="select">
            <option value="">{{ t('common.none') }}</option>
            <option v-for="env in environments" :key="env.id" :value="env.id">{{ env.name }}</option>
          </select>
        </label>
        <p v-if="environment?.isProduction" class="response-notice">
          <Icon name="shield-alert" />{{ t('environment.productionWarning') }}
        </p>
        <p v-if="environment?.baseUrl" class="field-hint mono" dir="ltr">{{ environment.baseUrl }}</p>
      </div>

      <div v-else-if="step === 'send'" class="stack">
        <p class="section-help">{{ t('wizard.sendBody') }}</p>
        <a class="btn btn-secondary" :href="`/projects/${projectId}/request`">
          <Icon name="send" />{{ t('request.title') }}
        </a>
      </div>

      <div v-else-if="step === 'baseline'" class="stack">
        <label v-if="baselines.length" class="field">
          <span class="field-label">{{ t('wizard.existingBaseline') }}</span>
          <select v-model="baselineId" class="select">
            <option value="">{{ t('common.none') }}</option>
            <option v-for="item in baselines" :key="item.id" :value="item.id">{{ item.name }}</option>
          </select>
        </label>
        <div v-if="!baselineId" class="stack">
          <p class="section-help">{{ t('wizard.defineHelp') }}</p>
          <label class="field">
            <span class="field-label">{{ t('wizard.baselineName') }}</span>
            <input v-model="baselineName" class="input" dir="auto"
                   :placeholder="t('wizard.baselineNamePlaceholder')" />
          </label>
          <p class="field-hint mono" dir="ltr">{{ method }} {{ url }}</p>
          <button type="button" class="btn btn-primary btn-sm"
                  :disabled="busy || !baselineName.trim() || !url.trim()" @click="defineBaseline">
            <Icon name="target" />{{ t('wizard.defineBaseline') }}
          </button>
        </div>
      </div>

      <div v-else-if="step === 'data'" class="stack">
        <label v-if="dataSets.length" class="field">
          <span class="field-label">{{ t('wizard.existingDataSet') }}</span>
          <select v-model="dataSetId" class="select">
            <option value="">{{ t('wizard.pasteInstead') }}</option>
            <option v-for="set in dataSets" :key="set.id" :value="set.id">
              {{ set.name }} — {{ t('dataset.rowsShort', set.rowCount) }}
            </option>
          </select>
        </label>

        <template v-if="!dataSetId && canManage">
          <textarea v-model="paste" class="textarea input-mono" rows="6" dir="ltr"
                    :placeholder="t('dataset.pastePlaceholder')" @input="preview = null"></textarea>

          <div class="row wrap">
            <button type="button" class="btn btn-secondary btn-sm" :disabled="busy || !paste.trim()" @click="parse">
              <Icon name="wand-sparkles" />{{ t('dataset.read') }}
            </button>

            <label v-if="preview" class="field field-inline">
              <span class="field-label">{{ t('dataset.keyColumn') }}</span>
              <select v-model="keyColumn" class="select">
                <option v-for="column in preview.columns" :key="column" :value="column">{{ column }}</option>
              </select>
            </label>
          </div>

          <div v-if="preview" class="stack-2">
            <p class="row wrap">
              <span class="badge badge-accent">{{ t(`dataset.format.${preview.format}`) }}</span>
              <span class="text-xs subtle">
                {{ t('dataset.previewSummary', preview.rows.length, preview.columns.length) }}
              </span>
            </p>
            <button type="button" class="btn btn-primary btn-sm" :disabled="busy || !preview.rows.length"
                    @click="createDataSet">
              <Icon name="table-2" />{{ t('wizard.createDataSet', preview.rows.length) }}
            </button>
          </div>
        </template>
      </div>

      <div v-else-if="step === 'sweep'" class="stack">
        <p class="section-help">{{ t('wizard.sweepBody', rowsToSweep) }}</p>
        <button type="button" class="btn btn-primary"
                :disabled="busy || !canRun || !versionToSweep || !baselineId"
                @click="runSweep">
          <Icon name="play" />{{ busy ? t('wizard.sweeping') : t('wizard.startSweep') }}
        </button>
        <p v-if="!canRun" class="response-notice"><Icon name="lock" />{{ t('error.403body') }}</p>
      </div>

      <div v-else-if="step === 'review'" class="stack">
        <div v-if="sweep" class="row wrap">
          <span class="badge badge-idle"><span class="tabular">{{ sweep.totalRows }}</span> {{ t('dataset.rows') }}</span>
          <span v-if="sweep.differing" class="badge badge-warn">
            <span class="tabular">{{ sweep.differing }}</span> {{ t('diff.kind.Changed') }}
          </span>
          <span v-if="sweep.failed" class="badge badge-fail">
            <span class="tabular">{{ sweep.failed }}</span> {{ t('capture.status.Failed') }}
          </span>
        </div>
        <a v-if="sweep" class="btn btn-primary" :href="sweep.url">
          <Icon name="inbox" />{{ t('wizard.openQueue') }}
        </a>
        <a v-else-if="sessionId" class="btn btn-primary"
           :href="`/projects/${projectId}/captures/${sessionId}`">
          <Icon name="inbox" />{{ t('wizard.openQueue') }}
        </a>
        <p v-else class="response-notice">
          <Icon name="info" />{{ t('wizard.noSweepYet') }}
        </p>
      </div>

      <div v-else-if="step === 'rules'" class="stack">
        <p class="section-help">{{ t('wizard.rulesBody') }}</p>
        <a v-if="baselineId" class="btn btn-secondary" :href="`/projects/${projectId}/baselines/${baselineId}`">
          <Icon name="filter" />{{ t('baseline.rulesTab') }}
        </a>
      </div>

      <div v-else class="stack">
        <div class="empty empty-inline">
          <div class="empty-art"><Icon name="circle-check" /></div>
          <h3 class="empty-title">{{ t('wizard.doneTitle') }}</h3>
          <p class="empty-body">{{ t('wizard.doneBody') }}</p>
        </div>
      </div>

      <footer class="wizard-foot">
        <button type="button" class="btn btn-ghost" :disabled="index === 0" @click="back">
          <Icon name="arrow-left" class="icon-forward" />{{ t('action.back') }}
        </button>

        <span class="grow"></span>

        <!-- Leaving is a first-class action, and it says what happens to the work. -->
        <button type="button" class="btn btn-ghost btn-sm" @click="leave">
          {{ t('wizard.leave') }}
        </button>

        <button v-if="step !== 'done'" type="button" class="btn btn-primary" :disabled="!ready" @click="next">
          {{ t('action.next') }}<Icon name="arrow-right" class="icon-forward" />
        </button>
        <button v-else type="button" class="btn btn-primary" @click="finish">
          <Icon name="check" />{{ t('action.finish') }}
        </button>
      </footer>
    </section>
  </div>
</template>
