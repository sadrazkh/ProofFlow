/** The wire shapes the canvas exchanges with the server. Mirrors ProofFlow.Contracts.Scenarios. */

export type PortDto = {
  name: string;
  labelKey: string;
  kind: 'Control' | 'Data';
  type: string;
  isFailure: boolean;
  required: boolean;
};

export type PropertyDto = {
  name: string;
  labelKey: string;
  kind: string;
  required: boolean;
  default: string | null;
  helpKey: string | null;
  placeholder: string | null;
  options: string[];
  visibleWhen: { property: string; values: string[] } | null;
};

export type NodeSpecDto = {
  key: string;
  group: 'Core' | 'Data' | 'Testing' | 'Flow' | 'Auth';
  icon: string;
  inputs: PortDto[];
  outputs: PortDto[];
  properties: PropertyDto[];
  isStart: boolean;
  isTerminal: boolean;
  isContainer: boolean;
  reaches: boolean;
};

export type GraphNodeDto = {
  id: string;
  key: string;
  name: string;
  note: string | null;
  x: number;
  y: number;
  parentId: string | null;
  disabled: boolean;
  properties: Record<string, string | null>;
};

export type GraphEdgeDto = {
  id: string;
  fromId: string;
  fromPort: string;
  toId: string;
  toPort: string;
  label: string | null;
};

export type GraphDto = {
  nodes: GraphNodeDto[];
  edges: GraphEdgeDto[];
  canvasJson: string | null;
};

export type GraphProblem = {
  severity: 'Warning' | 'Error';
  code: string;
  message: string;
  nodeId: string | null;
  port: string | null;
  property: string | null;
};

export type SaveGraphResult = {
  versionId: string;
  number: number;
  isValid: boolean;
  problems: GraphProblem[];
  nodeIds: Record<string, string>;
};

/** The palette's order, which is a claim about what somebody reaches for first. */
export const GROUPS = ['Core', 'Data', 'Testing', 'Flow', 'Auth'] as const;

/**
 * The eight states a node can be in during a run.
 *
 * Defined here rather than in the run console because the canvas draws them: watching a run means
 * watching the ring around each node change, on the same picture that was drawn to build the test.
 */
export const NODE_STATES = [
  'idle', 'running', 'passed', 'failed', 'skipped', 'waiting', 'retrying', 'cancelled',
] as const;

export type NodeState = typeof NODE_STATES[number];

/**
 * Whether a value of one type may be plugged into a socket wanting another.
 *
 * The same rule as the server's, on purpose and stated twice: the canvas needs it while an edge is
 * being dragged, which is before any round trip, and the server needs it because a browser can be
 * made to say anything. A test asserts the two agree.
 */
export function accepts(to: string, from: string): boolean {
  if (to === from) return true;
  if (to === 'None' || from === 'None') return false;
  if (to === 'Secret') return false;
  return to === 'Any' || from === 'Any';
}
