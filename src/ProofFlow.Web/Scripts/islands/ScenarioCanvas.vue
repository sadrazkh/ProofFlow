<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef, watch, type Ref } from 'vue';
import { VueFlow, useVueFlow, type Connection, type Edge, type Node } from '@vue-flow/core';
import { Background } from '@vue-flow/background';
import { MiniMap } from '@vue-flow/minimap';
import WorkflowNodeCard from './WorkflowNodeCard.vue';
import NodePalette from './NodePalette.vue';
import NodeInspector from './NodeInspector.vue';
import { EMPTY_CATALOGUE, type ReferenceCatalogue } from './referenceTypes';
import { api, ApiError } from '../lib/api';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';
import {
  accepts,
  type GraphDto, type GraphEdgeDto, type GraphNodeDto, type GraphProblem,
  type NodeSpecDto, type SaveGraphResult,
} from './graphTypes';

/**
 * Where a test gets drawn.
 *
 * Three decisions shape it.
 *
 * Flow runs left to right even in Persian. A flowchart's direction is a convention people already
 * hold, not a reading direction — mirroring it would make every diagram in every document about
 * this product wrong. The palette, the inspector and every word on a node are localised; the
 * arrows are not. This is recorded in the design plan and visible in the Persian screenshots.
 *
 * An edge is refused before it is dropped, not reported after. The type check runs while the
 * connection is being dragged, so a list cannot be plugged into a socket wanting a response —
 * the interaction says no rather than the validator saying no afterwards.
 *
 * And undo is over the graph, not over the viewport. Panning is not a change to the test, so it is
 * not something Ctrl+Z should take back.
 */

const props = defineProps<{
  projectId: string;
  scenarioId: string;
  environments: { id: string; name: string; isProduction: boolean }[];
  canEdit: boolean;
  canRun: boolean;
  canDraw: boolean;

  /** What this scenario asks before it runs, offered in every field as {{inputs.name}}. */
  inputs: string[];

  /** Whose variables and secrets to offer. The one this scenario runs in unless told otherwise. */
  environmentId: string | null;
}>();

const {
  onConnect, addEdges, project, getSelectedNodes, onNodeDragStop, toObject, addSelectedNodes,
  findNode, zoomIn, zoomOut, fitView, onNodesInitialized,
} = useVueFlow();

/**
 * What this canvas keeps on a node, named.
 *
 * Vue Flow's own `Node` is generic enough that inferring through it blows past TypeScript's
 * instantiation depth — and the wider point is that these six fields are the contract between the
 * card, the inspector and the serialiser, so writing them down is worth doing anyway.
 */
type CanvasNode = {
  id: string;
  position: { x: number; y: number };
  parentNode?: string;
  data: {
    spec: NodeSpecDto;
    name: string;
    note?: string | null;
    disabled?: boolean;
    properties?: Record<string, string | null>;
  };
};

const specs = shallowRef<NodeSpecDto[]>([]);
const byKey = computed(() => new Map(specs.value.map((spec) => [spec.key, spec])));

/*
  `ref([]) as Ref<Node[]>` rather than `ref<Node[]>([])`.

  Vue's UnwrapRef walks the whole type to build the reactive shape, and Vue Flow's Node is generic
  enough that the walk exceeds TypeScript's instantiation depth — which fails the build with a
  message about infinite types and no hint of where. The cast keeps the runtime behaviour and skips
  the computation.
*/
const nodes = ref([]) as Ref<Node[]>;
const edges = ref([]) as Ref<Edge[]>;
const problems = ref<GraphProblem[]>([]);
const selectedId = ref<string | null>(null);

const saving = ref(false);
const dirty = ref(false);
const loaded = ref(false);

/** Undo over the graph only. Panning is not a change to the test. */
const past = ref<string[]>([]);
const future = ref<string[]>([]);
const HISTORY = 100;

