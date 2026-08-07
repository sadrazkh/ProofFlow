<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef } from 'vue';
import DiffViewer from './DiffViewer.vue';
import SuggestionList from './SuggestionList.vue';
import { Icon } from '../lib/Icon';
import { t } from '../lib/i18n';
import { api, ApiError } from '../lib/api';
import { toast } from '../lib/toast';
import type { Comparison, Matrix, MatrixCell } from './matrixTypes';
import { isSettled } from './matrixTypes';
import { statusClass } from './runTypes';

/**
 * The same tests, at the same moment, in more than one place.
 *
 * The grid is the phase's whole idea: a row per scenario, a column per environment, and a cell that
 * says what happened there. Reading across a row answers the question the product exists for —
 * "it works here and not there" — which no amount of reading one run at a time will.
 *
 * A cell is a link to an ordinary run console. That is not a shortcut; it is the reason the batch
 * is only a grouping. Everything a person needs after "this one failed" already exists.
 */

const props = defineProps<{
  projectId: string;
  batchId: string;
}>();

/** How often the grid re-reads while cells are still landing. */
const POLL_MS = 2000;

const grid = shallowRef<Matrix | null>(null);
const loading = ref(true);

const comparing = ref(false);
const comparison = shallowRef<Comparison | null>(null);

/** Which scenario is being compared, and between which two columns. */
const row = ref<string | null>(null);
const left = ref<string | null>(null);
const right = ref<string | null>(null);

let poll: number | undefined;

const columns = computed(() => grid.value?.columns ?? []);

const settled = computed(() => !!grid.value && isSettled(grid.value.state));

/** Comparison needs two environments to compare. One column is a list, not a comparison. */
const comparable = computed(() => columns.value.length >= 2);

const progress = computed(() => {
  if (!grid.value) return '';
  return `${grid.value.done}/${grid.value.total}`;
});

onMounted(async () => {
  await refresh();
  loading.value = false;

  if (!settled.value) poll = window.setInterval(() => void refresh(), POLL_MS);

  if (comparable.value) {
    left.value = columns.value[0]?.environmentId ?? null;
    right.value = columns.value[1]?.environmentId ?? null;
  }
});

onUnmounted(() => window.clearInterval(poll));

async function refresh(): Promise<void> {
  try {
    grid.value = await api.get<Matrix>(
      `/projects/${props.projectId}/matrix/${props.batchId}/state`);

    if (settled.value) window.clearInterval(poll);
  } catch (error) {
    window.clearInterval(poll);
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  }
}

async function compare(scenarioId: string): Promise<void> {
  if (!left.value || !right.value || left.value === right.value) return;

  row.value = scenarioId;
  comparing.value = true;
  comparison.value = null;

  try {
    comparison.value = await api.get<Comparison>(
      `/projects/${props.projectId}/matrix/${props.batchId}/compare`
      + `?scenarioId=${scenarioId}&left=${left.value}&right=${right.value}`);
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    comparing.value = false;
  }
}

function close(): void {
  comparison.value = null;
  row.value = null;
}

function duration(ms: number): string {
  return ms >= 1000 ? `${(ms / 1000).toFixed(1)}s` : `${Math.round(ms)}ms`;
}

function cellUrl(cell: MatrixCell): string {
  return `/projects/${props.projectId}/runs/${cell.runId}`;
}

/** The scenario whose comparison is open, for the panel's heading. */
const openRow = computed(() =>
  grid.value?.rows.find((candidate) => candidate.scenarioId === row.value) ?? null);
</script>

