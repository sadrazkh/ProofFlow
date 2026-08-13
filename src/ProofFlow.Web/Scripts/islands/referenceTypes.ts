/**
 * What can be written between braces here, and where it came from.
 *
 * Every request in this product is built out of references — an address that starts
 * `{{environment.baseUrl}}`, a header holding `{{secrets.apiToken}}`, a path segment reading an id
 * out of the step before it. Typing them by hand means knowing the scope names, the exact spelling
 * of a variable somebody else defined, and the shape of a response nobody has in front of them.
 *
 * So the product offers them instead. This turns what is available into a flat list of choices, and
 * the picker puts the one that is chosen into the field at the cursor.
 */

/** The names a project publishes. Names only — a secret's value never leaves the server. */
export type ReferenceCatalogue = {
  environment: string[];
  variables: string[];
  secrets: string[];
  inputs: string[];

  /** Steps whose output this field can already read: the ones that run before it. */
  steps: string[];
};

export type ReferenceOption = {
  /** What goes into the field, braces and all. */
  insert: string;

  /** The part shown large: the path without its scope. */
  label: string;

  /** Which group it belongs to, as a translation key suffix. */
  group: ReferenceGroup;

  /** Everything a search should match, lower-cased and joined. */
  haystack: string;
};

export type ReferenceGroup = 'environment' | 'vars' | 'secrets' | 'inputs' | 'steps' | 'run';

export const EMPTY_CATALOGUE: ReferenceCatalogue = {
  environment: [],
  variables: [],
  secrets: [],
  inputs: [],
  steps: [],
};

/**
 * What one finished step offers.
 *
 * Five, not the whole tree. A response has a body of unknown shape, and offering to walk it would
 * mean fetching a run to find out what it looked like last time — this lists the doors, and the
 * field is a text box, so anybody who knows the field they want types the rest.
 */
const STEP_PARTS = [
  'response.statusCode',
  'response.body',
  'response.headers',
  'response.text',
  'response.durationMs',
];

/** What every run knows about itself, whatever is in it. */
const RUN_PARTS = ['run.id', 'run.startedAt', 'run.environment'];

export function referenceOptions(catalogue: ReferenceCatalogue): ReferenceOption[] {
  const options: ReferenceOption[] = [];

  const add = (group: ReferenceGroup, path: string, label: string): void => {
    options.push({
      insert: `{{${path}}}`,
      label,
      group,
      haystack: `${path} ${label}`.toLowerCase(),
    });
  };

  for (const name of catalogue.environment) add('environment', `environment.${name}`, name);
  for (const name of catalogue.variables) add('vars', `vars.${name}`, name);
  for (const name of catalogue.secrets) add('secrets', `secrets.${name}`, name);
  for (const name of catalogue.inputs) add('inputs', `inputs.${name}`, name);

  for (const step of catalogue.steps)
  {
    for (const part of STEP_PARTS) add('steps', `steps.${step}.${part}`, `${step} · ${part}`);
  }

  for (const part of RUN_PARTS) add('run', part, part.slice('run.'.length));

  return options;
}