const selected = computed<GraphNodeDto | null>(() => {
  const node = nodes.value.find((n) => n.id === selectedId.value);
  return node ? toGraphNode(node as unknown as CanvasNode) : null;
});

const selectedSpec = computed(() => selected.value ? byKey.value.get(selected.value.key) ?? null : null);

/**
 * The names this environment publishes. Fetched once, and only names.
 *
 * The same endpoint the request builder uses, for the same reason: a secret's value never comes to
 * the browser, and knowing that one is called «apiToken» is what somebody needs to write a header.
 */
const names = ref({ environment: [] as string[], variables: [] as string[], secrets: [] as string[] });

/**
 * Which steps the selected one can already read.
 *
 * Walked backwards along the connections rather than taken from the whole graph. Offering every
 * step would offer ones that run afterwards, and a reference to a step that has not happened yet
 * resolves to nothing at exactly the moment somebody is trying to work out why.
 */
const reachable = computed<string[]>(() => {
  if (!selected.value) return [];

  const before = new Map<string, string[]>();
  for (const edge of edges.value) {
    const list = before.get(edge.target) ?? [];
    list.push(edge.source);
    before.set(edge.target, list);
  }

  const seen = new Set<string>();
  const queue = [...(before.get(selected.value.id) ?? [])];

  while (queue.length > 0) {
    const id = queue.pop()!;
    if (seen.has(id)) continue;
    seen.add(id);
    queue.push(...(before.get(id) ?? []));
  }

  return nodes.value
    .filter((node) => seen.has(node.id))
    .map((node) => (node.data as { name?: string }).name ?? '')
    .filter((name) => name.length > 0);
});

const catalogue = computed<ReferenceCatalogue>(() => ({
  ...EMPTY_CATALOGUE,
  environment: names.value.environment,
  variables: names.value.variables,
  secrets: names.value.secrets,
  inputs: props.inputs,
  steps: reachable.value,
}));

const errors = computed(() => problems.value.filter((p) => p.severity === 'Error'));
const warnings = computed(() => problems.value.filter((p) => p.severity === 'Warning'));

onMounted(async () => {
  await loadCatalogue();
  await loadNames();
  await loadGraph();

  document.addEventListener('keydown', onKey);
  window.addEventListener('beforeunload', warnIfDirty);
});

onUnmounted(() => {
  document.removeEventListener('keydown', onKey);
  window.removeEventListener('beforeunload', warnIfDirty);
});

function warnIfDirty(event: BeforeUnloadEvent): void {
  if (dirty.value) event.preventDefault();
}

const drawing = ref(false);
const asked = ref('');

/**
 * Asks the workspace's model for a graph and puts it on the canvas, unsaved.
 *
 * Through the history, so the first thing somebody does after seeing a draft they dislike is press
 * Ctrl+Z and get their own canvas back. A feature that overwrites work with no way back is one
 * people stop pressing.
 */
async function draw(): Promise<void> {
  const request = asked.value.trim();
  if (!request || drawing.value) return;

  drawing.value = true;

  try {
    const graph = await api.post<GraphDto>(
      `/projects/${props.projectId}/scenarios/${props.scenarioId}/draw`, { request });

    remember();

    nodes.value = graph.nodes.map(toFlowNode);
    edges.value = graph.edges.map(toFlowEdge);
    selectedId.value = null;
    dirty.value = true;

    await nextTick();
    fitView({ maxZoom: 1, padding: 0.2 });
    await validate();

    asked.value = '';
    toast(t('ai.drawn'), 'success');
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    drawing.value = false;
  }
}

async function loadNames(): Promise<void> {
  try {
    // With the environment, because half of what is worth offering — baseUrl, and every secret
    // defined for one place rather than for all of them — does not exist without it.
    const environment = props.environmentId ?? props.environments[0]?.id ?? '';

    names.value = await api.get(
      `/projects/${props.projectId}/request/variables?environmentId=${environment}`);
  } catch {
    // Only the offer degrades. Anything already written still resolves at run time.
  }
}

