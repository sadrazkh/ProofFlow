/** The wire shapes the data-set and capture screens exchange with the server. */

export type DataRow = Record<string, string>;

export type DataSetDraft = {
  columns: string[];
  rows: DataRow[];
  keyColumn: string | null;
  description: string | null;
};

export type PasteProblem = { line: number; text: string; reason: string };

export type ParsedPaste = {
  /** Csv, Tsv, Json, Lines or Empty — named so the interface can say what it thinks it read. */
  format: string;
  columns: string[];
  rows: DataRow[];
  problems: PasteProblem[];
  totalLines: number;
};

/**
 * The formats the reader can force.
 *
 * Offered because the parser guesses, and a guess about somebody's data has to be overrulable. The
 * order is the order they are met: a list is the commonest paste, then a spreadsheet.
 */
export const PASTE_FORMATS = ['Lines', 'Csv', 'Tsv', 'Json'] as const;

// ------------------------------------------------------------------------------------------------

export type SampleStatus =
  | 'Captured' | 'Reviewed' | 'Approved' | 'Rejected' | 'Outdated' | 'Failed';

export type SampleRow = {
  id: string;
  key: string;
  ordinal: number;
  status: SampleStatus;
  differs: boolean;
  statusCode: number;
  durationMs: number;
  failureMessage: string | null;
  diffCounts: Record<string, number>;
  reviewNote: string | null;
};

export type CaptureSessionState = {
  id: string;
  mode: string;
  status: string;
  totalRows: number;
  completed: number;
  differing: number;
  failed: number;
  stoppedReason: string | null;
  counts: Record<string, number>;
};

export type SamplePage = {
  session: CaptureSessionState;
  total: number;
  rows: SampleRow[];
};

/**
 * The six states, in the order a sample moves through them.
 *
 * Ordered rather than alphabetical because the filter bar reads as a life cycle: what nobody has
 * looked at, what somebody looked at, the two decisions, and the two ways a sample stops being a
 * decision anyone can make.
 */
export const SAMPLE_STATUSES: { status: SampleStatus; tone: string; icon: string }[] = [
  { status: 'Captured', tone: 'badge-running', icon: 'circle-dot' },
  { status: 'Reviewed', tone: 'badge-accent', icon: 'eye' },
  { status: 'Approved', tone: 'badge-pass', icon: 'circle-check' },
  { status: 'Rejected', tone: 'badge-fail', icon: 'circle-slash' },
  { status: 'Outdated', tone: 'badge-warn', icon: 'history' },
  { status: 'Failed', tone: 'badge-fail', icon: 'triangle-alert' },
];

export function toneFor(status: SampleStatus): string {
  return SAMPLE_STATUSES.find((entry) => entry.status === status)?.tone ?? 'badge-idle';
}

export function iconFor(status: SampleStatus): string {
  return SAMPLE_STATUSES.find((entry) => entry.status === status)?.icon ?? 'circle-dot';
}
