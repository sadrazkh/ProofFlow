<script setup lang="ts">
import { computed } from 'vue';
import { Icon } from '../lib/Icon';
import { t } from '../lib/i18n';
import type { AssertionRow, NodeRunRow } from './runTypes';

/**
 * How long each step took, in order.
 *
 * A bar per turn, its length the duration — which is the whole reason this exists rather than a
 * table of milliseconds. "Which step is slow" is a question somebody answers by looking, and a
 * column of numbers makes them read forty of them.
 *
 * Retries are drawn as separate segments on one row rather than as separate rows. "It worked on the
 * third go" is one step with a history, and splitting it into three rows loses the fact that they
 * were the same step.
 */

const props = defineProps<{
  nodes: NodeRunRow[];
  assertions: AssertionRow[];
}>();

/**
 * Asks the log to show one step.
 *
 * The bars answer "where did the time go"; the answer to "and what happened there" is in the log,
 * and making somebody type the step's name into a search box to cross between them is the kind of
 * gap that makes two panes feel like two products.
 */
const emit = defineEmits<{ (event: 'focus', nodeName: string): void }>();

type Turn = {
  key: string;
  name: string;
  nodeKey: string;
  attempts: NodeRunRow[];
  total: number;
  failed: boolean;
  iterations: number;
};

/**
 * One row per step, however many times it ran.
 *
 * Keyed by the node rather than by the turn: a loop of two thousand rows would otherwise be two
 * thousand rows in the timeline, which is a scroll bar rather than a picture.
 */
const rows = computed<Turn[]>(() => {
  const byNode = new Map<string, Turn>();

  for (const node of props.nodes) {
    let turn = byNode.get(node.nodeId);

    if (!turn) {
      turn = {
        key: node.nodeId,
        name: node.nodeName,
        nodeKey: node.nodeKey,
        attempts: [],
        total: 0,
        failed: false,
        iterations: 0,
      };

      byNode.set(node.nodeId, turn);
    }

    turn.attempts.push(node);
    turn.total += node.durationMs;
    turn.failed ||= node.status === 'Failed';
    turn.iterations = Math.max(turn.iterations, node.iteration + 1);
  }

  return [...byNode.values()];
});

/** The longest step sets the scale, so the picture always fills its width. */
const longest = computed(() => Math.max(1, ...rows.value.map((row) => row.total)));

const slowest = computed(() =>
  rows.value.length ? rows.value.reduce((a, b) => (a.total >= b.total ? a : b)) : null);

function width(ms: number): string {
  // A floor of one per cent: a step that took two milliseconds still ran, and a bar of zero width
  // reads as a step that did not.
  return `${Math.max(1, (ms / longest.value) * 100)}%`;
}

function failures(nodeRunId: string): AssertionRow[] {
  return props.assertions.filter((row) => row.nodeRunId === nodeRunId && !row.passed);
}

function duration(ms: number): string {
  return ms >= 1000 ? `${(ms / 1000).toFixed(2)}s` : `${Math.round(ms)}ms`;
}
</script>

<template>
  <section class="run-timeline" aria-labelledby="run-timeline-heading">
    <header class="run-timeline-bar">
      <h3 id="run-timeline-heading" class="text-sm">{{ t('run.timeline') }}</h3>

      <span v-if="slowest" class="text-xs subtle">
        {{ t('run.timeline.slowest', slowest.name, duration(slowest.total)) }}
      </span>
    </header>

    <p v-if="!rows.length" class="run-timeline-empty text-sm subtle">{{ t('run.timeline.empty') }}</p>

    <ol v-else class="run-timeline-rows">
      <li v-for="row in rows" :key="row.key">
        <button
          type="button"
          class="run-timeline-row"
          :aria-label="t('run.timeline.show', row.name)"
          @click="emit('focus', row.name)"
        >
          <span class="run-timeline-name" dir="auto" :title="row.nodeKey">
          {{ row.name }}
          <span v-if="row.iterations > 1" class="badge badge-idle tabular" dir="ltr">
            ×{{ row.iterations }}
          </span>
        </span>

          <span class="run-timeline-track">
            <!--
              One segment per attempt. A retried step shows its failed tries and then the one that
              worked, which is the difference between "fast" and "fast on the third go".
            -->
            <span
              v-for="attempt in row.attempts"
              :key="attempt.id"
              class="run-timeline-segment"
              :class="[`state-${attempt.status.toLowerCase()}`, { 'is-retry': attempt.attempt > 1 }]"
              :style="{ width: width(attempt.durationMs) }"
              :title="`${attempt.nodeName} · ${duration(attempt.durationMs)}`"
            ></span>
          </span>

          <span class="run-timeline-figure tabular" dir="ltr">{{ duration(row.total) }}</span>

          <span v-if="row.failed" class="run-timeline-verdict">
            <Icon name="circle-x" :size="14" />
            <span class="sr-only">{{ t('run.state.failed') }}</span>
          </span>
        </button>
      </li>
    </ol>

    <!--
      The failed checks, spelled out. The bars say where the run spent its time; this says what it
      found — and a person opening a failed run is looking for the second of those.
    -->
    <div v-if="assertions.some((a) => !a.passed)" class="run-failures">
      <h4 class="text-sm">{{ t('run.failedChecks') }}</h4>

      <ul class="run-failure-list">
        <li v-for="row in nodes" :key="`f-${row.id}`">
          <template v-for="check in failures(row.id)" :key="check.description">
            <div class="run-failure">
              <span class="badge" :class="check.soft ? 'badge-warn' : 'badge-fail'">
                {{ check.soft ? t('run.soft') : t('run.hard') }}
              </span>
              <span class="run-failure-step" dir="auto">{{ row.nodeName }}</span>
              <span class="run-failure-text" dir="auto">{{ check.description }}</span>
              <span v-if="check.expected !== null" class="run-failure-values mono" dir="ltr">
                {{ check.expected }} → {{ check.actual ?? '—' }}
              </span>
            </div>
          </template>
        </li>
      </ul>
    </div>
  </section>
</template>