<template>
  <div class="matrix">
    <header class="matrix-head">
      <span v-if="grid" class="badge" :class="statusClass(grid.state === 'Queued' ? 'Queued'
        : grid.state === 'Running' ? 'Running' : grid.state === 'Passed' ? 'Passed' : 'Failed')">
        <span class="status-dot" aria-hidden="true"></span>
        {{ t(`matrix.state.${grid.state.toLowerCase()}`) }}
      </span>

      <span class="text-sm subtle">
        {{ t('matrix.progress') }}
        <span class="tabular" dir="ltr">{{ progress }}</span>
      </span>

      <span class="grow"></span>

      <!--
        Which two columns to compare. Offered here rather than per row: comparing staging with
        production is one decision somebody makes about the whole grid, not once per scenario.
      -->
      <template v-if="comparable">
        <label class="text-sm subtle" for="matrix-left">{{ t('matrix.compare') }}</label>
        <select id="matrix-left" v-model="left" class="select">
          <option v-for="column in columns" :key="`l-${column.environmentId}`"
                  :value="column.environmentId">{{ column.name }}</option>
        </select>

        <Icon name="arrow-right" :size="14" />

        <select v-model="right" class="select" :aria-label="t('matrix.compare')">
          <option v-for="column in columns" :key="`r-${column.environmentId}`"
                  :value="column.environmentId">{{ column.name }}</option>
        </select>
      </template>
    </header>

    <div v-if="loading" class="skeleton matrix-skeleton" role="status"
         :aria-label="t('app.loading')"></div>

    <div v-else-if="!grid || !grid.rows.length" class="empty">
      <div class="empty-art"><Icon name="layout-grid" /></div>
      <h2 class="empty-title">{{ t('matrix.empty.title') }}</h2>
      <p class="empty-body">{{ t('matrix.empty.body') }}</p>
    </div>

    <template v-else>
      <div class="table-wrap">
        <table class="table matrix-table">
          <caption class="sr-only">{{ t('nav.matrix') }}</caption>
          <thead>
            <tr>
              <th scope="col">{{ t('matrix.column.scenario') }}</th>
              <th v-for="column in columns" :key="column.environmentId" scope="col">
                <span class="matrix-column">
                  {{ column.name }}
                  <!--
                    Production is marked, always. Reading a green cell in the wrong column is how
                    somebody concludes a release is safe.
                  -->
                  <span v-if="column.isProduction" class="matrix-live" :title="t('matrix.production')">
                    <Icon name="shield-alert" :size="14" />
                    <span class="sr-only">{{ t('matrix.production') }}</span>
                  </span>
                </span>
              </th>
              <th v-if="comparable" scope="col"><span class="sr-only">{{ t('matrix.compare') }}</span></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="line in grid.rows" :key="line.scenarioId"
                :class="{ 'is-open': line.scenarioId === row }">
              <th scope="row" dir="auto">{{ line.name }}</th>

              <td v-for="(cell, index) in line.cells" :key="`${line.scenarioId}-${index}`">
                <!--
                  A hole, not a zero. A cell with no run behind it is a pairing nobody asked for,
                  and drawing anything else would be a claim about a test that never ran.
                -->
                <span v-if="!cell" class="subtle" aria-label="—">—</span>

                <a v-else :href="cellUrl(cell)" class="matrix-cell" :class="statusClass(cell.status)">
                  <span class="status-dot" aria-hidden="true"></span>
                  <span class="matrix-cell-word">{{ t(`run.status.${cell.status.toLowerCase()}`) }}</span>
                  <span class="matrix-cell-time tabular" dir="ltr">{{ duration(cell.durationMs) }}</span>
                </a>
              </td>

              <td v-if="comparable">
                <button type="button" class="btn btn-ghost btn-sm"
                        :disabled="comparing || left === right"
                        @click="compare(line.scenarioId)">
                  <Icon name="git-compare-arrows" :size="14" />{{ t('matrix.compare.open') }}
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!--
        The comparison, in the diff viewer the baseline workbench uses. A second way of showing two
        JSON documents differing would be a second colour language to learn.
      -->
      <section v-if="row" class="matrix-comparison" aria-labelledby="matrix-comparison-heading">
        <header class="matrix-comparison-head">
          <h2 id="matrix-comparison-heading" class="text-sm" dir="auto">
            {{ openRow?.name }}
          </h2>

          <span class="matrix-sides">
            <span class="badge badge-idle">{{ comparison?.leftName ?? '—' }}</span>
            <Icon name="arrow-right" :size="14" />
            <span class="badge badge-idle">{{ comparison?.rightName ?? '—' }}</span>
          </span>

          <span class="grow"></span>

          <button type="button" class="btn btn-ghost btn-icon btn-sm"
                  :aria-label="t('action.close')" @click="close">
            <Icon name="x" />
          </button>
        </header>

        <div v-if="comparing" class="skeleton matrix-skeleton" role="status"
             :aria-label="t('app.loading')"></div>

        <template v-else-if="comparison">
          <p v-if="comparison.onlyLeft.length || comparison.onlyRight.length"
             class="matrix-note" role="status">
            <Icon name="triangle-alert" :size="14" />
            {{ t('matrix.onlyOneSide') }}
            <span dir="auto">{{ [...comparison.onlyLeft, ...comparison.onlyRight].join('، ') }}</span>
          </p>

          <p v-if="comparison.stepsNotShown > 0" class="matrix-note" role="status">
            <Icon name="info" :size="14" />
            {{ t('matrix.stepsNotShown', comparison.stepsNotShown) }}
          </p>

          <p v-if="!comparison.steps.length" class="text-sm subtle matrix-note">
            {{ t('matrix.noSharedSteps') }}
          </p>

          <article v-for="step in comparison.steps" :key="`${step.nodeId}-${step.iteration}`"
                   class="matrix-step">
            <header class="matrix-step-head">
              <span class="matrix-step-name" dir="auto">{{ step.nodeName }}</span>

              <span v-if="step.iteration > 0" class="badge badge-idle tabular" dir="ltr">
                #{{ step.iteration + 1 }}
              </span>

              <span class="matrix-step-codes tabular" dir="ltr">
                {{ step.leftStatus }} → {{ step.rightStatus }}
              </span>

              <span class="grow"></span>

              <span class="badge" :class="step.diff.matches ? 'badge-pass' : 'badge-fail'">
                {{ step.diff.matches ? t('matrix.same') : t('matrix.differs') }}
              </span>
            </header>

            <DiffViewer :result="step.diff" :pending="false" :can-accept="false" subject="other" />

            <!--
              What looks dynamic rather than broken. Two environments differ in ids and timestamps
              as a matter of course, and a reader who cannot tell those from a regression will stop
              reading the whole comparison.
            -->
            <SuggestionList v-if="step.suggestions.length" :suggestions="step.suggestions"
                            :accepted="[]" :readonly="true" />
          </article>
        </template>
      </section>
    </template>
  </div>
</template>
