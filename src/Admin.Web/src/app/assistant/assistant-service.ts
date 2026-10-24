import { DOCUMENT } from '@angular/common';
import { Injectable, LOCALE_ID, computed, effect, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HostClient } from '../core/host/host-client';
import { TraceFollow } from '../features/trace/trace-follow';
import { SCREENS } from '../screens';
import { builtInScreensHolding, builtInWidgets, widgetsOf } from '../workspace/built-in-pages';
import { WIDGETS } from '../workspace/widgets';
import { RunningWork } from '../workspace/running-work';
import { WorkspaceService } from '../workspace/workspace-service';
import {
  AssistantClient,
  AssistantConversation,
  ConversationState,
  EMPTY_CONVERSATION,
  IdStore,
} from './assistant-client';
import { ClientToolRegistry } from './client-tools';
import { highlightWidget } from './highlight';
import { AssistantStatus, CatalogueEntry, OpenConversation } from './protocol';
import { findWidgetType, registerWorkspaceTools } from './workspace-tools';

/** Where the tab keeps its conversation's id, so a reload (a language switch among them) resumes it (§5.5). */
export const CONVERSATION_KEY = 'admin.assistant.conversation';

/** Where a widget type is found when no built-in page holds it. */
const ADD_WIDGET = 'on no built-in page: add it to a page with Add widget in the layout editor';

/** The prefix of a widget type's catalogue id, so it never meets a screen's. */
export const WIDGET_ID_PREFIX = 'widget:';

/**
 * The catalogue the assistant explains (§6.1): each screen, then each widget type, with its label as shown, where it
 * lives and its `help`. A screen's `help` is from `SCREENS` and names the widgets its page holds; a widget's is its own,
 * from `WIDGETS`, and its place is the built-in pages that hold it.
 */
export function catalogue(): CatalogueEntry[] {
  const screens = SCREENS.map((s) => ({ id: s.path, label: s.label, where: `/${s.path}`, help: s.help }));
  const widgets = WIDGETS.map((w) => {
    const holding = builtInScreensHolding(w.type);
    return {
      id: `${WIDGET_ID_PREFIX}${w.type}`,
      label: w.title,
      where: holding.length === 0 ? ADD_WIDGET : holding.map((path) => `/${path}`).join(', '),
      help: w.help,
    };
  });
  return [...screens, ...widgets];
}

/** Where the browser keeps the providers whose notice the operator accepted (§10): a JSON list of provider ids. */
export const ACKNOWLEDGED_KEY = 'admin.assistant.acknowledged';

/** The provider that sends nothing out of the console, so it needs no notice: `AssistantOptions.Fake` on the host. */
const LOCAL_PROVIDER = 'fake';

/** Where the tab keeps the realm user the operator pinned for the assistant's platform reads (§6.5); absent is automatic. */
export const IDENTITY_KEY = 'admin.assistant.identity';

function pinnedIdentity(): string | null {
  try {
    return sessionStorage.getItem(IDENTITY_KEY);
  } catch {
    return null;
  }
}

function acknowledgedProviders(): string[] {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(ACKNOWLEDGED_KEY) ?? '[]');
    return Array.isArray(stored) ? stored.filter((p): p is string => typeof p === 'string') : [];
  } catch {
    return [];
  }
}

const tabStore: IdStore = {
  get: () => {
    try {
      return sessionStorage.getItem(CONVERSATION_KEY);
    } catch {
      return null;
    }
  },
  set: (id) => {
    try {
      if (id) sessionStorage.setItem(CONVERSATION_KEY, id);
      else sessionStorage.removeItem(CONVERSATION_KEY);
    } catch {
      // A tab that cannot store keeps its conversation until it reloads.
    }
  },
};

/**
 * The console's one assistant conversation per tab (AI assistant design §2), held app-wide: the shell's panel is a view
 * of it. The status is read once at startup; off, nothing else of the assistant runs (§9.1). The client tools it
 * offers route the SPA and nothing else (§6.4).
 */
@Injectable({ providedIn: 'root' })
export class AssistantService {
  private readonly router = inject(Router);
  private readonly locale = inject(LOCALE_ID);
  private readonly workspace = inject(WorkspaceService);
  private readonly document = inject(DOCUMENT);
  private readonly host = inject(HostClient);
  private readonly traceFollow = inject(TraceFollow);
  private readonly runningWork = inject(RunningWork);
  private readonly client = new AssistantClient();
  private readonly tools = new ClientToolRegistry();
  private readonly conversation: AssistantConversation;
  /** Where the running turn was asked from: the running-work indicator links back to it. */
  private readonly askedFrom = signal('/');

  readonly status = signal<AssistantStatus | null>(null);
  readonly enabled = computed(() => this.status()?.enabled === true);
  readonly panelOpen = signal(false);
  readonly state = signal<ConversationState>(EMPTY_CONVERSATION);

  private readonly acknowledged = signal<string[]>(acknowledgedProviders());

  /**
   * The realm user the platform reads are made as (§6.5), or null for the host's choice by each route's policy. Kept for
   * the tab, and sent as `hostOptions.identity` with every turn.
   */
  readonly identity = signal<string | null>(pinnedIdentity());

  /** The realm users the console can read as: usernames only, from the host, loaded when the panel first shows. */
  readonly users = signal<string[]>([]);

  /**
   * The first message of a browser waits on a one-time notice that questions and the console data the assistant reads
   * go to the provider (§10). A provider that sends nothing out needs none, and an unknown one is treated as sending.
   */
  readonly needsNotice = computed(() => {
    const status = this.status();
    return (
      status?.enabled === true &&
      status.provider !== null &&
      status.provider !== LOCAL_PROVIDER &&
      !this.acknowledged().includes(status.provider)
    );
  });

