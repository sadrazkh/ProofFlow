<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef } from 'vue';
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';
import RunGraph from './RunGraph.vue';
import RunLog from './RunLog.vue';
import RunTimeline from './RunTimeline.vue';
import { Icon } from '../lib/Icon';
import { t } from '../lib/i18n';
import { api, ApiError } from '../lib/api';
import { toast } from '../lib/toast';
import { confirmAction } from '../lib/shell';
import type { GraphDto, NodeSpecDto } from './graphTypes';
import type {
  AssertionRow, AssertionUpdate, NodeRunRow, NodeUpdate, RunEventRow, RunState, RunStatus, RunTotals,
} from './runTypes';
import { isOver, statusClass } from './runTypes';

/**
 * Watching a run.
 *
 * The state arrives twice over, and that is deliberate. One request loads whatever has already
 * happened, so the console opens the same way on a run from last month and on one that started two
 * seconds ago. A live connection then carries what happens next. Without the first, reloading
 * mid-run shows an empty page; without the second, watching means pressing refresh.
 *
 * Updates are buffered to an animation frame. A busy run pushes hundreds of lines a second, and a
 * component that re-rendered on each one would spend the run laying out text nobody has read yet.
 */

const props = defineProps<{
  projectId: string;
  runId: string;
  canCancel: boolean;

  /** The step this run began at, when it did not begin at the beginning. */
  startedFrom?: string | null;
}>();

const status = ref<RunStatus>('Queued');
const outcome = ref<string | null>(null);
const totals = ref<RunTotals>({
  steps: 0, stepsFailed: 0, assertionsPassed: 0, assertionsFailed: 0, durationMs: 0,
});

const graph = shallowRef<GraphDto | null>(null);
const specs = shallowRef<NodeSpecDto[]>([]);

const nodes = ref<NodeRunRow[]>([]);
const assertions = ref<AssertionRow[]>([]);
const lines = ref<RunEventRow[]>([]);

const connected = ref(false);
const loading = ref(true);
const cancelling = ref(false);
const rerunning = ref(false);
const tab = ref<'graph' | 'timeline'>('graph');

const log = ref<InstanceType<typeof RunLog> | null>(null);

/** The highest sequence seen, so a reconnect asks only for what it missed. */
let watermark = 0;

let connection: HubConnection | null = null;
let poll: number | undefined;

const running = computed(() => !isOver(status.value));

const elapsed = computed(() => {
  const ms = totals.value.durationMs;
  return ms >= 1000 ? `${(ms / 1000).toFixed(1)}s` : `${Math.round(ms)}ms`;
});

onMounted(async () => {
  await Promise.all([loadCatalogue(), refresh()]);
  loading.value = false;

  if (running.value) await connect();
});

onUnmounted(() => {
  window.clearInterval(poll);
  void connection?.stop();
});

async function loadCatalogue(): Promise<void> {
  try {
    specs.value = await api.get<NodeSpecDto[]>(`/projects/${props.projectId}/scenarios/catalogue`);
  } catch {
    // The graph pane degrades to a message; the log and the timeline do not need the catalogue.
    specs.value = [];
  }
}

/**
 * Reads everything since the watermark.
 *
 * By sequence rather than by offset: lines keep arriving while the request is in flight, and an
 * offset would skip or repeat exactly the ones that arrived in between.
 */
async function refresh(): Promise<void> {
  try {
    const state = await api.get<RunState>(
      `/projects/${props.projectId}/runs/${props.runId}/state?since=${watermark}`);

    status.value = state.status;
    outcome.value = state.outcome;
    totals.value = state.totals;
    nodes.value = state.nodes;
    assertions.value = state.assertions;

    if (state.graph && !graph.value) {
      try {
        graph.value = JSON.parse(state.graph) as GraphDto;
      } catch {
        graph.value = null;
      }
    }

    const newest = state.events.at(-1);

    if (newest) {
      lines.value = [...lines.value, ...state.events];
      watermark = newest.sequence;
    }
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  }
}

// ---- the live connection ------------------------------------------------------------------------

