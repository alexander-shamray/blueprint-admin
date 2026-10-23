import clientToolResult from '../../../../Admin.Assistant/Protocol/golden/client-tool-result.json';
import conversationOpened from '../../../../Admin.Assistant/Protocol/golden/conversation-opened.json';
import conversationView from '../../../../Admin.Assistant/Protocol/golden/conversation-view.json';
import eventClientToolCall from '../../../../Admin.Assistant/Protocol/golden/event-client-tool-call.json';
import eventConfirmationRequired from '../../../../Admin.Assistant/Protocol/golden/event-confirmation-required.json';
import eventError from '../../../../Admin.Assistant/Protocol/golden/event-error.json';
import eventText from '../../../../Admin.Assistant/Protocol/golden/event-text.json';
import eventToolFinished from '../../../../Admin.Assistant/Protocol/golden/event-tool-finished.json';
import eventToolStarted from '../../../../Admin.Assistant/Protocol/golden/event-tool-started.json';
import eventTurnEnded from '../../../../Admin.Assistant/Protocol/golden/event-turn-ended.json';
import openConversation from '../../../../Admin.Assistant/Protocol/golden/open-conversation.json';
import startTurn from '../../../../Admin.Assistant/Protocol/golden/start-turn.json';
import statusEnabled from '../../../../Admin.Assistant/Protocol/golden/status-enabled.json';
import statusOff from '../../../../Admin.Assistant/Protocol/golden/status-off.json';
import {
  AssistantEvents,
  AssistantStatus,
  ClientToolResult,
  ConversationOpened,
  ConversationView,
  OpenConversation,
  StartTurn,
} from './protocol';

/**
 * The TypeScript mirror held to its owner (AI assistant design §14, "Protocol"): each golden file the C# records are
 * tested against equals a value typed here, so a field renamed, added or retyped on either side fails one of the two.
 */
describe('the assistant protocol mirror', () => {
  const tool = {
    name: 'open_screen',
    title: 'Open a screen',
    description: 'Routes the console to a screen by its id.',
    inputSchema: { type: 'object', properties: { path: { type: 'string' } }, required: ['path'], additionalProperties: false },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    side: 'client',
  } as const;

  it('reads every body the host writes', () => {
    expect(statusEnabled).toEqual({ enabled: true, provider: 'fake', model: 'scripted', reason: null } satisfies AssistantStatus);
    expect(statusOff).toEqual({ enabled: false, provider: null, model: null, reason: 'off' } satisfies AssistantStatus);
    expect(conversationOpened).toEqual({ id: '0123456789abcdef0123456789abcdef' } satisfies ConversationOpened);
    expect(conversationView).toEqual({
      id: '0123456789abcdef0123456789abcdef',
      items: [
        { kind: 'operator', text: 'где логи?' },
        { kind: 'reply', text: 'Открываю «Logs». ' },
        { kind: 'tool', callId: 'call-1', name: 'open_screen', side: 'client', args: { path: 'logs' }, ok: true },
        { kind: 'reply', text: 'Экран «Logs» открыт.' },
        { kind: 'end', reason: 'done', category: null },
      ],
      running: false,
      lastEventId: 7,
    } satisfies ConversationView);
  });

  it('writes every body the host reads', () => {
    expect(openConversation).toEqual({
      client: { name: 'blueprint-admin', version: '1' },
      locale: 'ru-RU',
      catalogue: [{ id: 'logs', label: 'Logs', where: '/logs', help: "Follows the platform's log lines." }],
      clientTools: [tool],
      hostOptions: { identity: 'demo' },
    } satisfies OpenConversation);
    expect(startTurn).toEqual({ text: 'где логи?', context: { page: 'stack' }, locale: 'ru-RU', hostOptions: null } satisfies StartTurn);
    expect(clientToolResult).toEqual({ callId: 'call-1', ok: true, content: 'Opened /logs.' } satisfies ClientToolResult);
  });

  it('reads every event', () => {
    const events: { [K in keyof AssistantEvents]: [unknown, AssistantEvents[K]] } = {
      text: [eventText, { delta: 'Открываю «Logs». ' }],
      tool_started: [eventToolStarted, { callId: 'call-1', name: 'open_screen', side: 'client', args: { path: 'logs' } }],
      tool_finished: [eventToolFinished, { callId: 'call-1', ok: true }],
      client_tool_call: [eventClientToolCall, { callId: 'call-1', name: 'open_screen', input: { path: 'logs' } }],
      turn_ended: [
        eventTurnEnded,
        { reason: 'refused', category: 'cyber', usage: { input: 120, output: 8, cached: 100 }, total: { input: 240, output: 30, cached: 100 } },
      ],
      error: [eventError, { type: 'provider-error', title: "The assistant's provider failed", status: 502, detail: 'connection reset' }],
      confirmation_required: [eventConfirmationRequired, { callId: 'call-2', name: 'stack_down', args: { wipe: true }, confirmation: 'down -v' }],
    };
    for (const [golden, typed] of Object.values(events)) expect(golden).toEqual(typed);
  });
});
