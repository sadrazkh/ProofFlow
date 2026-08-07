import { describe, expect, it } from 'vitest';
import { accepts } from './graphTypes';

/**
 * The rule that decides whether an edge may be drawn.
 *
 * Stated twice on purpose — once here so the canvas can refuse a connection mid-drag without a
 * round trip, and once on the server because a browser can be made to say anything. Two statements
 * of one rule is exactly the arrangement that drifts, so the cases below are the same table the
 * engine's own test uses.
 */
describe('port type compatibility', () => {
  it.each([
    ['Any', 'Text', true],
    ['Text', 'Any', true],
    ['Text', 'Text', true],
    ['Text', 'Number', false],
    ['Number', 'Text', false],
    ['Json', 'Response', false],
    ['Any', 'Secret', true],
    ['Secret', 'Text', false],
    ['Secret', 'Any', false],
    ['Secret', 'Secret', true],
    ['None', 'Text', false],
  ])('a %s socket accepts %s: %s', (to, from, expected) => {
    expect(accepts(to as string, from as string)).toBe(expected);
  });

  it('never lets a credential be assembled from something that is not one', () => {
    // The asymmetry that matters: a token satisfies "anything", and nothing satisfies "token".
    for (const from of ['Text', 'Json', 'Number', 'Any', 'Response']) {
      expect(accepts('Secret', from)).toBe(false);
    }

    expect(accepts('Any', 'Secret')).toBe(true);
  });
});