  constructor() {
    registerWorkspaceTools(this.tools, {
      pages: () => this.workspace.pages(),
      currentPage: () => this.workspace.currentPage(),
      widgetsOf: (page) => widgetsOf(page),
      open: (page) => this.router.navigate(this.workspace.pageLink(page.id)),
      highlight: (instanceId) => highlightWidget(this.document, instanceId),
      followTrace: (correlationId) => this.traceFollow.open(correlationId),
    });
    this.conversation = new AssistantConversation(
      this.client,
      this.tools,
      () => this.opening(),
      tabStore,
      (s) => this.state.set(s),
    );
    // Shown means the panel is open in a tab the operator can see (widget-workspace spec §4.3): a hidden tab holds no
    // stream for an idle conversation, though a running turn keeps its own.
    const tabVisible = signal(this.document.visibilityState !== 'hidden');
    this.document.addEventListener('visibilitychange', () =>
      tabVisible.set(this.document.visibilityState !== 'hidden'),
    );
    effect(() => this.conversation.setVisible(this.panelOpen() && tabVisible()));
    // What shows a turn is the panel, not a page, so the item opens it; the link is where the question was asked.
    this.runningWork.report('assistant', () =>
      this.state().running
        ? { text: $localize`The assistant is answering`, link: this.askedFrom(), open: () => this.open() }
        : null,
    );
  }

  /** Reads the status once, and when the assistant is on, redraws the tab's conversation. */
  async load(): Promise<void> {
    const status = await this.client.status();
    this.status.set(status);
    if (status?.enabled) await this.conversation.resume();
  }

  toggle(): void {
    if (this.enabled()) this.panelOpen.update((open) => !open);
  }

  open(): void {
    if (this.enabled()) this.panelOpen.set(true);
  }

  /** The operator accepted that this provider is sent their questions; kept for the browser, per provider. */
  acknowledge(): void {
    const provider = this.status()?.provider;
    if (!provider || this.acknowledged().includes(provider)) return;
    const accepted = [...this.acknowledged(), provider];
    this.acknowledged.set(accepted);
    try {
      localStorage.setItem(ACKNOWLEDGED_KEY, JSON.stringify(accepted));
    } catch {
      // A browser that cannot store asks again at the next load.
    }
  }

  async send(text: string): Promise<void> {
    // The panel does not offer Send before the notice is accepted; this is the guard behind it.
    if (this.needsNotice()) return;
    this.askedFrom.set(this.router.url);
    return this.conversation.send(text, this.context(), this.locale, this.hostOptions());
  }

  /**
   * The operator's click on a proposed widget (`propose_widget`, §6.4): adds it to the page being shown by the workspace's
   * own add path, as the Add widget menu does. False when there is no such type, no page, or no room below the page's
   * last widget.
   */
  addProposed(type: string): boolean {
    const kind = findWidgetType(type);
    const pageId = this.workspace.currentPageId();
    if (!kind || !pageId) return false;
    this.workspace.editing.set(true);
    return this.workspace.addWidget(pageId, {
      type: kind.type,
      cols: kind.defaultSize.cols,
      rows: kind.defaultSize.rows,
      config: structuredClone(kind.presets[0].config),
    });
  }

  /** Pins the realm user for the platform reads, or null to leave it to the host. */
  pin(username: string | null): void {
    this.identity.set(username);
    try {
      if (username) sessionStorage.setItem(IDENTITY_KEY, username);
      else sessionStorage.removeItem(IDENTITY_KEY);
    } catch {
      // A tab that cannot store keeps the choice until it reloads.
    }
  }

  /** The usernames the host offers; the last list is kept if the host does not answer. */
  loadUsers(): void {
    this.host.identityUsers().subscribe({
      next: (users) => {
        const names = users.map((u) => u.username);
        this.users.set(names);
        // A pin the host no longer lists would be refused by every read, behind a picker that shows nothing chosen.
        const pinned = this.identity();
        if (pinned !== null && !names.includes(pinned)) this.pin(null);
      },
      error: () => undefined,
    });
  }

  /**
   * An empty object, not null, when nothing is pinned: the host keeps a conversation's options from the turn before when a
   * turn sends none, so taking the pin off has to say so.
   */
  private hostOptions(): Record<string, unknown> {
    const identity = this.identity();
    const listed = this.users();
    // Once the host has named its users, a pin it does not list is not sent. Before that, or if it never answers, the pin
    // goes as it is and the host refuses an unknown name, saying so.
    return identity && (listed.length === 0 || listed.includes(identity)) ? { identity } : {};
  }

  cancel(): Promise<void> {
    return this.conversation.cancel();
  }

  startOver(): Promise<void> {
    return this.conversation.startOver();
  }

  private opening(): OpenConversation {
    return {
      client: { name: 'blueprint-admin', version: '1' },
      locale: this.locale,
      catalogue: catalogue(),
      clientTools: this.tools.descriptors(),
      hostOptions: this.hostOptions(),
    };
  }

  /** What the operator is looking at (§5.1): the page and the widgets on it, by type. */
  private context(): Record<string, unknown> {
    const page = this.workspace.currentPage();
    const widgets = page ? (page.widgets ?? (page.builtIn ? builtInWidgets(page.builtIn) : [])) : [];
    return { url: this.router.url, page: page?.title ?? null, widgets: widgets.map((w) => w.type) };
  }
}
