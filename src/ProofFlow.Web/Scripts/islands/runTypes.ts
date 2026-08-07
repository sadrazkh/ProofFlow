/** The wire shapes the run console exchanges with the server. Mirrors ProofFlow.Web.ViewModels. */

import type { NodeState } from './graphTypes';

export type RunStatus =
  | 'Queued' | 'Running' | 'Passed' | 'Failed' | 'Cancelled' | 'Errored';

export type NodeRunStatus =
  | 'Idle' | 'Running' | 'Passed' | 'Failed' | 'Skipped' | 'Waiting' | 'Retrying' | 'Cancelled';

export type LogLevel = 'Debug' | 'Info' | 'Warning' | 'Error';

export type NodeRunRow = {
  id: string;
  nodeId: string;
  nodeName: string;
  nodeKey: string;
  status: NodeRunStatus;
  iteration: number;
  attempt: number;
  durationMs: number;
  takenPort: string | null;
  failureMessage: string | null;
  startedAt: string;
};

export type AssertionRow = {
  nodeRunId: string;
  description: string;
  passed: boolean;
  soft: boolean;
  expected: string | null;
  actual: string | null;
  target: string | null;
};

export type RunEventRow = {
  sequence: number;
  level: LogLevel;
  message: string;
  nodeId: string | null;
  nodeName: string | null;
  at: string;
  dataJson: string | null;
};

export type RunTotals = {
  steps: number;
  stepsFailed: number;
  assertionsPassed: number;
  assertionsFailed: number;
  durationMs: number;
};

export type RunState = {
  status: RunStatus;
  outcome: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  totals: RunTotals;
  graph: string | null;
  nodes: NodeRunRow[];
  assertions: AssertionRow[];
  events: RunEventRow[];
};

/** What arrives on the live connection while a run goes. */
export type NodeUpdate = {
  nodeId: string;
  nodeName: string;
  status: NodeRunStatus;
  iteration: number;
  attempt: number;
  durationMs: number;
  takenPort: string | null;
  failure: string | null;
};

export type AssertionUpdate = {
  nodeId: string;
  description: string;
  passed: boolean;
  soft: boolean;
  target: string | null;
};

/** A run is over when its status is one of these, and only then does the console stop following. */
export const TERMINAL: readonly RunStatus[] = ['Passed', 'Failed', 'Cancelled', 'Errored'];

export function isOver(status: RunStatus): boolean {
  return TERMINAL.includes(status);
}

/**
 * The server's node status as the canvas draws it.
 *
 * The two sets are deliberately the same eight — watching a run is watching the picture the test
 * was built on — so this is only a change of case.
 */
export function toNodeState(status: NodeRunStatus): NodeState {
  // Defensive because the failure mode is silent: a status that arrived as a number would throw
  // here, abandon the render, and leave a blank pane with nothing in the console to explain it.
  if (typeof status !== 'string') {
    console.warn('A node status arrived as', status, '— expected a word.');
    return 'idle';
  }

  return status.toLowerCase() as NodeState;
}

/**
 * The classes for a run's verdict, shared by the history table and the console header.
 *
 * Two of them: the badge colours the chip, and the status class colours the dot inside it. The dot
 * is not decoration — a verdict told only in green and red is a verdict one reader in twelve cannot
 * read, so every state is a dot, a colour and a word.
 */
export function statusClass(status: RunStatus): string {
  switch (status) {
    case 'Passed': return 'badge-pass status-pass';
    case 'Failed': return 'badge-fail status-fail';
    case 'Errored': return 'badge-warn status-warn';
    case 'Running': return 'badge-running status-running';
    default: return 'badge-idle status-idle';
  }
}