async function loadCatalogue(): Promise<void> {
  try {
    specs.value = await api.get<NodeSpecDto[]>(`/projects/${props.projectId}/scenarios/catalogue`);
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  }
}

async function loadGraph(): Promise<void> {
  try {
    const graph = await api.get<GraphDto>(
      `/projects/${props.projectId}/scenarios/${props.scenarioId}/graph`);

    nodes.value = graph.nodes.map(toFlowNode);
    edges.value = graph.edges.map(toFlowEdge);

    // An empty scenario has no card to measure, so onNodesInitialized never fires for it.
    if (graph.nodes.length === 0)
    {
      await nextTick();
      savedShape.value = JSON.stringify(currentGraph());
      loaded.value = true;
    }

    await validate();
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  }
}

// ---- shapes ------------------------------------------------------------------------------------

function toFlowNode(node: GraphNodeDto): Node {
  const spec = byKey.value.get(node.key);

  return {
    id: node.id,
    type: 'workflow',
    position: { x: node.x, y: node.y },
    data: {
      spec: spec ?? unknownSpec(node.key),
      name: node.name,
      summary: summarise(node, spec).text,
      literalSummary: summarise(node, spec).literal,
      note: node.note,
      disabled: node.disabled,
      state: 'idle',
      properties: { ...node.properties },
      problems: [],
    },
  };
}

/**
 * A node whose type this version does not know.
 *
 * Drawn rather than dropped: a graph saved by a newer version has to open, show what it can, and
 * say what it cannot — silently losing the node would lose the connections around it too.
 */
function unknownSpec(key: string): NodeSpecDto {
  return {
    key, group: 'Core', icon: 'circle-help', inputs: [], outputs: [], properties: [],
    isStart: false, isTerminal: false, isContainer: false, reaches: false,
  };
}

function toFlowEdge(edge: GraphEdgeDto): Edge {
  return {
    id: edge.id,
    source: edge.fromId,
    sourceHandle: edge.fromPort,
    target: edge.toId,
    targetHandle: edge.toPort,
    label: edge.label ?? undefined,
    class: portOf(edge.fromId, edge.fromPort)?.isFailure ? 'is-failure' : undefined,
  };
}

function toGraphNode(node: CanvasNode): GraphNodeDto {
  return {
    id: node.id,
    key: node.data.spec.key,
    name: node.data.name,
    note: node.data.note ?? null,
    x: node.position.x,
    y: node.position.y,
    parentId: node.parentNode ?? null,
    disabled: !!node.data.disabled,
    properties: node.data.properties ?? {},
  };
}

function currentGraph(): GraphDto {
  return {
    nodes: nodes.value.map((node) => toGraphNode(node as unknown as CanvasNode)),
    edges: edges.value.map((edge) => ({
      id: edge.id,
      fromId: edge.source,
      fromPort: edge.sourceHandle ?? 'out',
      toId: edge.target,
      toPort: edge.targetHandle ?? 'in',
      label: (edge.label as string) ?? null,
    })),
    canvasJson: JSON.stringify(toObject().viewport ?? {}),
  };
}

/**
 * The one line under a node's name.
 *
 * Whatever the node's most telling property is — the address for a request, the path for an
 * extraction — because a canvas of thirty boxes all reading "HTTP request" tells a reader nothing.
 */
function summarise(node: GraphNodeDto, spec: NodeSpecDto | undefined): Summary {
  if (!spec) return { text: t('canvas.unknownType'), literal: false };

  for (const name of ['url', 'path', 'condition', 'expression', 'message', 'text', 'name', 'template']) {
    const value = node.properties[name];
    if (value) return { text: value, literal: true };
  }

  return { text: t(`node.${spec.key}.summary`), literal: false };
}

/** Whether the line under a node's name is a value somebody typed or a sentence we wrote. */
type Summary = { text: string; literal: boolean };

