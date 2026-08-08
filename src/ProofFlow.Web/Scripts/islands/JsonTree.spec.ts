import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import JsonTree from './JsonTree.vue';

function tree(value: unknown) {
  return mount(JsonTree, { props: { value, path: '$' } });
}

describe('JsonTree', () => {
  it('marks a redacted value as an absence rather than a string', () => {
    const rendered = tree({ token: '«redacted»', name: 'Stable' });

    // The chip is the whole point: rendered in the string colour, "«redacted»" reads as data, and
    // somebody comparing two responses goes looking for a bug in a field they were never shown.
    expect(rendered.find('.chip-redacted').exists()).toBe(true);
    expect(rendered.findAll('.chip-redacted')).toHaveLength(1);

    // And the value itself is nowhere in the output, quotes and all.
    expect(rendered.text()).not.toContain('«redacted»');
    expect(rendered.text()).toContain('Stable');
  });

  it('leaves a value that merely mentions redaction alone', () => {
    // Only an exact match. A response whose body explains a redaction policy is still data, and
    // turning half of it into chips would be the tool editing what it was asked to show.
    const rendered = tree({ note: 'the token was «redacted» by the gateway' });

    expect(rendered.find('.chip-redacted').exists()).toBe(false);
    expect(rendered.text()).toContain('the token was «redacted» by the gateway');
  });
});
