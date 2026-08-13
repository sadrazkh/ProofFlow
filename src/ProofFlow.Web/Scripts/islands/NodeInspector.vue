<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, ref } from 'vue';
import KeyValueTable, { type KeyValueRow } from './KeyValueTable.vue';
import ReferencePicker from './ReferencePicker.vue';
import { t } from '../lib/i18n';
import { MATCHER_GROUPS } from './baselineTypes';
import { fieldWithin, type TextField } from '../lib/caret';
import { insertAtCaret } from '../lib/caret';
import { EMPTY_CATALOGUE, type ReferenceCatalogue } from './referenceTypes';
import type { GraphNodeDto, GraphProblem, NodeSpecDto, PropertyDto } from './graphTypes';

/**
 * The form for whichever node is selected, built from its specification.
 *
 * Which is the whole reason the catalogue is data. Seventy hand-written forms would be seventy
 * places to forget a label, and the seventy-first node would need a seventy-first form before it
 * could be filled in at all. This one renders any node it is given.
 *
 * Only the fields that apply are shown. A node with fourteen properties of which four are relevant
 * is a node people fill in wrongly — so a body appears once a body kind is chosen, and not before.
 */

const props = defineProps<{
  node: GraphNodeDto | null;
  spec: NodeSpecDto | null;
  problems: GraphProblem[];
  environments: { id: string; name: string; isProduction: boolean }[];
  canEdit: boolean;
  canRun: boolean;

  /** What can be written between braces here — offered, so nobody has to remember it. */
  catalogue?: ReferenceCatalogue;
}>();

const emit = defineEmits<{
  update: [changes: Partial<GraphNodeDto>];
  property: [name: string, value: string | null];
  remove: [];
  runFrom: [];
}>();

/**
 * Whether this step can be the place a run begins.
 *
 * Not the Start — running from Start is the ordinary button. And not a step inside a container: a
 * loop body is driven by the loop, and entering at it would run it once, outside the thing that
 * gives its iteration any meaning.
 */
const canStartHere = computed(() =>
  props.canRun
  && props.node !== null
  && !props.node.parentId
  && props.spec?.isStart !== true);

const visible = computed<PropertyDto[]>(() => {
  if (!props.spec || !props.node) return [];

  return props.spec.properties.filter((property) => {
    if (!property.visibleWhen) return true;
    const current = props.node!.properties[property.visibleWhen.property];
    return current !== null && current !== undefined
      && property.visibleWhen.values.includes(current);
  });
});

/** Problems for this node, so the reader is not sent to a list at the bottom of the page. */
const mine = computed(() => props.problems.filter((problem) => problem.nodeId === props.node?.id));

function errorFor(name: string): string | null {
  return mine.value.find((problem) => problem.property === name)?.message ?? null;
}

function value(name: string): string {
  return props.node?.properties[name] ?? '';
}

function set(name: string, next: string | null): void {
  emit('property', name, next);
}

/** Key/value properties are stored as JSON, and edited as rows. */
function rows(name: string): KeyValueRow[] {
  try {
    const parsed = JSON.parse(value(name) || '[]');
    return Array.isArray(parsed) && parsed.length ? parsed : [{ name: '', value: '', enabled: true }];
  } catch {
    return [{ name: '', value: '', enabled: true }];
  }
}

function setRows(name: string, next: KeyValueRow[]): void {
  const kept = next.filter((row) => row.name || row.value);
  set(name, kept.length ? JSON.stringify(kept) : null);
}

const matchers = MATCHER_GROUPS;

const catalogue = computed(() => props.catalogue ?? EMPTY_CATALOGUE);

/**
 * Which fields are worth offering references in.
 *
 * Not a number, not a choice, not a checkbox — a reference resolves to text, and offering to put
 * one into a field that takes «200» or «GET» would be offering something that cannot work.
 */
function takesReferences(property: PropertyDto): boolean {
  return property.kind === 'Text' || property.kind === 'LongText' || property.kind === 'Url'
    || property.kind === 'JsonPath' || property.kind === 'Expression';
}

/** One wrapper per property, so the picker can find the field it belongs to. */
const wrappers = ref<Record<string, HTMLElement | null>>({});

function hold(name: string, element: unknown): void {
  wrappers.value[name] = (element as HTMLElement | null) ?? null;
}

function insert(name: string, text: string): void {
  const field: TextField | null = fieldWithin(wrappers.value[name] ?? null);
  if (!field) return;

  set(name, insertAtCaret(field, text) || null);
}
</script>