function portOf(nodeId: string, portName: string) {
  const node = nodes.value.find((n) => n.id === nodeId);
  return node?.data.spec.outputs.find((p: { name: string }) => p.name === portName);
}

// ---- editing -----------------------------------------------------------------------------------

function remember(): void {
  past.value = [...past.value.slice(-HISTORY + 1), JSON.stringify(currentGraph())];
  future.value = [];
  dirty.value = true;
}

function restore(snapshot: string): void {
  const graph = JSON.parse(snapshot) as GraphDto;
  nodes.value = graph.nodes.map(toFlowNode);
  edges.value = graph.edges.map(toFlowEdge);
  void validate();
}

function undo(): void {
  const previous = past.value.at(-1);
  if (!previous) return;

  future.value = [JSON.stringify(currentGraph()), ...future.value];
  past.value = past.value.slice(0, -1);
  restore(previous);
  dirty.value = true;
}

function redo(): void {
  const next = future.value[0];
  if (!next) return;

  past.value = [...past.value, JSON.stringify(currentGraph())];
  future.value = future.value.slice(1);
  restore(next);
  dirty.value = true;
}

/** A name nothing else in the graph is using, because names are what references point at. */
function uniqueName(base: string): string {
  const taken = new Set(nodes.value.map((node) => node.data.name));
  if (!taken.has(base)) return base;

  let index = 2;
  while (taken.has(`${base} ${index}`)) index++;
  return `${base} ${index}`;
}

function add(key: string, at?: { x: number; y: number }): void {
  if (!props.canEdit) return;

  const spec = byKey.value.get(key);
  if (!spec) return;

  if (spec.isStart && nodes.value.some((node) => node.data.spec.isStart)) {
    toast(t('canvas.oneStartOnly'), 'warn');
    return;
  }

  remember();

  // Placed clear of what is already there: a 24px step on a 216px card buried every node under
  // the next one. To the right of the rightmost, or beside the selection when there is one.
  const anchor = nodes.value.find((node) => node.id === selectedId.value)
    ?? nodes.value.reduce<Node | undefined>(
      (rightmost, node) => !rightmost || node.position.x > rightmost.position.x ? node : rightmost,
      undefined);

  const position = at ?? (anchor
    ? { x: anchor.position.x + 280, y: anchor.position.y }
    : { x: 96, y: 96 });
  const id = `new-${crypto.randomUUID()}`;

  const properties: Record<string, string | null> = {};
  for (const property of spec.properties) {
    if (property.default) properties[property.name] = property.default;
  }

  nodes.value = [...nodes.value, {
    id,
    type: 'workflow',
    position,
    data: {
      spec,
      name: uniqueName(t(`node.${key}.title`)),
      summary: t(`node.${key}.summary`),
      literalSummary: false,
      note: null,
      disabled: false,
      state: 'idle',
      properties,
      problems: [],
    },
  }];

  selectedId.value = id;

  // Brought into view. Placed 280px along, a new node can land past the edge of the surface or
  // under the inspector that just opened for it — and a palette click that appears to do nothing
  // is the one thing a palette must never do.
  void reveal(id);
  void validate();
}

/**
 * Scrolls a node into the visible part of the canvas.
 *
 * Only when it is not already there: a canvas that jumped on every addition would make placing
 * five nodes in a row into five involuntary pans.
 */
async function reveal(id: string): Promise<void> {
  await nextTick();

  const node = findNode(id);
  const surface = document.querySelector('.canvas-surface');
  if (!node || !surface) return;

  const bounds = surface.getBoundingClientRect();
  const inspector = document.querySelector('.inspector')?.getBoundingClientRect();

  // The inspector sits over the surface, so the visible width ends where it begins.
  const visible = inspector && inspector.width > 0
    ? { width: Math.max(0, bounds.width - inspector.width), height: bounds.height }
    : { width: bounds.width, height: bounds.height };

  const at = project({ x: bounds.left, y: bounds.top });
  const far = project({ x: bounds.left + visible.width, y: bounds.top + visible.height });

  const inside = node.position.x >= at.x
    && node.position.x + (node.dimensions?.width ?? 216) <= far.x
    && node.position.y >= at.y
    && node.position.y + (node.dimensions?.height ?? 64) <= far.y;

  if (!inside) fitView({ nodes: [id], duration: 200, maxZoom: 1 });
}

