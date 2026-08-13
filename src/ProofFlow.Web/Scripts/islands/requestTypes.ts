/** The wire shapes the request lab exchanges with the server. Mirrors ProofFlow.Contracts.Requests. */

export type KeyValue = { name: string; value: string; enabled: boolean };

export type Unresolved = { reference: string; explanation: string };

export type SendRequestResult = {
  succeeded: boolean;
  resolvedUrl: string | null;
  method: string;
  statusCode: number;
  reasonPhrase: string | null;
  responseHeaders: KeyValue[];
  sentHeaders: KeyValue[];
  body: string;
  contentType: string | null;
  bodyBytes: number;
  durationMs: number;
  attempts: number;
  redirectChain: string[];
  failureKind: string | null;
  failureMessage: string | null;
  failureDetail: string | null;
  unresolved: Unresolved[];
};

export type VariableNames = {
  environment: string[];
  variables: string[];
  secrets: string[];
};

export type LabEnvironment = {
  id: string;
  name: string;
  baseUrl: string | null;
  isProduction: boolean;
};

/** The HTTP verbs the builder offers, with the token each one's chip is painted from. */
export const METHODS = [
  { name: 'GET', tone: 'pass' },
  { name: 'POST', tone: 'running' },
  { name: 'PUT', tone: 'warn' },
  { name: 'PATCH', tone: 'accent' },
  { name: 'DELETE', tone: 'fail' },
  { name: 'HEAD', tone: 'idle' },
  { name: 'OPTIONS', tone: 'idle' },
] as const;

/** Verbs that carry a body. The body tab is hidden for the rest rather than shown and ignored. */
export const METHODS_WITH_BODY = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

/** What an authorisation server said, as the panel needs it. */
export type TokenResult = {
  succeeded: boolean;
  accessToken?: string | null;
  tokenType?: string | null;
  expiresIn?: number | null;
  statusCode: number;
  problem?: string | null;
  detail?: string | null;
};
