<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { t } from '../lib/i18n';
import { MATCHER_FIELDS, MATCHER_GROUPS, emptyRule, type Rule } from './baselineTypes';

/**
 * Where somebody says which differences do not count.
 *
 * A row is one sentence — "at this path, allow this" — and the boxes it shows are only the ones
 * that matcher needs. Twenty matchers is a lot to meet in a dropdown, so they arrive in five groups
 * named after the question being asked, and each carries a one-line explanation underneath.
 *
 * The note column is not decoration. Every one of these rows is a place where a check was
 * deliberately loosened, and in six months somebody will ask why; the answer belongs beside the
 * rule and not in a chat log.
 */

const rules = defineModel<Rule[]>({ required: true });

defineProps<{ readonly: boolean }>();

function fieldsFor(matcher: string) {
  return MATCHER_FIELDS[matcher] ?? {};
}

function add(path = ''): void {
  rules.value = [...rules.value, emptyRule(path)];
}

function remove(index: number): void {
  rules.value = rules.value.filter((_, i) => i !== index);
}

/**
 * Changing the matcher clears every parameter, not only the ones the new matcher has no slot for.
 *
 * Keeping a value because the new matcher happens to have somewhere to put it is the dangerous
 * case, not the safe one: a tolerance of ±5 on a row switched from NumericTolerance to ArrayCount
 * lands in the minimum-count box and becomes "at least five items" — a rule nobody wrote, quietly
 * enforcing something nobody meant, in a row that still reads as though it were just retyped.
 *
 * The new value is read off the event rather than from `rule.matcher`, and the assignment is made
 * here rather than by v-model. Both would listen for the same `change`, and which of them runs
 * first is a detail of how the template compiles.
 */
function onMatcherChange(rule: Rule, event: Event): void {
  rule.matcher = (event.target as HTMLSelectElement).value;
  rule.text = null;
  rule.number = null;
  rule.number2 = null;
}

</script>

<template>
  <div class="rule-builder">
    <table v-if="rules.length" class="table rule-table">
      <caption class="sr-only">{{ t('rule.tableCaption') }}</caption>
      <thead>
        <tr>
          <th class="rule-enabled"><span class="sr-only">{{ t('request.enabled') }}</span></th>
          <th class="rule-path">{{ t('rule.path') }}</th>
          <th class="rule-matcher">{{ t('rule.matcher') }}</th>
          <th class="rule-params">{{ t('rule.parameters') }}</th>
          <th class="rule-note">{{ t('rule.note') }}</th>
          <th class="rule-actions"><span class="sr-only">{{ t('action.delete') }}</span></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(rule, index) in rules" :key="rule.id ?? `new-${index}`">
          <td class="rule-enabled">
            <input
              v-model="rule.enabled"
              class="checkbox"
              type="checkbox"
              :disabled="readonly"
              :aria-label="t('rule.enabledFor', rule.path || t('rule.newRule'))"
            />
          </td>
          <td class="rule-path">
            <input
              v-model="rule.path"
              class="input input-mono"
              dir="ltr"
              :disabled="readonly"
              :placeholder="t('rule.pathPlaceholder')"
              :aria-label="t('rule.path')"
            />
          </td>
          <td class="rule-matcher">
            <select
              class="select"
              :value="rule.matcher"
              :disabled="readonly"
              :aria-label="t('rule.matcher')"
              @change="onMatcherChange(rule, $event)"
            >
              <optgroup
                v-for="group in MATCHER_GROUPS"
                :key="group.key"
                :label="t(`rule.group.${group.key}`)"
              >
                <option v-for="matcher in group.matchers" :key="matcher" :value="matcher">
                  {{ t(`matcher.${matcher}`) }}
                </option>
              </optgroup>
            </select>
            <p class="rule-hint">{{ t(`matcher.${rule.matcher}.help`) }}</p>
          </td>
          <td class="rule-params">
            <input
              v-if="fieldsFor(rule.matcher).text"
              v-model="rule.text"
              class="input input-mono"
              dir="ltr"
              :disabled="readonly"
              :placeholder="t(`rule.field.${fieldsFor(rule.matcher).text}`)"
              :aria-label="t(`rule.field.${fieldsFor(rule.matcher).text}`)"
            />
            <input
              v-if="fieldsFor(rule.matcher).number"
              v-model.number="rule.number"
              class="input input-number"
              type="number"
              step="any"
              :disabled="readonly"
              :placeholder="t(`rule.field.${fieldsFor(rule.matcher).number}`)"
              :aria-label="t(`rule.field.${fieldsFor(rule.matcher).number}`)"
            />
            <input
              v-if="fieldsFor(rule.matcher).number2"
              v-model.number="rule.number2"
              class="input input-number"
              type="number"
              step="any"
              :disabled="readonly"
              :placeholder="t(`rule.field.${fieldsFor(rule.matcher).number2}`)"
              :aria-label="t(`rule.field.${fieldsFor(rule.matcher).number2}`)"
            />
            <span
              v-if="!fieldsFor(rule.matcher).text && !fieldsFor(rule.matcher).number"
              class="rule-noparams"
            >{{ t('rule.noParameters') }}</span>
          </td>
          <td class="rule-note">
            <input
              v-model="rule.note"
              class="input"
              dir="auto"
              :disabled="readonly"
              :placeholder="t('rule.notePlaceholder')"
              :aria-label="t('rule.note')"
            />
          </td>
          <td class="rule-actions">
            <button
              v-if="!readonly"
              type="button"
              class="btn btn-ghost btn-icon btn-sm has-tip"
              :data-tip="t('action.delete')"
              :aria-label="t('rule.deleteFor', rule.path || t('rule.newRule'))"
              @click="remove(index)"
            >
              <Icon name="trash-2" />
            </button>
          </td>
        </tr>
      </tbody>
    </table>

    <div v-if="rules.length === 0" class="empty empty-inline">
      <p class="empty-body">{{ t('rule.none') }}</p>
    </div>

    <button v-if="!readonly" type="button" class="btn btn-ghost btn-sm" @click="add()">
      <Icon name="plus" />{{ t('rule.add') }}
    </button>

  </div>
</template>