function duplicate(): void {
  const node = nodes.value.find((n) => n.id === selectedId.value);
  if (!node || !props.canEdit) return;

  remember();

  const id = `new-${crypto.randomUUID()}`;
  nodes.value = [...nodes.value, {
    ...node,
    id,
    position: { x: node.position.x + 40, y: node.position.y + 40 },
    data: { ...node.data, name: uniqueName(node.data.name), properties: { ...node.data.properties } },
  }];

  selectedId.value = id;
  void reveal(id);
  void validate();
}

/**
 * Ctrl+A. Selects every node, which is what makes "delete these fifteen" one action.
 *
 * Vue Flow wants its own internal node objects rather than the ones this component holds, so they
 * are looked up rather than passed through.
 */
function selectAll(): void {
  addSelectedNodes(nodes.value.map((node) => findNode(node.id)!).filter(Boolean));
}

function removeSelected(): void {
  if (!props.canEdit) return;

  const ids = getSelectedNodes.value.map((node) => node.id);
  const targets = ids.length ? ids : selectedId.value ? [selectedId.value] : [];
  if (targets.length === 0) return;

  remember();

  nodes.value = nodes.value.filter((node) => !targets.includes(node.id));
  edges.value = edges.value.filter(
    (edge) => !targets.includes(edge.source) && !targets.includes(edge.target));

  selectedId.value = null;
  void validate();
}

function updateSelected(changes: Partial<GraphNodeDto>): void {
  const node = nodes.value.find((n) => n.id === selectedId.value);
  if (!node || !props.canEdit) return;

  remember();

  nodes.value = nodes.value.map((current) => current.id !== node.id ? current : {
    ...current,
    data: {
      ...current.data,
      ...(changes.name !== undefined ? { name: changes.name } : {}),
      ...(changes.note !== undefined ? { note: changes.note } : {}),
      ...(changes.disabled !== undefined ? { disabled: changes.disabled } : {}),
    },
  });

  void validate();
}

function setProperty(name: string, value: string | null): void {
  const node = nodes.value.find((n) => n.id === selectedId.value);
  if (!node || !props.canEdit) return;

  remember();

  nodes.value = nodes.value.map((current) => {
    if (current.id !== node.id) return current;

    const properties = { ...current.data.properties, [name]: value };
    const graphNode = { ...toGraphNode(current as unknown as CanvasNode), properties };

    return {
      ...current,
      data: {
        ...current.data,
        properties,
        summary: summarise(graphNode, current.data.spec).text,
        literalSummary: summarise(graphNode, current.data.spec).literal,
      },
    };
  });

  void validate();
}

// ---- connections -------------------------------------------------------------------------------

/**
 * Whether an edge may be made at all.
 *
 * Vue Flow asks this while the connection is being dragged, which is what makes a mismatch a
 * refusal rather than a report: the line does not attach, so nobody has to be told afterwards that
 * what they drew is wrong.
 */
function isValidConnection(connection: Connection): boolean {
  if (connection.source === connection.target) return false;

  const from = nodes.value.find((n) => n.id === connection.source);
  const to = nodes.value.find((n) => n.id === connection.target);
  if (!from || !to) return false;

  const output = from.data.spec.outputs.find(
    (port: { name: string }) => port.name === connection.sourceHandle);
  const input = to.data.spec.inputs.find(
    (port: { name: string }) => port.name === connection.targetHandle);

  if (!output || !input) return false;
  if (output.kind !== input.kind) return false;
  if (output.kind === 'Control') return true;

  return accepts(input.type, output.type);
}

