import { Component, DestroyRef, ElementRef, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { BrokerFeed } from '../../../features/broker/broker-feed';
import { MapFeed } from '../../../features/map/map-feed';
import { StackFeed } from '../../../features/stack/stack-feed';
import { widgetsOf } from '../../built-in-pages';
import { pollWhenOn } from '../../polled-feed';
import { injectWidgetContext } from '../../widget-context';
import { WorkspaceService } from '../../workspace-service';
import { WidgetSessionStore } from '../../widget-session-store';
import { GRAPH_FACTORY, GraphView } from './graph-loader';
import { Position, TopologyNode, positionsOf, topologyOf } from './topology-graph';

/**
 * The platform as it is now (widget-workspace spec §8.1): the gateway, the services, their queues and the exchanges the
 * queues are bound from, drawn with Cytoscape and the dagre layout, coloured by what the stack and the broker say, and
 * read again on the Map's 5 s while the widget shows. Nothing is drawn by hand: the host's map and the stack are the
 * data, and a place where the map and the tables disagree is listed under the drawing, never drawn as resolved. The same
 * nodes are listed in words under it, which is also how a keyboard reaches one.
 *
 * Config: `autoRefresh`, on until switched off, and `positions`, where the operator dragged a node to; Re-layout
 * empties it. Session state: none.
 */
@Component({
  selector: 'app-topology-widget',
  templateUrl: './topology-widget.html',
  styleUrl: './topology-widget.css',
  preserveWhitespaces: true,
})
export class TopologyWidget {
  readonly feed = inject(MapFeed);
  private readonly stack = inject(StackFeed);
  private readonly broker = inject(BrokerFeed);
  private readonly workspace = inject(WorkspaceService);
  private readonly sessions = inject(WidgetSessionStore);
  private readonly router = inject(Router);
  private readonly context = injectWidgetContext();
  private readonly createGraph = inject(GRAPH_FACTORY);

  readonly config = input<Record<string, unknown>>({});
  private readonly canvas = viewChild.required<ElementRef<HTMLElement>>('canvas');

  readonly autoRefresh = computed(() => this.config()['autoRefresh'] !== false);
  private readonly positions = computed(() => positionsOf(this.config()['positions']));

  /** The graph the map and the stack make; null until the map has answered. */
  readonly topology = computed(() => {
    const map = this.feed.map.value();
    if (!map) return null;
    const rates = this.feed.rates();
    return topologyOf(map, { stack: this.stack.stack.value(), rate: (q) => rates.get(q), rateText: (q) => this.feed.rateOf(q) });
  });

  /** The nodes the operator can click, each with the state in words. */
  readonly listed = computed(() => (this.topology()?.nodes ?? []).filter((n) => n.kind !== 'exchange'));

  readonly summary = computed(() => {
    const nodes = this.topology()?.nodes ?? [];
    const count = (kind: TopologyNode['kind']): number => nodes.filter((n) => n.kind === kind).length;
    return `Platform diagram: ${count('service') + count('gateway')} services and ${count('queue')} queues. The same nodes are listed under it.`;
  });

  /** The library could not be fetched or the drawing failed. */
  readonly drawError = signal<string | null>(null);
  /** What the last click did, when it was not obvious from where it went. */
  readonly notice = signal<string | null>(null);

  private view: GraphView | null = null;
  private readonly drawn = signal<GraphView | null>(null);

  constructor() {
    this.feed.show(inject(DestroyRef));
    pollWhenOn(this.feed.map, this.autoRefresh);
    // The stack is read with each map answer, so the colours and the depths come from the same moment and the 5 s is the
    // Map's alone; the Stack's own 3 s tick runs only while a Stack widget is showing.
    effect(() => {
      this.feed.map.value();
      untracked(() => this.stack.stack.refresh());
    });

    effect((onCleanup) => {
      const container = this.canvas().nativeElement;
      let gone = false;
      let made: GraphView | undefined;
      this.createGraph(container, { tap: (id) => this.activate(id), moved: (id, at) => this.moved(id, at) }).then(
        (view) => {
          if (gone) {
            view.destroy();
            return;
          }
          made = view;
          this.view = view;
          this.drawn.set(view);
        },
        (e: unknown) => {
          if (!gone) this.drawError.set(e instanceof Error ? e.message : String(e));
        },
      );
      onCleanup(() => {
        gone = true;
        made?.destroy();
        this.view = null;
        untracked(() => this.drawn.set(null));
      });
    });

    effect(() => {
      const view = this.drawn();
      const topology = this.topology();
      const positions = this.positions();
      if (view && topology) view.show(topology, positions);
    });
  }

  /** A node was clicked, in the drawing or in the list. */
  activate(id: string): void {
    const node = this.topology()?.nodes.find((n) => n.id === id);
    if (!node) return;
    this.notice.set(null);
    if (node.kind === 'queue') this.openQueue(node);
    else if (node.kind === 'service' || node.kind === 'gateway') this.openService(node);
  }

  /** A service points a Logs widget on this page at its Compose service, or opens the Logs page. */
  private openService(node: TopologyNode): void {
    if (node.compose === null) {
      this.notice.set(`${node.name} has no Compose service to follow.`);
      return;
    }
    if (this.point('logs', [node.compose])) this.notice.set(`The Logs widget on this page now shows ${node.compose}.`);
    else void this.router.navigateByUrl('/logs');
  }

  /** A parked queue opens in the dead-letter workbench; any other points a Queues widget at it, or opens the Broker page. */
  private openQueue(node: TopologyNode): void {
    if (node.parkedQueue !== null && this.broker.workbenches() > 0) {
      this.broker.requestOpen(node.parkedQueue);
      this.notice.set(`The dead-letter workbench on this page opened ${node.parkedQueue}.`);
    } else if (node.parkedQueue === null && this.point('queues', node.name)) {
      this.notice.set(`The Queues widget on this page shows ${node.name}.`);
    } else {
      void this.router.navigateByUrl('/broker');
    }
  }

  /**
   * Points the first widget of `type` on the page being shown at `value`, which that widget keeps as session state and
   * shows over its configured view until the operator takes it off (§4.2): the saved page is not touched. False when the
   * page has no such widget.
   */
  private point(type: string, value: string | string[]): boolean {
    const page = this.workspace.currentPage();
    const target = page ? widgetsOf(page).find((w) => w.type === type) : undefined;
    if (!target) return false;
    this.sessions.state<string | string[] | null>(target.id, 'pointed', null).set(value);
    return true;
  }

  /** A node was dropped: kept in this widget's config, so the arrangement outlives the tab. */
  private moved(id: string, at: Position): void {
    if (this.context) {
      this.workspace.configureWidget(this.context.pageId, this.context.instanceId, { ...this.config(), positions: { ...this.positions(), [id]: at } });
    }
  }

  /** Forgets where the operator put things and lays everything out again. */
  relayout(): void {
    if (this.context) this.workspace.configureWidget(this.context.pageId, this.context.instanceId, { ...this.config(), positions: {} });
    this.view?.relayout();
  }
}
