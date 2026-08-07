/** The wire shapes the matrix screens exchange with the server. Mirrors ProofFlow.Contracts.Runs. */

import type { DiffResult, Suggestion } from './baselineTypes';
import type { RunStatus } from './runTypes';

export type MatrixColumn = {
  environmentId: string;
  name: string;
  isProduction: boolean;
};

export type MatrixCell = {
  runId: string;
  status: RunStatus;
  durationMs: number;
  assertionsPassed: number;
  assertionsFailed: number;
  outcome: string | null;
};

export type MatrixRow = {
  scenarioId: string;
  name: string;

  /** One per column, in the columns' order. Null where no run was started for that pairing. */
  cells: (MatrixCell | null)[];
};

export type BatchState = 'Queued' | 'Running' | 'Passed' | 'Failed';

export type Matrix = {
  batchId: string;
  name: string | null;
  state: BatchState;
  total: number;
  done: number;
  startedAt: string;
  finishedAt: string | null;
  columns: MatrixColumn[];
  rows: MatrixRow[];
};

export type ComparisonStep = {
  nodeId: string;
  nodeName: string;
  iteration: number;
  leftStatus: number;
  rightStatus: number;
  leftDurationMs: number;
  rightDurationMs: number;
  diff: DiffResult;
  suggestions: Suggestion[];
};

export type Comparison = {
  batchId: string;
  scenarioId: string;
  leftEnvironmentId: string;
  rightEnvironmentId: string;
  leftName: string;
  rightName: string;
  leftStatus: RunStatus;
  rightStatus: RunStatus;
  leftRunId: string;
  rightRunId: string;
  steps: ComparisonStep[];
  stepsNotShown: number;
  onlyLeft: string[];
  onlyRight: string[];
};

/** A batch is over when nothing is still queued or running; only then does the grid stop polling. */
export function isSettled(state: BatchState): boolean {
  return state === 'Passed' || state === 'Failed';
}
