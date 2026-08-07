<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed } from 'vue';
import { Handle, Position } from '@vue-flow/core';
import { t } from '../lib/i18n';
import type { GraphProblem, NodeSpecDto, NodeState } from './graphTypes';

/**
 * One box on the canvas.
 *
 * The anatomy is fixed so that thirty of them read as one picture: a group icon, the name somebody
 * gave it, one line saying what it will do, a ring for what happened when it last ran, and the
 * sockets down each edge.
 *
 * Two of those carry meaning twice over. The state ring is a colour *and* a shape — a run where
 * pass and fail differ only in green and red is unreadable to one man in twelve. And a failure
 * socket is a diamond as well as red, because "which of these is the error path" is the question
 * somebody asks while dragging, when the tooltip is not showing.
 */

const props = defineProps<{
  id: string;
  data: {
    spec: NodeSpecDto;
    name: string;
    summary: string;

    /// True when the summary is a value somebody typed rather than a sentence we wrote.
    literalSummary: boolean;

    disabled: boolean;
    state: NodeState;
    problems: GraphProblem[];
  };
  selected?: boolean;
}>();

const spec = computed(() => props.data.spec);

const errors = computed(() => props.data.problems.filter((p) => p.severity === 'Error'));
const warnings = computed(() => props.data.problems.filter((p) => p.severity === 'Warning'));

/**
 * Sockets are spread evenly down the edge rather than stacked from the top.
 *
 * A node with one output and a node with four have to look like the same kind of object, and
 * ports bunched at the top of a 64px card with empty space below reads as a mistake.
 */
function offset(index: number, count: number): string {
  return `${((index + 1) / (count + 1)) * 100}%`;
}
</script>

<template>
  <div
    class="wf-node"
    :class="[
      `is-${spec.group.toLowerCase()}`,
      `state-${data.state}`,
      { 'is-disabled': data.disabled, 'is-selected': selected, 'has-error': errors.length },
    ]"
  >
    <!-- Inputs down the leading edge. Flow is left to right even in Persian; see the design plan. -->
    <Handle
      v-for="(port, index) in spec.inputs"
      :key="`in-${port.name}`"
      :id="port.name"
      type="target"
      :position="Position.Left"
      class="wf-port"
      :class="[`is-${port.kind.toLowerCase()}`, `type-${port.type.toLowerCase()}`, { 'is-required': port.required }]"
      :style="{ top: offset(index, spec.inputs.length) }"
      :title="`${t(port.labelKey)} · ${t(`portType.${port.type}`)}`"
    />

    <header class="wf-head">
      <span class="wf-icon"><Icon :name="spec.icon" :size="15" /></span>

      <span class="wf-name" dir="auto">{{ data.name }}</span>

      <!-- Two marks, both of which change what a reader should expect from a run. -->
      <span v-if="spec.reaches" class="wf-mark has-tip" :data-tip="t('canvas.reachesOut')">
        <Icon name="globe" :size="12" />
      </span>
      <span v-if="data.disabled" class="wf-mark has-tip" :data-tip="t('canvas.disabled')">
        <Icon name="circle-slash" :size="12" />
      </span>
    </header>

    <!--
      A URL is mono and left-to-right; a translated sentence is neither. The card asks the summary
      which it is rather than forcing one on both.
    -->
    <p class="wf-summary" :class="{ 'is-literal': data.literalSummary }"
       :dir="data.literalSummary ? 'ltr' : 'auto'">{{ data.summary }}</p>

    <footer v-if="errors.length || warnings.length" class="wf-problems">
      <span v-if="errors.length" class="wf-problem is-error">
        <Icon name="circle-alert" :size="12" />{{ errors[0]!.message }}
      </span>
      <span v-else class="wf-problem is-warning">
        <Icon name="triangle-alert" :size="12" />{{ warnings[0]!.message }}
      </span>
    </footer>

    <!--
      The state ring, named as well as coloured. Screen readers get the word; everyone gets the
      shape, because a run where pass and fail differ only in hue is unreadable to one reader in
      twelve.
    -->
    <span class="wf-state" aria-hidden="true"></span>
    <span class="sr-only">{{ t(`canvas.state.${data.state}`) }}</span>

    <Handle
      v-for="(port, index) in spec.outputs"
      :key="`out-${port.name}`"
      :id="port.name"
      type="source"
      :position="Position.Right"
      class="wf-port"
      :class="[
        `is-${port.kind.toLowerCase()}`,
        `type-${port.type.toLowerCase()}`,
        { 'is-failure': port.isFailure },
      ]"
      :style="{ top: offset(index, spec.outputs.length) }"
      :title="`${t(port.labelKey)} · ${t(`portType.${port.type}`)}`"
    />
  </div>
</template>