<template>
  <aside class="inspector" :aria-label="t('canvas.inspector')">
    <div v-if="!node || !spec" class="empty empty-inline">
      <div class="empty-art"><Icon name="mouse-pointer-click" /></div>
      <p class="empty-body">{{ t('canvas.nothingSelected') }}</p>
    </div>

    <template v-else>
      <header class="inspector-head">
        <span class="node-palette-icon" :class="`is-${spec.group.toLowerCase()}`">
          <Icon :name="spec.icon" :size="14" />
        </span>

        <label class="field grow">
          <span class="sr-only">{{ t('canvas.stepName') }}</span>
          <input
            class="input inspector-name"
            dir="auto"
            :value="node.name"
            :disabled="!canEdit"
            :aria-label="t('canvas.stepName')"
            @change="emit('update', { name: ($event.target as HTMLInputElement).value.trim() })"
          />
        </label>

        <!--
          Disable rather than delete. A step somebody is working around temporarily should come
          back with its properties and its connections intact.
        -->
        <label class="check-row has-tip" :data-tip="t('canvas.disable')" style="padding: 0;">
          <input
            class="checkbox"
            type="checkbox"
            :checked="node.disabled"
            :disabled="!canEdit"
            :aria-label="t('canvas.disable')"
            @change="emit('update', { disabled: ($event.target as HTMLInputElement).checked })"
          />
        </label>
      </header>

      <p class="section-help">{{ t(`node.${spec.key}.summary`) }}</p>

      <div class="inspector-body stack-2">
        <template v-for="property in visible" :key="property.name">
          <label class="field" :ref="(el) => hold(property.name, el)">
            <span class="field-label">
              {{ t(property.labelKey) }}
              <span v-if="!property.required" class="field-optional">{{ t('common.optional') }}</span>

              <!--
                Beside the label rather than inside the field. An icon sitting on top of a text box
                covers the end of what is written in it, which in this product is usually the part
                somebody is editing.
              -->
              <ReferencePicker
                v-if="canEdit && takesReferences(property)"
                :catalogue="catalogue"
                :field="t(property.labelKey)"
                @pick="insert(property.name, $event)"
              />
            </span>

            <select
              v-if="property.kind === 'Choice'"
              class="select"
              :value="value(property.name) || property.default || ''"
              :disabled="!canEdit"
              @change="set(property.name, ($event.target as HTMLSelectElement).value)"
            >
              <option v-for="option in property.options" :key="option" :value="option">
                {{ t(`option.${option}`) }}
              </option>
            </select>

            <select
              v-else-if="property.kind === 'Matcher'"
              class="select"
              :value="value(property.name) || 'Exact'"
              :disabled="!canEdit"
              @change="set(property.name, ($event.target as HTMLSelectElement).value)"
            >
              <optgroup v-for="group in matchers" :key="group.key" :label="t(`rule.group.${group.key}`)">
                <option v-for="matcher in group.matchers" :key="matcher" :value="matcher">
                  {{ t(`matcher.${matcher}`) }}
                </option>
              </optgroup>
            </select>

            <select
              v-else-if="property.kind === 'Reference'"
              class="select"
              :value="value(property.name)"
              :disabled="!canEdit"
              @change="set(property.name, ($event.target as HTMLSelectElement).value || null)"
            >
              <option value="">{{ t('common.none') }}</option>
              <option v-for="item in environments" :key="item.id" :value="item.id">{{ item.name }}</option>
            </select>

            <textarea
              v-else-if="property.kind === 'LongText'"
              class="textarea input-mono"
              rows="4"
              dir="ltr"
              :value="value(property.name)"
              :disabled="!canEdit"
              :placeholder="property.placeholder ?? ''"
              :aria-invalid="errorFor(property.name) ? 'true' : undefined"
              @change="set(property.name, ($event.target as HTMLTextAreaElement).value || null)"
            ></textarea>

            <input
              v-else-if="property.kind === 'Boolean'"
              class="checkbox"
              type="checkbox"
              :checked="value(property.name) === 'true'"
              :disabled="!canEdit"
              @change="set(property.name, ($event.target as HTMLInputElement).checked ? 'true' : 'false')"
            />

            <KeyValueTable
              v-else-if="property.kind === 'KeyValues'"
              :model-value="rows(property.name)"
              :label="t(property.labelKey)"
              :catalogue="catalogue"
              @update:model-value="setRows(property.name, $event)"
            />

            <input
              v-else
              class="input"
              :class="{ 'input-mono': property.kind !== 'Text' }"
              :type="property.kind === 'Number' ? 'number' : 'text'"
              :dir="property.kind === 'Text' ? 'auto' : 'ltr'"
              :value="value(property.name)"
              :disabled="!canEdit"
              :placeholder="property.placeholder ?? ''"
              :aria-invalid="errorFor(property.name) ? 'true' : undefined"
              @change="set(property.name, ($event.target as HTMLInputElement).value || null)"
            />

            <!-- Beside the field it is about, not in a summary at the bottom of the panel. -->
            <span v-if="errorFor(property.name)" class="field-error">
              <Icon name="circle-alert" :size="13" />{{ errorFor(property.name) }}
            </span>
            <span v-else-if="property.helpKey" class="field-hint">{{ t(property.helpKey) }}</span>
          </label>
        </template>

        <label class="field">
          <span class="field-label">
            {{ t('canvas.note') }}
            <span class="field-optional">{{ t('common.optional') }}</span>
          </span>
          <textarea
            class="textarea"
            rows="2"
            dir="auto"
            :value="node.note ?? ''"
            :disabled="!canEdit"
            :placeholder="t('canvas.notePlaceholder')"
            @change="emit('update', { note: ($event.target as HTMLTextAreaElement).value || null })"
          ></textarea>
        </label>
      </div>

      <!-- Problems that are about the node rather than about one of its fields. -->
      <ul v-if="mine.some((p) => !p.property)" class="inspector-problems">
        <li v-for="problem in mine.filter((p) => !p.property)" :key="problem.code + problem.message"
            :class="problem.severity === 'Error' ? 'is-error' : 'is-warning'">
          <Icon :name="problem.severity === 'Error' ? 'circle-alert' : 'triangle-alert'" :size="13" />
          {{ problem.message }}
        </li>
      </ul>

      <footer v-if="canEdit || canStartHere" class="inspector-foot">
        <button v-if="canEdit" type="button" class="btn btn-secondary btn-sm" @click="emit('remove')">
          <Icon name="trash-2" />{{ t('canvas.removeStep') }}
        </button>

        <!--
          Debugging the second half of a long scenario. The title says what it will not do, because
          the surprise otherwise is a step that fails on a value an earlier step was supposed to
          have put there.
        -->
        <button
          v-if="canStartHere"
          type="button"
          class="btn btn-secondary btn-sm"
          :title="t('canvas.runFrom.hint')"
          @click="emit('runFrom')"
        >
          <Icon name="circle-play" />{{ t('canvas.runFrom') }}
        </button>
      </footer>
    </template>
  </aside>
</template>