onConnect((connection) => {
  if (!props.canEdit) return;

  remember();
  addEdges([{
    ...connection,
    id: `new-${crypto.randomUUID()}`,
    class: portOf(connection.source, connection.sourceHandle ?? '')?.isFailure ? 'is-failure' : undefined,
  }]);

  void validate();
});

onNodeDragStop(() => {
  dirty.value = true;
  // Not remembered as an undo step per pixel: the drag is one thought, and the position is taken
  // from the live nodes when the graph is next serialised.
});

/** Read from the document so the minimap follows the theme rather than guessing at it. */
function minimapColour(): string {
  return getComputedStyle(document.documentElement)
    .getPropertyValue('--canvas-edge').trim() || '#9aa0b5';
}

// ---- drag from the palette ---------------------------------------------------------------------

function onDrop(event: DragEvent): void {
  const key = event.dataTransfer?.getData('application/proofflow-node');
  if (!key) return;

  event.preventDefault();
  add(key, project({ x: event.clientX, y: event.clientY }));
}

// ---- validate and save -------------------------------------------------------------------------

let validateTimer: number | undefined;

function validate(): Promise<void> {
  // Debounced: typing a URL is twenty keystrokes and twenty round trips would be twenty chances
  // for a slow one to arrive after a fast one and overwrite it.
  window.clearTimeout(validateTimer);

  return new Promise((resolve) => {
    validateTimer = window.setTimeout(async () => {
      try {
        problems.value = await api.post<GraphProblem[]>(
          `/projects/${props.projectId}/scenarios/validate`, currentGraph());

        attachProblems();
      } catch {
        // A failed check is not a failed edit. The save runs the same validation server-side.
      } finally {
        resolve();
      }
    }, 250);
  });
}

/** Puts each problem on its node, so the message is where the mistake is. */
function attachProblems(): void {
  nodes.value = nodes.value.map((node) => ({
    ...node,
    data: {
      ...node.data,
      problems: problems.value.filter((problem) => problem.nodeId === node.id),
    },
  }));
}

/**
 * Starts a run at the selected step.
 *
 * Through the page's own run form rather than a second POST of its own: that form already carries
 * the antiforgery token and the environment somebody chose in the bar, and duplicating either here
 * would mean a "run from here" that quietly went somewhere else.
 *
 * Saved first. The server has to begin at a step it knows about, and the save is also what turns a
 * node's temporary id into the one the graph is stored under.
 */
async function runFromSelected(): Promise<void> {
  if (dirty.value && props.canEdit) await save();

  const from = selectedId.value;
  const form = document.querySelector<HTMLFormElement>('form[action$="/runs/start"]');

  if (!from || !form) return;

  const field = form.querySelector<HTMLInputElement>('input[name="fromNodeId"]')
    ?? form.appendChild(Object.assign(document.createElement('input'), {
      type: 'hidden', name: 'fromNodeId',
    }));

  field.value = from;
  form.requestSubmit();
}

async function save(): Promise<void> {
  if (!props.canEdit || saving.value) return;
  saving.value = true;

  try {
    const result = await api.post<SaveGraphResult>(
      `/projects/${props.projectId}/scenarios/${props.scenarioId}/graph`, currentGraph());

    // The server's ids replace the temporary ones, so a second save updates rather than inserts —
    // and the viewport, the selection and the undo history all survive.
    const mapping = result.nodeIds;

    nodes.value = nodes.value.map((node) => ({ ...node, id: mapping[node.id] ?? node.id }));
    edges.value = edges.value.map((edge) => ({
      ...edge,
      source: mapping[edge.source] ?? edge.source,
      target: mapping[edge.target] ?? edge.target,
    }));

    if (selectedId.value) selectedId.value = mapping[selectedId.value] ?? selectedId.value;

    problems.value = result.problems;
    attachProblems();

    await nextTick();
    savedShape.value = JSON.stringify(currentGraph());
    dirty.value = false;

    toast(result.isValid ? t('canvas.saved') : t('canvas.savedWithProblems', errors.value.length),
      result.isValid ? 'success' : 'warn');
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    saving.value = false;
  }
}

