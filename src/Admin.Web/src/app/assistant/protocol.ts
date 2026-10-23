/**
 * The assistant's protocol (AI assistant design §5), mirrored from its owner, the C# records under
 * `src/Admin.Assistant/Protocol/` (`Resources.cs`, `Events.cs`) and `src/Admin.Assistant/Tools/ToolDescriptor.cs`.
 * The golden files beside them pin the JSON, and `protocol.spec.ts` reads them, so this mirror cannot drift from its
 * owner. Framework-free: nothing here imports Angular. Every code below is a code, never a sentence; a client words it
 * in its own language (§5.4).
 */

/** Why the assistant is not enabled. */
export type StatusReason = 'off' | 'no-credential' | 'unreachable';

export interface AssistantStatus {
  enabled: boolean;
  provider: string | null;
  model: string | null;
  reason: StatusReason | null;
}

export type ToolSide = 'server' | 'client';

/** A tool in the Model Context Protocol's shape, plus its side (§6). */
export interface ToolDescriptor {
  name: string;
  title: string;
  description: string;
  inputSchema: Record<string, unknown>;
  annotations: { readOnlyHint: boolean; destructiveHint: boolean; idempotentHint: boolean; openWorldHint: boolean };
  side: ToolSide;
}

/** One thing the client can explain (§6.1), its label as the client shows it in the active locale. */
export interface CatalogueEntry {
  id: string;
  label: string;
  where: string;
  help: string;
}

export interface OpenConversation {
  client: { name: string; version: string };
  locale: string;
  catalogue: CatalogueEntry[];
  clientTools: ToolDescriptor[];
  hostOptions: Record<string, unknown> | null;
}

export interface ConversationOpened {
  id: string;
}

export interface StartTurn {
  text: string;
  context: Record<string, unknown> | null;
  locale: string | null;
  hostOptions: Record<string, unknown> | null;
}

export interface ClientToolResult {
  callId: string;
  ok: boolean;
  content: string;
}

export type TurnEndReason = 'done' | 'max_steps' | 'budget' | 'cancelled' | 'refused' | 'error';

/** Every end reason, so a spec can walk them and hold each to a translated message. */
export const TURN_END_REASONS: readonly TurnEndReason[] = ['done', 'max_steps', 'budget', 'cancelled', 'refused', 'error'];

/** Every status reason, for the same walk. */
export const STATUS_REASONS: readonly StatusReason[] = ['off', 'no-credential', 'unreachable'];

export type ConversationItem =
  | { kind: 'operator'; text: string }
  | { kind: 'reply'; text: string }
  | { kind: 'tool'; callId: string; name: string; side: ToolSide; args: Record<string, unknown>; ok: boolean | null }
  | { kind: 'end'; reason: TurnEndReason; category: string | null };

export interface ConversationView {
  id: string;
  items: ConversationItem[];
  running: boolean;
  lastEventId: number;
}

export interface Usage {
  input: number;
  output: number;
  cached: number;
}

/** Each Server-Sent Event's data, by its event name (§5.3). */
export interface AssistantEvents {
  text: { delta: string };
  tool_started: { callId: string; name: string; side: ToolSide; args: Record<string, unknown> };
  tool_finished: { callId: string; ok: boolean };
  client_tool_call: { callId: string; name: string; input: Record<string, unknown> };
  turn_ended: { reason: TurnEndReason; category: string | null; usage: Usage; total: Usage };
  error: { type: string; title: string; status: number; detail: string | null };
  /** Reserved (§6.3): never emitted until actions are allowed, and ignored until then. */
  confirmation_required: { callId: string; name: string; args: Record<string, unknown>; confirmation: string | null };
}

export type AssistantEventName = keyof AssistantEvents;

/** The event names a client listens for; `confirmation_required` is reserved and left out on purpose. */
export const EVENT_NAMES: readonly AssistantEventName[] = [
  'text',
  'tool_started',
  'tool_finished',
  'client_tool_call',
  'turn_ended',
  'error',
];

export type AssistantEvent = { [K in AssistantEventName]: { name: K; data: AssistantEvents[K]; id: number } }[AssistantEventName];
