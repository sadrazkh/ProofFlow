<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, ref } from 'vue';
import ReviewQueue from './ReviewQueue.vue';
import { api, ApiError } from '../lib/api';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';

/**
 * The button, and what it found.
 *
 * This is the whole point of the endpoint page: one press that sends the request across every
 * input and says how many answers still match. Before it existed, doing this meant knowing that
 * «the endpoint» was a baseline, that the inputs were a data set, and that pressing the button
 * meant starting a capture session in regression mode from a third page.
 *
 * The counts come back from one call, and the queue below them is the same component the review
 * page used — a row per input, and the diff for whichever one is under the cursor. There was no
 * reason to write a second list that shows the same thing slightly differently.
 */

const props = defineProps<{
  projectId: string;
  endpointId: string;

  /** How many rows the chosen set has. Zero means «no inputs», which the button says out loud. */
  inputCount: number;

  dataSetName?: string | null;
  canRun: boolean;
  canReview: boolean;

  /** The last test, if there was one, so the page opens on its results instead of on a button. */
  lastSessionId?: string | null;

  environments: { id: string; name: string; isProduction: boolean }[];
  defaultEnvironmentId?: string | null;
}>();

type TestResult = {
  sessionId: string;
  totalRows: number;
  completed: number;
  differing: number;
  failed: number;

  /** Rows with no approved answer. Compared against nothing, so not a pass. */
  unmatched: number;

  status: string;
  stoppedReason: string | null;
};

const running = ref(false);
const result = ref<TestResult | null>(null);
const sessionId = ref<string | null>(props.lastSessionId ?? null);
const environmentId = ref<string>(props.defaultEnvironmentId ?? '');

/**
 * Stop after this many inputs.
 *
 * Offered rather than assumed, and empty by default. The first sweep of a two-thousand-row set is
 * usually a mistake somebody would like to find after ten rows instead of after twenty minutes of
 * real calls to a real API — but silently capping it would be worse, because a run that says
 * «10 passed» when there are two thousand inputs is a run that has lied about what it proved.
 */
const limit = ref<string>('');

const passed = computed(() => {
  if (!result.value) return 0;
  const { completed, differing, failed, unmatched } = result.value;
  return Math.max(0, completed - differing - failed - unmatched);
});

const clean = computed(() =>
  result.value !== null
  && result.value.differing === 0
  && result.value.failed === 0
  && result.value.unmatched === 0);

const base = computed(() =>
  sessionId.value
    ? `/projects/${props.projectId}/endpoints/${props.endpointId}/tests/${sessionId.value}`
    : null);

const chosen = computed(() => props.environments.find((e) => e.id === environmentId.value) ?? null);

async function run(): Promise<void> {
  if (!props.canRun || running.value) return;

  running.value = true;
  result.value = null;

  const parsed = Number.parseInt(limit.value, 10);

  try {
    const answer = await api.post<TestResult>(
      `/projects/${props.projectId}/endpoints/${props.endpointId}/test`,
      {
        environmentId: environmentId.value || null,
        limit: Number.isFinite(parsed) && parsed > 0 ? parsed : null,
      });

    result.value = answer;

    // Remounts the queue: a new session id means a different set of samples, and reusing the old
    // component would show the previous test's rows under this test's numbers.
    sessionId.value = answer.sessionId;

    const matched = answer.differing === 0 && answer.failed === 0 && answer.unmatched === 0;

    toast(
      matched
        ? t('endpoint.test.allMatched', answer.completed)
        : t('endpoint.test.found', answer.differing + answer.failed + answer.unmatched),
      matched ? 'success' : 'warn');
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    running.value = false;
  }
}
</script>