async function connect(): Promise<void> {
  connection = new HubConnectionBuilder()
    .withUrl('/hubs/runs')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  connection.on('log', (line: RunEventRow) => queue(() => {
    if (line.sequence <= watermark) return;
    watermark = line.sequence;
    lines.value.push(line);
  }));

  connection.on('node', (update: NodeUpdate) => queue(() => applyNode(update)));

  connection.on('assertion', (update: AssertionUpdate) => queue(() => {
    assertions.value.push({
      nodeRunId: update.nodeId,
      description: update.description,
      passed: update.passed,
      soft: update.soft,
      expected: null,
      actual: null,
      target: update.target,
    });
  }));

  connection.on('status', (update: { status: RunStatus; totals: RunTotals & { outcome?: string } }) =>
    queue(() => {
      status.value = update.status;
      totals.value = update.totals;
      if (update.totals.outcome !== undefined) outcome.value = update.totals.outcome ?? null;

      // One last read when it ends: the live messages carry the shape, the database carries the
      // detail — expected and actual values among it.
      if (isOver(update.status)) {
        window.clearInterval(poll);
        void refresh();
      }
    }));

  connection.onreconnected(() => {
    connected.value = true;
    // Whatever arrived while the socket was down is fetched rather than lost.
    void refresh();
  });

  connection.onclose(() => { connected.value = false; });

  try {
    await connection.start();
    await connection.invoke('Watch', props.runId);
    connected.value = true;

    // Read once more, now that we are actually listening.
    //
    // Between the read in onMounted and this line, the run is unwatched: anything it published in
    // that window went to a group this connection had not joined. Most runs are slower than the
    // handshake and never notice. A run that finishes in thirty milliseconds — which is every run
    // against a local service — finishes inside it, and without this the console sits on "Running"
    // for ever while the database says Passed. Nothing throws, nothing retries, and the page is
    // simply wrong until somebody reloads it.
    await refresh();
  } catch {
    // A blocked WebSocket is somebody else's proxy, not a broken run. Polling is slower and works.
    connected.value = false;
    poll = window.setInterval(() => void refresh(), 2000);
  }
}

/**
 * Applies a node update, keeping one row per turn.
 *
 * A turn is (node, iteration, attempt): the same step inside a loop is a new row, and the same step
 * being retried is a new row — because both are things the timeline has to draw separately.
 */
function applyNode(update: NodeUpdate): void {
  const at = nodes.value.findIndex((row) =>
    row.nodeId === update.nodeId
    && row.iteration === update.iteration
    && row.attempt === update.attempt);

  const existing = at >= 0 ? nodes.value[at] : undefined;

  const row: NodeRunRow = {
    id: `${update.nodeId}:${update.iteration}:${update.attempt}`,
    nodeId: update.nodeId,
    nodeName: update.nodeName,

    // Carried over from the row this replaces: the live message says what changed, not everything
    // the row knows, and the key and start time were settled when the step began.
    nodeKey: existing?.nodeKey ?? '',
    status: update.status,
    iteration: update.iteration,
    attempt: update.attempt,
    durationMs: update.durationMs,
    takenPort: update.takenPort,
    failureMessage: update.failure,
    startedAt: existing?.startedAt ?? new Date().toISOString(),
  };

  if (existing) nodes.value[at] = row;
  else nodes.value.push(row);
}

// ---- buffering ------------------------------------------------------------------------------------

let pending: (() => void)[] = [];
let frame = 0;

/**
 * Holds an update until the next frame.
 *
 * Sixty renders a second is the most a screen can show. A run that pushes six hundred messages a
 * second without this spends its time in layout, and the console becomes slower exactly when there
 * is most to watch.
 */
function queue(apply: () => void): void {
  pending.push(apply);
  if (frame) return;

  frame = window.requestAnimationFrame(() => {
    frame = 0;
    const batch = pending;
    pending = [];
    for (const apply of batch) apply();
  });
}

// ---- stopping ---------------------------------------------------------------------------------

async function cancel(): Promise<void> {
  // Confirmed, because a run halfway through a data set has done real work against a real API and
  // stopping it is not free.
  const sure = await confirmAction({
    title: t('run.cancel.title'),
    body: t('run.cancel.body'),
    confirm: t('run.cancel.confirm'),
  });

  if (!sure) return;

  cancelling.value = true;

  try {
    await api.post(`/projects/${props.projectId}/runs/${props.runId}/cancel`, {});
    toast(t('run.cancel.asked'), 'info');
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    cancelling.value = false;
  }
}

/**
 * A link somebody without an account can open.
 *
 * Minting is a deliberate act, so the first press asks — what the link shows is a summary, but it
 * is still this project's name and this test's verdict leaving the building. A second press takes
 * it back, and the old address then answers nothing.
 */
const shareUrl = ref<string | null>(null);
const sharing = ref(false);

async function share(): Promise<void> {
  if (sharing.value) return;

  if (shareUrl.value === null) {
    const sure = await confirmAction({
      title: t('share.title'),
      body: t('share.body'),
      confirm: t('share.confirm'),

      // Making a link is not a destructive act. The red button is reserved for the presses that
      // take something away — including, one press later, this one.
      tone: 'primary',
    });

    if (!sure) return;
  }

  sharing.value = true;

  try {
    const revoke = shareUrl.value !== null;

    const answer = await api.post<{ shared: boolean; url?: string }>(
      `/projects/${props.projectId}/runs/${props.runId}/share?revoke=${revoke}`, {});

    if (answer.shared && answer.url) {
      shareUrl.value = answer.url;
      await navigator.clipboard.writeText(answer.url).catch(() => undefined);
      toast(t('share.made'), 'success');
    } else {
      shareUrl.value = null;
      toast(t('share.revoked'), 'info');
    }
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    sharing.value = false;
  }
}

/**
 * The same run, started again — same environment, same inputs, the scenario as it is today.
 *
 * No confirmation, deliberately: it does exactly what the button that started this run did, and
 * that one does not ask either.
 */
