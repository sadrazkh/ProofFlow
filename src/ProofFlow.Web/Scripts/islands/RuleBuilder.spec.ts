import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import RuleBuilder from './RuleBuilder.vue';
import SuggestionList from './SuggestionList.vue';
import { MATCHER_GROUPS, emptyRule, type Rule, type Suggestion } from './baselineTypes';

function withRules(rules: Rule[], readonly = false) {
  return mount(RuleBuilder, { props: { modelValue: rules, readonly } });
}

describe('RuleBuilder', () => {
  it('shows only the parameters the chosen matcher needs', async () => {
    const builder = withRules([{ ...emptyRule('$.id'), matcher: 'Regex' }]);
    expect(builder.findAll('.rule-params input')).toHaveLength(1);

    await builder.find('.rule-matcher select').setValue('NumericRange');
    expect(builder.findAll('.rule-params input')).toHaveLength(2);

    await builder.find('.rule-matcher select').setValue('Ignore');
    expect(builder.findAll('.rule-params input')).toHaveLength(0);
    expect(builder.find('.rule-noparams').exists()).toBe(true);
  });

  it('clears the old parameters even when the new matcher has a slot for them', async () => {
    // The dangerous case: ArrayCount also reads `number`, so a ±5 tolerance would land in the
    // minimum-count box and become "at least five items" — a rule nobody wrote.
    const rules: Rule[] = [{ ...emptyRule('$.score'), matcher: 'NumericTolerance', number: 5 }];
    const builder = withRules(rules);

    await builder.find('.rule-matcher select').setValue('ArrayCount');

    expect(rules[0]!.matcher).toBe('ArrayCount');
    expect(rules[0]!.number).toBeNull();
  });

  it('clears a pattern when the matcher stops being a text one', async () => {
    const rules: Rule[] = [{ ...emptyRule('$.id'), matcher: 'Regex', text: '^[0-9]+$' }];
    const builder = withRules(rules);

    await builder.find('.rule-matcher select').setValue('Contains');
    expect(rules[0]!.text).toBeNull();
  });

  it('explains every matcher in a sentence', async () => {
    const builder = withRules([emptyRule('$.a')]);

    // Against the real catalogue: a matcher with no help string would render its own key here,
    // and every one of these assertions would fail on the way past.
    for (const matcher of MATCHER_GROUPS.flatMap((group) => group.matchers)) {
      await builder.find('.rule-matcher select').setValue(matcher);

      const hint = builder.find('.rule-hint').text();
      expect(hint).not.toContain('matcher.');
      expect(hint.length).toBeGreaterThan(20);
    }
  });

  it('offers no editing at all when the reader cannot record', () => {
    const builder = withRules([emptyRule('$.a')], true);

    expect(builder.find('.rule-path input').attributes('disabled')).toBeDefined();
    expect(builder.find('.rule-actions button').exists()).toBe(false);
    expect(builder.findAll('button')).toHaveLength(0);
  });

  it('adds and removes rows', async () => {
    const builder = withRules([emptyRule('$.a')]);

    await builder.find('.rule-builder > button').trigger('click');
    expect(builder.emitted('update:modelValue')!.at(-1)![0]).toHaveLength(2);
  });
});

const SUGGESTIONS: Suggestion[] = [
  { path: '$.requestId', reason: 'Guid', confidence: 'Certain', matcher: 'Ignore', note: null, sample: 'a3f…' },
  { path: '$.timestamp', reason: 'Timestamp', confidence: 'Likely', matcher: 'Ignore', note: null, sample: null },
];

describe('SuggestionList', () => {
  it('starts with nothing ticked, however sure the detector is', () => {
    // Section 12 of the brief: a field is never excluded from checking without somebody deciding
    // to exclude it. A pre-ticked "Certain" row is that decision made on their behalf.
    const list = mount(SuggestionList, {
      props: { suggestions: SUGGESTIONS, accepted: [], readonly: false },
    });

    const boxes = list.findAll('input[type="checkbox"]');
    expect(boxes).toHaveLength(2);
    expect(boxes.every((box) => (box.element as HTMLInputElement).checked)).toBe(false);
  });

  it('reports one path per tick', async () => {
    const list = mount(SuggestionList, {
      props: { suggestions: SUGGESTIONS, accepted: [], readonly: false },
    });

    await list.findAll('input[type="checkbox"]')[1]!.setValue(true);
    expect(list.emitted('update:accepted')![0]![0]).toEqual(['$.timestamp']);
  });

  it('shows the evidence beside the proposal', () => {
    const list = mount(SuggestionList, {
      props: { suggestions: SUGGESTIONS, accepted: [], readonly: false },
    });

    expect(list.text()).toContain('$.requestId');
    expect(list.text()).toContain('a3f…');
    expect(list.text()).toContain('Looks like a generated id');
    expect(list.text()).toContain('Certain');
  });

  it('renders nothing when there is nothing to suggest', () => {
    const list = mount(SuggestionList, { props: { suggestions: [], accepted: [], readonly: false } });
    expect(list.find('.suggestions').exists()).toBe(false);
  });
});
