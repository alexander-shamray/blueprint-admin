import { Component, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { SCREENS } from '../screens';
import { AssistantService } from './assistant-service';
import { ConversationItem, StatusReason, TurnEndReason } from './protocol';

/** Each end reason in the console's words; `done` says nothing, since the reply is the answer (§5.3, §5.4). */
export const END_REASON_TEXT: Record<TurnEndReason, string> = {
  done: '',
  max_steps: $localize`The assistant stopped: it reached its limit of tool calls for one question.`,
  budget: $localize`This conversation is as long as it can get. Start a new one to go on.`,
  cancelled: $localize`Stopped.`,
  refused: $localize`The assistant declined to answer that.`,
  error: $localize`The assistant failed to answer.`,
};

/** Each status reason in the console's words, for when the assistant is not available. */
export const STATUS_REASON_TEXT: Record<StatusReason, string> = {
  off: $localize`The assistant is turned off in the console's configuration.`,
  'no-credential': $localize`The assistant has no credential for its provider.`,
  unreachable: $localize`The assistant's provider cannot be reached.`,
};

/**
 * The host's read tools (§6.2) in the console's words. The host's `AdminReadTools` owns the tools; this is only how the
 * panel names a call to one, and a tool not listed here is still labelled, by its name.
 */
export const READ_TOOL_TEXT: Record<string, string> = {
  stack_status: $localize`Reading the stack`,
  firing_alerts: $localize`Reading the alerts`,
  queue_depths: $localize`Reading the queues`,
  recent_logs: $localize`Reading the log follow`,
  trace_summary: $localize`Reading a trace`,
  list_api_operations: $localize`Reading the API catalogue`,
  list_runbooks: $localize`Reading the runbooks`,
};

/** A tool call's label, by its name and the few args it carries, in the console's words (§5.3). */
export function toolLabel(name: string, args: Record<string, unknown>): string {
  if (name === 'open_screen') {
    const screen = SCREENS.find((s) => s.path === args['path']);
    return $localize`Opening ${screen?.label ?? String(args['path'])}:screen:`;
  }
  return Object.hasOwn(READ_TOOL_TEXT, name) ? READ_TOOL_TEXT[name] : $localize`Using ${name}:tool:`;
}

/**
 * The shell's assistant panel (AI assistant design §2): a view of the tab's one conversation. It streams the reply as
 * it is written, labels each tool call, says plainly when a turn was refused or stopped, and holds no timer: what
 * keeps it current is the conversation's event stream, open while the panel shows or a turn runs.
 */
@Component({
  selector: 'app-assistant-panel',
  imports: [MatButtonModule, MatIconModule],
  templateUrl: './assistant-panel.html',
  styleUrl: './assistant-panel.css',
})
export class AssistantPanel {
  readonly assistant = inject(AssistantService);
  readonly draft = signal('');
  private readonly messages = viewChild<ElementRef<HTMLElement>>('messages');

  readonly state = this.assistant.state;
  readonly provider = computed(() => this.assistant.status()?.provider ?? '');
  readonly items = computed(() => this.state().items.filter((i) => i.kind !== 'end' || i.reason !== 'done'));
  readonly problem = computed(() => {
    const problem = this.state().problem;
    if (!problem) return null;
    switch (problem.code) {
      case 'turn-running':
        return $localize`The assistant is still answering the last question.`;
      case 'gone':
        return $localize`The console no longer holds this conversation; the next message starts a new one.`;
      case 'provider':
        return $localize`The assistant's provider failed: ${problem.detail ?? ''}:reason:`;
      default:
        return $localize`The message was not sent: ${problem.detail ?? ''}:reason:`;
    }
  });

  constructor() {
    // Keeps the newest line in view as the reply streams in.
    effect(() => {
      this.state();
      const list = this.messages()?.nativeElement;
      if (list) queueMicrotask(() => (list.scrollTop = list.scrollHeight));
    });
  }

  label(item: ConversationItem & { kind: 'tool' }): string {
    return toolLabel(item.name, item.args);
  }

  endText(item: ConversationItem & { kind: 'end' }): string {
    return END_REASON_TEXT[item.reason];
  }

  async send(): Promise<void> {
    const text = this.draft().trim();
    if (!text || this.state().running || this.assistant.needsNotice()) return;
    this.draft.set('');
    await this.assistant.send(text);
  }

  onKey(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void this.send();
    }
  }
}
