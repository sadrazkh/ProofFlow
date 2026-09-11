<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef } from 'vue';
import { Icon } from '../lib/Icon';
import { t } from '../lib/i18n';
import { api } from '../lib/api';

/**
 * What happened when somebody pressed «check everything».
 *
 * A row per endpoint, filling in as the worker gets to each one. It is a page rather than a badge
 * on the endpoint list because the list is paged and the answer is not: forty checks started
 * together are one event, and «how did that go» is asked once, about all of them.
 *
 * Every row links to its endpoint, which is where the diff, the review queue and the history
 * already live. Nothing about a check is only visible here.
 */

const props = defineProps<{
  projectId: string;
  batchId: string;
}>();

/** How often to re-read while checks are still landing. */
const POLL_MS = 2000;

type Row = {
  sessionId: string;
  baselineId: string;
  name: string;
  method: string;
  url: string;
  environmentName: string | null;
  status: string;
  totalRows: number;
  completed: number;
  differing: number;
  failed: number;
  unmatched: number;
  slow: number;
  stoppedReason: string | null;
};

type Batch = {
  id: string;
  total: number;
  settled: boolean;
  rows: Row[];
};

const batch = shallowRef<Batch | null>(null);
const loading = ref(true);

let poll: number | undefined;

const rows = computed(() => batch.value?.rows ?? []);

const done = computed(() =>
  rows.value.filter((row) => row.status !== 'Queued' && row.status !== 'Running').length);

/** Passed is what is left over, never a stored number — the same rule the endpoint page follows. */
function passed(row: Row): number {
  return Math.max(0, row.completed - row.differing - row.failed - row.unmatched - row.slow);
}

function wrong(row: Row): number {
  return row.differing + row.failed + row.unmatched + row.slow;
}

function running(row: Row): boolean {
  return row.status === 'Queued' || row.status === 'Running';
}

const clean = computed(() =>
  batch.value?.settled === true && rows.value.every((row) => wrong(row) === 0));

/** How many endpoints have something to look at. The headline number when it is not zero. */
const needLooking = computed(() =>
  rows.value.filter((row) => !running(row) && wrong(row) > 0).length);

async function refresh(): Promise<void> {
  batch.value = await api.get<Batch>(`/projects/${props.projectId}/checks/${props.batchId}/state`);

  if (batch.value.settled) stop();
}

function stop(): void {
  if (poll !== undefined) window.clearInterval(poll);
  poll = undefined;
}

onMounted(async () => {
  try {
    await refresh();
  } finally {
    loading.value = false;
  }

  if (batch.value?.settled) return;

  poll = window.setInterval(() => {
    // A batch that cannot be read is not a batch that failed. Stop asking rather than repeating
    // the same error every two seconds.
    void refresh().catch(() => stop());
  }, POLL_MS);
});

onUnmounted(stop);
</script>

<template>
  <div class="card">
    <div class="card-header">
      <div>
        <div class="card-title">
          <template v-if="loading">{{ t('app.loading') }}</template>
          <template v-else-if="!batch?.settled">
            {{ t('checks.progress', done, batch?.total ?? 0) }}
          </template>
          <template v-else-if="clean">{{ t('checks.allWell', rows.length) }}</template>
          <template v-else>{{ t('checks.needLooking', needLooking) }}</template>
        </div>
        <div class="card-subtitle">{{ t('checks.eachRow') }}</div>
      </div>

      <span v-if="!loading && !batch?.settled" class="status status-idle">
        <Icon name="loader-circle" class="is-spinning" />
        {{ t('checks.working') }}
      </span>
    </div>

    <div v-if="!loading && rows.length > 0" class="table-wrap">
      <table class="table table-hover">
        <caption class="sr-only">{{ t('checks.title') }}</caption>
        <thead>
          <tr>
            <th>{{ t('common.name') }}</th>
            <th>{{ t('endpoint.request') }}</th>
            <th>{{ t('environment.title') }}</th>
            <th>{{ t('endpoint.lastResult') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.sessionId">
            <td>
              <a :href="`/projects/${projectId}/endpoints/${row.baselineId}`" dir="auto">{{ row.name }}</a>
            </td>
            <td>
              <!-- Forced left-to-right: a path is not a sentence, and in Persian «GET /orders»
                   otherwise renders with the method on the wrong end. -->
              <span class="endpoint-request" dir="ltr">
                <span class="method-chip">{{ row.method }}</span>
                <code class="endpoint-path truncate">{{ row.url }}</code>
              </span>
            </td>
            <td>
              <span v-if="row.environmentName" dir="auto">{{ row.environmentName }}</span>
              <span v-else class="subtle">{{ t('common.none') }}</span>
            </td>
            <td>
              <span v-if="running(row)" class="status status-idle">
                <Icon name="loader-circle" class="is-spinning" />
                {{
                  row.status === 'Queued'
                    ? t('endpoint.test.queued')
                    : t('endpoint.test.progress', row.completed, row.totalRows)
                }}
              </span>

              <template v-else>
                <!-- Stopped early is said before any counter, because a partial result that looks
                     whole is the one thing this table must not produce. -->
                <span v-if="row.stoppedReason" class="badge badge-warn">
                  <Icon name="circle-alert" />{{ row.stoppedReason }}
                </span>

                <template v-else-if="wrong(row) === 0">
                  <span class="status status-pass">
                    <span class="status-dot" aria-hidden="true"></span>
                    {{ t('endpoint.result.allPassed', row.completed) }}
                  </span>
                </template>

                <template v-else>
                  <span v-if="passed(row) > 0" class="status status-pass">
                    <span class="status-dot" aria-hidden="true"></span>
                    <span class="tabular">{{ passed(row) }}</span>&nbsp;{{ t('endpoint.result.passed') }}
                  </span>
                  <span v-if="row.differing > 0" class="status status-warn">
                    <span class="status-dot" aria-hidden="true"></span>
                    <span class="tabular">{{ row.differing }}</span>&nbsp;{{ t('endpoint.result.differ') }}
                  </span>
                  <span v-if="row.failed > 0" class="status status-fail">
                    <span class="status-dot" aria-hidden="true"></span>
                    <span class="tabular">{{ row.failed }}</span>&nbsp;{{ t('endpoint.result.failed') }}
                  </span>
                  <span v-if="row.slow > 0" class="status status-warn">
                    <span class="status-dot" aria-hidden="true"></span>
                    <span class="tabular">{{ row.slow }}</span>&nbsp;{{ t('endpoint.result.slow') }}
                  </span>

                  <!-- Never folded into passed. These were compared against nothing, which is what
                       an endpoint that has never had an answer approved looks like. -->
                  <span v-if="row.unmatched > 0" class="status status-idle">
                    <span class="status-dot" aria-hidden="true"></span>
                    <span class="tabular">{{ row.unmatched }}</span>&nbsp;{{ t('endpoint.result.unchecked') }}
                  </span>
                </template>
              </template>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-else-if="!loading" class="card-body">
      <p class="subtle">{{ t('checks.none') }}</p>
    </div>
  </div>
</template>
