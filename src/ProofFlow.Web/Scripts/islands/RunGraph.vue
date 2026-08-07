<script setup lang="ts">
import { computed, nextTick, ref, watch, type Ref } from 'vue';
import { VueFlow, type Edge, type Node } from '@vue-flow/core';
import { Background } from '@vue-flow/background';
import { MiniMap } from '@vue-flow/minimap';
import WorkflowNodeCard from './WorkflowNodeCard.vue';
import { Icon } from '../lib/Icon';
import { t } from '../lib/i18n';
import type { GraphDto, NodeSpecDto, NodeState } from './graphTypes';
import type { NodeRunRow } from './runTypes';
import { toNodeState } from './runTypes';

/**
 * The graph that was run, with the state of each step on it.
 *
 * The same cards the canvas draws, because watching a run should be watching the picture the test
 * was built on — a second, different diagram would mean learning the product twice.
 *
 * Read-only throughout. Nothing here can be dragged, connected or deleted: the graph is a record of
 * what ran, and a record somebody can edit is not one.
 */

const props = defineProps<{
  graph: GraphDto | null;
  specs: NodeSpecDto[];
  states: NodeRunRow[];
}>();

const byKey = computed(() => new Map(props.specs.map((spec) => [spec.key, spec])));

/**
 * The state to draw for each node: the latest turn it took.
 *
 * Latest rather than worst. Inside a retry the picture should show where the step ended up, and the
 * failed attempts are in the timeline where they can be seen as a sequence.
 */
const state = computed(() => {
  const latest = new Map<string, NodeState>();

  for (const row of props.states) latest.set(row.nodeId, toNodeState(row.status));

  return latest;
});

const running = computed(() => props.states.filter((row) => row.status === 'Running'));

// See ScenarioCanvas for why these are cast rather than typed: Vue Flow's Node type is deep enough
// to exceed TypeScript's instantiation limit when Vue walks it to build the reactive shape.
const nodes = ref([]) as Ref<Node[]>;
const edges = ref([]) as Ref<Edge[]>;

const flow = ref<InstanceType<typeof VueFlow> | null>(null);


const drawnNodes = computed(() => {
  if (!props.graph) return [];

  return props.graph.nodes.map((node) => {
    const spec = byKey.value.get(node.key);

    return {
      id: node.id,
      type: 'workflow',
      position: { x: node.x, y: node.y },
      ...(node.parentId ? { parentNode: node.parentId } : {}),
      draggable: false,
      selectable: false,
      connectable: false,
      data: {
        spec,
        name: node.name,
        summary: spec ? t(`node.${spec.key}.summary`) : node.key,
        literalSummary: false,
        disabled: node.disabled,
        state: state.value.get(node.id) ?? 'idle',
        problems: [],
      },
    };
  }).filter((node) => node.data.spec);
});

const drawnEdges = computed(() => {
  if (!props.graph) return [];

  return props.graph.edges.map((edge) => ({
    id: edge.id,
    source: edge.fromId,
    sourceHandle: edge.fromPort,
    target: edge.toId,
    targetHandle: edge.toPort,
    label: edge.label ?? undefined,
    animated: running.value.some((row) => row.nodeId === edge.fromId),
    updatable: false,
    selectable: false,
  }));
});

/** Read from the document so the minimap follows the theme rather than guessing at it. */
function minimapColour(): string {
  return getComputedStyle(document.documentElement)
    .getPropertyValue('--canvas-edge').trim() || '#9aa0b5';
}

/*
  Assigned into the model rather than passed as defaults.

  `default-nodes` is only read when there is no `v-model:nodes`, and with both bound the empty model
  wins — which is a canvas that renders its grid, its minimap and no nodes at all, with nothing in
  the console to say why.
*/
watch(drawnNodes, async (next) => {
  nodes.value = next as unknown as Node[];
  await nextTick();

  // Fitted when the pane knows its size, not when the nodes arrive. The console mounts inside a
  // grid that has not been laid out yet, and a fit against a zero-width pane leaves the graph
  // pushed off its own right edge.
  if (ready) fit();
}, { immediate: true });

watch(drawnEdges, (next) => { edges.value = next as unknown as Edge[]; }, { immediate: true });

let ready = false;

function onReady(): void {
  ready = true;
  fit();
}

function fit(): void {
  (flow.value as unknown as { fitView?: (options?: object) => void } | null)
    ?.fitView?.({ maxZoom: 1, padding: 0.2 });
}
</script>

<template>
  <section class="run-graph" aria-labelledby="run-graph-heading">
    <header class="run-graph-bar">
      <h3 id="run-graph-heading" class="text-sm">{{ t('run.graph') }}</h3>

      <span class="grow"></span>

      <button type="button" class="btn btn-ghost btn-icon btn-sm"
              :aria-label="t('canvas.fit')" @click="fit">
        <Icon name="maximize" />
      </button>
    </header>

    <!--
      Flow runs left to right even in Persian: a flowchart's direction is a convention people
      already hold, and mirroring it would make every diagram about this product wrong.
    -->
    <div class="run-graph-surface" dir="ltr">
      <VueFlow
        ref="flow"
        v-model:nodes="nodes"
        v-model:edges="edges"
        :nodes-draggable="false"
        :nodes-connectable="false"
        :elements-selectable="false"
        :zoom-on-double-click="false"
        @pane-ready="onReady"
      >
        <template #node-workflow="nodeProps">
          <WorkflowNodeCard v-bind="nodeProps" />
        </template>

        <Background :gap="16" :size="1.4" pattern-color="var(--canvas-dot)" />
        <MiniMap pannable zoomable :node-color="minimapColour" />
      </VueFlow>

      <p v-if="!drawnNodes.length" class="run-graph-empty text-sm subtle">{{ t('run.graph.empty') }}</p>
    </div>
  </section>
</template>