// ---- keyboard ----------------------------------------------------------------------------------

function onKey(event: KeyboardEvent): void {
  const target = event.target as HTMLElement | null;
  if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return;

  const control = event.ctrlKey || event.metaKey;

  if (control && event.key.toLowerCase() === 'z' && !event.shiftKey) { event.preventDefault(); undo(); }
  else if (control && (event.key.toLowerCase() === 'y' || (event.shiftKey && event.key.toLowerCase() === 'z'))) {
    event.preventDefault(); redo();
  }
  else if (control && event.key.toLowerCase() === 's') { event.preventDefault(); void save(); }
  else if (control && event.key.toLowerCase() === 'd') { event.preventDefault(); duplicate(); }
  else if (control && event.key.toLowerCase() === 'a') { event.preventDefault(); selectAll(); }
  else if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); removeSelected(); }
}

/**
 * What was last agreed with the server, as text.
 *
 * «Unsaved changes» used to mean «the nodes array has been touched since it loaded», which is not
 * the same thing and was wrong in both directions. Vue Flow measures every card after rendering and
 * writes the size back onto the node, so every scenario opened claiming changes nobody had made —
 * and asked to confirm before leaving a page nobody had edited. Meanwhile dragging a connection
 * changed only the edges, which nothing was watching, so a real edit went unmarked.
 *
 * Comparing the shape that would be sent answers the question that was being asked all along.
 */
const savedShape = ref('');

watch([nodes, edges], () => {
  if (loaded.value) dirty.value = JSON.stringify(currentGraph()) !== savedShape.value;
}, { deep: true });

/**
 * The moment the canvas is settled: measured, laid out, and untouched.
 *
 * Fitting belongs here rather than a tick after the fetch — before the cards have a size there is
 * no extent to fit to, so it was fitting to nothing and leaving the graph parked at its top left.
 */
let settled = false;

onNodesInitialized(() => {
  if (settled) return;
  settled = true;

  // With a ceiling on the zoom. A scenario of one node fitted without one opens at 2×, where a
  // 216px card fills half the surface and the next node somebody adds lands off it.
  fitView({ maxZoom: 1, padding: 0.2 });

  void nextTick().then(() => {
    savedShape.value = JSON.stringify(currentGraph());
    loaded.value = true;
  });
});
</script>