<template>
  <div class="endpoint-test stack">
    <div class="card card-pad">
      <div class="row wrap endpoint-test-bar">
        <button
          type="button"
          class="btn btn-primary"
          :disabled="!canRun || running || inputCount === 0"
          @click="run"
        >
          <Icon :name="running ? 'loader-circle' : 'play'" :class="running ? 'is-spinning' : ''" />
          {{ running ? t('endpoint.test.running') : t('endpoint.test.run') }}
        </button>

        <!-- What it is about to do, in words, before it is pressed. -->
        <p v-if="inputCount > 0" class="field-hint" style="margin: 0;">
          {{ t('endpoint.test.will', inputCount, dataSetName ?? '') }}
        </p>
        <p v-else class="field-hint" style="margin: 0;">{{ t('endpoint.test.noInputs') }}</p>

        <span class="grow"></span>

        <label v-if="environments.length > 0" class="row" style="gap: var(--space-2);">
          <span class="text-xs subtle">{{ t('environment.title') }}</span>
          <select v-model="environmentId" class="select select-sm" :disabled="running">
            <option value="">{{ t('common.none') }}</option>
            <option v-for="environment in environments" :key="environment.id" :value="environment.id">
              {{ environment.name }}
            </option>
          </select>
        </label>

        <label class="row" style="gap: var(--space-2);">
          <span class="text-xs subtle">{{ t('endpoint.test.limit') }}</span>
          <input
            v-model="limit"
            class="input input-sm"
            type="number"
            min="1"
            inputmode="numeric"
            style="inline-size: 88px;"
            :placeholder="t('endpoint.test.all')"
            :disabled="running"
          />
        </label>
      </div>

      <!-- Said before the request goes out, not after. A production environment is a decision. -->
      <p v-if="chosen?.isProduction" class="endpoint-warning" style="margin-block-start: var(--space-3);">
        <Icon name="triangle-alert" />{{ t('endpoint.test.production', chosen.name) }}
      </p>

      <div v-if="result" class="endpoint-test-result">
        <span v-if="clean" class="status status-pass">
          <span class="status-dot" aria-hidden="true"></span>
          {{ t('endpoint.result.allPassed', result.completed) }}
        </span>

        <template v-else>
          <span v-if="passed > 0" class="status status-pass">
            <span class="status-dot" aria-hidden="true"></span>
            <span class="tabular">{{ passed }}</span>&nbsp;{{ t('endpoint.result.passed') }}
          </span>
          <span v-if="result.differing > 0" class="status status-warn">
            <span class="status-dot" aria-hidden="true"></span>
            <span class="tabular">{{ result.differing }}</span>&nbsp;{{ t('endpoint.result.differ') }}
          </span>
          <span v-if="result.failed > 0" class="status status-fail">
            <span class="status-dot" aria-hidden="true"></span>
            <span class="tabular">{{ result.failed }}</span>&nbsp;{{ t('endpoint.result.failed') }}
          </span>

          <!-- Never folded into «passed». These rows were compared against nothing: there is no
               approved answer for them yet, which is what the first test of a new set looks like
               and is the opposite of a green result. -->
          <span v-if="result.unmatched > 0" class="status status-idle">
            <span class="status-dot" aria-hidden="true"></span>
            <span class="tabular">{{ result.unmatched }}</span>&nbsp;{{ t('endpoint.result.unchecked') }}
          </span>
        </template>

        <!-- Never silent about a sweep that stopped early: a partial result that looks whole is
             the one failure this screen must not produce. -->
        <span v-if="result.stoppedReason" class="badge badge-warn">
          <Icon name="circle-alert" />{{ result.stoppedReason }}
        </span>
        <span v-else-if="result.completed < result.totalRows" class="badge badge-warn">
          <Icon name="circle-alert" />
          {{ t('endpoint.result.partial', result.completed, result.totalRows) }}
        </span>
      </div>
    </div>

    <!-- The queue, keyed on the session so a new test replaces it rather than appending to it. -->
    <ReviewQueue v-if="base" :key="base" :base="base" :can-review="canReview" />
  </div>
</template>