async function again(): Promise<void> {
  if (rerunning.value) return;
  rerunning.value = true;

  try {
    const started = await api.post<{ url: string }>(
      `/projects/${props.projectId}/runs/${props.runId}/again`, {});

    location.assign(started.url);
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
    rerunning.value = false;
  }
}
</script>

<template>
  <div class="run-console">
    <header class="run-head">
      <span class="badge" :class="statusClass(status)">
        <span class="status-dot" aria-hidden="true"></span>
        {{ t(`run.status.${status.toLowerCase()}`) }}
      </span>

      <!--
        Counts, not just a word. "Passed" and "passed, 40 of 41 checks" are different facts, and the
        second is the one that shows a soft assertion quietly failing.
      -->
      <dl class="run-figures">
        <div>
          <dt>{{ t('run.steps') }}</dt>
          <dd class="tabular" dir="ltr">{{ totals.steps }}</dd>
        </div>
        <div>
          <dt>{{ t('run.checksPassed') }}</dt>
          <dd class="tabular" dir="ltr">{{ totals.assertionsPassed }}</dd>
        </div>
        <div :class="{ 'is-bad': totals.assertionsFailed > 0 }">
          <dt>{{ t('run.checksFailed') }}</dt>
          <dd class="tabular" dir="ltr">{{ totals.assertionsFailed }}</dd>
        </div>
        <div>
          <dt>{{ t('run.elapsed') }}</dt>
          <dd class="tabular" dir="ltr">{{ elapsed }}</dd>
        </div>
      </dl>

      <!--
        Said on the page, not only in the record. Three steps in a scenario of nine reads as a run
        that fell over unless something says the earlier ones were never asked to run.
      -->
      <span v-if="startedFrom" class="badge badge-idle run-partial">
        <Icon name="circle-play" />{{ t('run.startedFrom', startedFrom) }}
      </span>

      <span class="grow"></span>

      <span v-if="running" class="run-live text-xs subtle"
            :class="connected ? 'status-running' : 'status-idle'">
        <span class="status-dot" aria-hidden="true"></span>
        {{ connected ? t('run.live') : t('run.polling') }}
      </span>

      <button
        v-if="canCancel && running"
        type="button"
        class="btn btn-danger btn-sm"
        :disabled="cancelling"
        @click="cancel"
      >
        <Icon name="square" />{{ t('run.cancel') }}
      </button>

      <!-- The same capability as starting one: canCancel is «may run tests», not «may stop». -->
      <button
        v-if="canCancel && !running"
        type="button"
        class="btn btn-secondary btn-sm"
        :disabled="rerunning"
        @click="again"
      >
        <Icon :name="rerunning ? 'loader-circle' : 'rotate-ccw'"
              :class="rerunning ? 'is-spinning' : ''" />
        {{ t('run.again') }}
      </button>

      <button
        v-if="canCancel && !running"
        type="button"
        class="btn btn-ghost btn-sm"
        :disabled="sharing"
        @click="share"
      >
        <Icon :name="shareUrl ? 'circle-slash' : 'external-link'" />
        {{ shareUrl ? t('share.revoke') : t('share.action') }}
      </button>
    </header>

    <!-- Shown until the page is left: the address is minted once and this is the only place it
         appears, so a copy that silently failed must not be the only record of it. -->
    <p v-if="shareUrl" class="run-shared" role="status">
      <Icon name="external-link" :size="15" />
      <code dir="ltr">{{ shareUrl }}</code>
      <span class="text-xs subtle">{{ t('share.what') }}</span>
    </p>

    <p v-if="outcome" class="run-outcome" :class="{ 'is-bad': status === 'Failed' || status === 'Errored' }"
       dir="auto" role="status">
      {{ outcome }}
    </p>

    <div v-if="loading" class="skeleton run-skeleton" role="status" :aria-label="t('app.loading')"></div>

    <template v-else>
      <!--
        Toggle buttons rather than tabs. Both panes are on the page at once — the log never hides —
        so calling these tabs would promise a panel swap that does not happen, and aria-pressed says
        what is actually true.
      -->
      <div class="segmented run-tabs" role="group" :aria-label="t('run.view')">
        <button type="button" :aria-pressed="tab === 'graph'" @click="tab = 'graph'">
          <Icon name="workflow" :size="14" />{{ t('run.graph') }}
        </button>
        <button type="button" :aria-pressed="tab === 'timeline'" @click="tab = 'timeline'">
          <Icon name="chart-no-axes-gantt" :size="14" />{{ t('run.timeline') }}
        </button>
      </div>

      <div class="run-panes">
        <RunGraph v-show="tab === 'graph'" :graph="graph" :specs="specs" :states="nodes" />
        <RunTimeline
          v-show="tab === 'timeline'"
          :nodes="nodes"
          :assertions="assertions"
          @focus="log?.focusOn($event)"
        />

        <RunLog ref="log" :lines="lines" :running="running" />
      </div>
    </template>
  </div>
</template>
