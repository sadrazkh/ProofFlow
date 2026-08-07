/** The wire shapes the baseline screens exchange with the server. Mirrors ProofFlow.Contracts.Baselines. */

export type DiffRow = {
  index: number;
  path: string;
  leaf: string;
  depth: number;
  kind: DiffKind;
  expected: string | null;
  actual: string | null;
  reason: string | null;
  rulePath: string | null;
  ruleKind: string | null;
  hasChildren: boolean;
  hasFindings: boolean;
};

export type DiffKind =
  | 'Unchanged' | 'Added' | 'Removed' | 'Changed'
  | 'TypeChanged' | 'OrderChanged' | 'RuleViolation' | 'Ignored';

export type DiffResult = {
  matches: boolean;
  rows: DiffRow[];
  /**
   * Keyed by the kind's name, not camel-cased: System.Text.Json's web defaults rename properties
   * but leave dictionary keys alone, so 'Added' arrives as 'Added'.
   */
  counts: Record<string, number>;
  findingIndexes: number[];
  invalidRules: string[];
  failureMessage: string | null;
  baselineVersion: string | null;
  statusCode: number | null;
  durationMs: number;
};

export type Rule = {
  id: string | null;
  path: string;
  matcher: string;
  text: string | null;
  number: number | null;
  number2: number | null;
  note: string | null;
  enabled: boolean;
};

export type Suggestion = {
  path: string;
  reason: string;
  confidence: 'Certain' | 'Likely' | 'Possible' | string;
  matcher: string;
  note: string | null;
  sample: string | null;
};

export type BaselineEnvironment = {
  id: string;
  name: string;
  baseUrl: string | null;
  isProduction: boolean;
};

/**
 * The matchers, grouped the way somebody choosing one thinks about them.
 *
 * A flat list of twenty is a wall; the groups are "does it exist", "what shape is it", "what does
 * the text look like", "how close is the number", and "how do the items line up". The labels
 * themselves come from the catalogue at render time — only the keys live here.
 */
export const MATCHER_GROUPS = [
  {
    key: 'presence',
    matchers: ['Ignore', 'Exists', 'NotExists', 'IsNull', 'IsNotNull'],
  },
  {
    key: 'shape',
    matchers: ['Exact', 'TypeOnly', 'JsonSubset'],
  },
  {
    key: 'text',
    matchers: ['Regex', 'Contains', 'StartsWith', 'EndsWith', 'CaseInsensitive', 'Trimmed'],
  },
  {
    key: 'number',
    matchers: ['NumericTolerance', 'NumericRange', 'DateTolerance'],
  },
  {
    key: 'array',
    matchers: ['ArrayOrdered', 'ArrayUnordered', 'ArrayMatchByKey', 'ArrayCount'],
  },
] as const;

/**
 * Which extra fields each matcher needs.
 *
 * Drives what the row shows: a Regex row needs a pattern box, a NumericRange row needs two
 * numbers, and an Ignore row needs neither. Showing all three always would leave most rows with
 * two empty boxes that do nothing, which is how people learn to ignore the boxes that matter.
 */
export const MATCHER_FIELDS: Record<string, { text?: 'pattern' | 'key' | 'value'; number?: string; number2?: string }> = {
  Regex: { text: 'pattern' },
  Contains: { text: 'value' },
  StartsWith: { text: 'value' },
  EndsWith: { text: 'value' },
  ArrayMatchByKey: { text: 'key' },
  NumericTolerance: { number: 'tolerance' },
  NumericRange: { number: 'min', number2: 'max' },
  DateTolerance: { number: 'seconds' },
  ArrayCount: { number: 'min', number2: 'max' },
};

export function emptyRule(path = ''): Rule {
  return {
    id: null, path, matcher: 'Ignore', text: null,
    number: null, number2: null, note: null, enabled: true,
  };
}