<template>
  <div class="canvas-shell" :class="{ 'is-readonly': !canEdit }">
    <NodePalette :specs="specs" :can-edit="canEdit" @add="add($event)" />

    <div class="canvas-main">
      <div class="canvas-bar">
        <!--
          Say what you want and it draws one. A box rather than a dialog, because the thing somebody
          types here is one sentence and a dialog would make it feel like a commitment. What comes
          back is unsaved and undoable, so pressing it is cheap.
        -->
        <form v-if="canDraw" class="canvas-draw" @submit.prevent="draw">
          <label class="sr-only" for="draw-request">{{ t('ai.ask') }}</label>
          <input
            id="draw-request"
            v-model="asked"
            class="input input-sm"
            dir="auto"
            :placeholder="t('ai.ask.placeholder')"
            :disabled="drawing"
            maxlength="600"
          />
          <button type="submit" class="btn btn-secondary btn-sm" :disabled="drawing || !asked.trim()">
            <Icon :name="drawing ? 'loader' : 'sparkles'" :class="{ 'is-spinning': drawing }" />
            {{ drawing ? t('ai.drawing') : t('ai.draw') }}
          </button>
        </form>

        <button type="button" class="btn btn-ghost btn-icon btn-sm has-tip"
                :data-tip="t('canvas.undo')" :aria-label="t('canvas.undo')"
                :disabled="!past.length" @click="undo">
          <Icon name="undo-2" />
        </button>
        <span class="text-xs subtle tabular" dir="ltr">{{ past.length }}</span>

        <button type="button" class="btn btn-ghost btn-icon btn-sm has-tip"
                :data-tip="t('canvas.redo')" :aria-label="t('canvas.redo')"
                :disabled="!future.length" @click="redo">
          <Icon name="redo-2" />
        </button>

        <span class="grow"></span>

        <span v-if="errors.length" class="badge badge-fail">
          <Icon name="circle-alert" />
          <span class="tabular">{{ errors.length }}</span>{{ t('canvas.errors') }}
        </span>
        <span v-else-if="warnings.length" class="badge badge-warn">
          <Icon name="triangle-alert" />
          <span class="tabular">{{ warnings.length }}</span>{{ t('canvas.warnings') }}
        </span>
        <span v-else-if="loaded" class="badge badge-pass">
          <Icon name="circle-check" />{{ t('canvas.ready') }}
        </span>

        <span v-if="dirty" class="text-xs subtle">{{ t('common.unsaved') }}</span>

        <button v-if="canEdit" type="button" class="btn btn-primary btn-sm"
                :disabled="saving" @click="save">
          <Icon name="save" />{{ saving ? t('common.saving') : t('action.save') }}
        </button>
      </div>

      <!--
        Flow runs left to right even in Persian: a flowchart's direction is a convention people
        already hold, and mirroring it would make every diagram about this product wrong.
      -->
      <div class="canvas-surface" dir="ltr" @drop="onDrop" @dragover.prevent>
        <VueFlow
          v-model:nodes="nodes"
          v-model:edges="edges"
          :is-valid-connection="isValidConnection"
          :nodes-draggable="canEdit"
          :nodes-connectable="canEdit"
          :elements-selectable="true"
          :snap-to-grid="true"
          :snap-grid="[16, 16]"
          :min-zoom="0.2"
          :max-zoom="2"
          @node-click="selectedId = $event.node.id"
          @pane-click="selectedId = null"
        >
          <template #node-workflow="nodeProps">
            <WorkflowNodeCard v-bind="nodeProps" />
          </template>

          <Background :gap="16" :size="1.4" pattern-color="var(--canvas-dot)" />
          <!--
            A literal colour, not a token: the minimap paints onto a canvas element, where a CSS
            variable resolves to nothing and every node came out black.
          -->
          <MiniMap pannable zoomable :node-color="minimapColour" />
        </VueFlow>

        <!--
          Our own zoom controls rather than the library's.

          Vue Flow's ship as icon-only buttons with no accessible name at all, which axe reports as
          critical and a screen-reader user meets as three buttons called "button".
        -->
        <div class="canvas-controls" role="group" :aria-label="t('canvas.view')">
          <button type="button" class="btn btn-ghost btn-icon btn-sm has-tip"
                  :data-tip="t('canvas.zoomIn')" :aria-label="t('canvas.zoomIn')"
                  @click="zoomIn()">
            <Icon name="plus" />
          </button>
          <button type="button" class="btn btn-ghost btn-icon btn-sm has-tip"
                  :data-tip="t('canvas.zoomOut')" :aria-label="t('canvas.zoomOut')"
                  @click="zoomOut()">
            <Icon name="minus" />
          </button>
          <button type="button" class="btn btn-ghost btn-icon btn-sm has-tip"
                  :data-tip="t('canvas.fitView')" :aria-label="t('canvas.fitView')"
                  @click="fitView({ maxZoom: 1, padding: 0.2 })">
            <Icon name="maximize" />
          </button>
        </div>
      </div>
    </div>

    <NodeInspector
      :node="selected"
      :spec="selectedSpec"
      :problems="problems"
      :environments="environments"
      :can-edit="canEdit"
      :can-run="canRun"
      :catalogue="catalogue"
      @update="updateSelected"
      @property="setProperty"
      @remove="removeSelected"
      @run-from="runFromSelected"
    />
  </div>
</template>
