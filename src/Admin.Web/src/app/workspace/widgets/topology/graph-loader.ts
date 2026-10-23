import { InjectionToken } from '@angular/core';
import type { Core, ElementDefinition, LayoutOptions, StylesheetJson } from 'cytoscape';
import { Position, Positions, Topology } from './topology-graph';

/** What the drawing tells the widget about the operator: a node clicked, a node dropped where they put it. */
export interface GraphEvents {
  tap(id: string): void;
  moved(id: string, at: Position): void;
}

/** The drawn graph, which the widget keeps in step with the topology it reads. */
export interface GraphView {
  /**
   * Shows `topology`. Nodes keep their place across calls, so a refresh recolours and relabels without moving anything;
   * a node new to the drawing is placed by the layout, and `positions` (the operator's) put the nodes that have one back.
   */
  show(topology: Topology, positions: Positions): void;
  /** Lays every node out again, ignoring where the operator put them. */
  relayout(): void;
  destroy(): void;
}

export type GraphFactory = (container: HTMLElement, events: GraphEvents) => Promise<GraphView>;

/** Draws a graph into an element; a token so a test can draw without Cytoscape, which needs a real canvas. */
export const GRAPH_FACTORY = new InjectionToken<GraphFactory>('GRAPH_FACTORY', {
  providedIn: 'root',
  factory: () => cytoscapeGraph,
});

type Cytoscape = typeof import('cytoscape');

let loaded: Promise<Cytoscape> | undefined;

/**
 * Cytoscape and its dagre layout, fetched on the first diagram (widget-workspace spec §8.1) and registered once. Both
 * ship their types as `export =` while their ES builds export a default, which the bundler hands over and the types
 * do not know of.
 */
function cytoscape(): Promise<Cytoscape> {
  loaded ??= Promise.all([import('cytoscape'), import('cytoscape-dagre')]).then(([core, dagre]) => {
    const create = (core as unknown as { default: Cytoscape }).default;
    create.use((dagre as unknown as { default: Parameters<Cytoscape['use']>[0] }).default);
    return create;
  });
  // A chunk that failed to load is tried again by the next diagram, not remembered as failed until a reload.
  loaded.catch(() => (loaded = undefined));
  return loaded;
}

/**
 * Fills by state, drawn for the white page the drawing sits on whatever the console's theme (as the Mermaid drawings
 * are). The words beside the drawing say the same, so no state is told by colour alone; a node of unknown state is also
 * dashed, and a parked queue heavier.
 */
const FILL: Readonly<Record<string, string>> = {
  up: '#c8e6c9',
  down: '#ffcdd2',
  starting: '#ffe0b2',
  unknown: '#eeeeee',
  empty: '#e3f2fd',
  holding: '#fff59d',
  growing: '#ffcc80',
  parked: '#ef9a9a',
  neutral: '#f3e5f5',
};

const STYLE: StylesheetJson = [
  {
    selector: 'node',
    style: {
      label: 'data(label)',
      'background-color': 'data(fill)',
      'border-color': '#37474f',
      'border-width': 1.5,
      color: '#111',
      'font-size': 11,
      'text-valign': 'center',
      'text-halign': 'center',
      'text-wrap': 'wrap',
      'text-max-width': '180px',
      shape: 'round-rectangle',
      width: 'label',
      height: 'label',
      padding: '8px',
    },
  },
  { selector: 'node[kind = "gateway"]', style: { shape: 'round-hexagon' } },
  { selector: 'node[kind = "queue"]', style: { shape: 'barrel' } },
  { selector: 'node[kind = "exchange"]', style: { shape: 'round-tag', 'font-size': 10 } },
  { selector: 'node[state = "unknown"]', style: { 'border-style': 'dashed' } },
  { selector: 'node[state = "parked"]', style: { 'border-width': 4 } },
  {
    selector: 'edge',
    style: {
      'curve-style': 'bezier',
      'target-arrow-shape': 'triangle',
      width: 1.5,
      'line-color': '#78909c',
      'target-arrow-color': '#78909c',
      label: 'data(label)',
      color: '#263238',
      'font-size': 10,
      'text-background-color': '#fff',
      'text-background-opacity': 0.85,
      'text-background-padding': '2px',
      'text-rotation': 'autorotate',
    },
  },
  { selector: 'edge[kind = "route"]', style: { 'line-style': 'dashed' } },
];

/** Left to right: requests enter on the left, and a message travels from its exchange through its queue to its consumer. */
const LAYOUT = { name: 'dagre', rankDir: 'LR', nodeSep: 24, rankSep: 90, animate: false, padding: 16 } as const;

function elementOf(node: Topology['nodes'][number]): ElementDefinition {
  return { group: 'nodes', data: { id: node.id, kind: node.kind, label: node.label, state: node.state, fill: FILL[node.state] } };
}

/** The drawing on Cytoscape: a canvas, so a refresh changes data on elements already there and moves nothing. */
async function cytoscapeGraph(container: HTMLElement, events: GraphEvents): Promise<GraphView> {
  const create = await cytoscape();
  const cy: Core = create({
    container,
    style: STYLE,
    boxSelectionEnabled: false,
    // A graph of this size is read at one zoom; the wheel scrolls the page, the buttons and a pinch zoom it.
    userZoomingEnabled: true,
    wheelSensitivity: 0.3,
  });
  cy.on('tap', 'node', (e) => events.tap(e.target.id()));
  cy.on('dragfree', 'node', (e) => {
    const at = e.target.position();
    events.moved(e.target.id(), { x: Math.round(at.x), y: Math.round(at.y) });
  });
  const resize = new ResizeObserver(() => cy.resize());
  resize.observe(container);

  const layout = (fit: boolean): void => {
    // The dagre options are the extension's own, which `LayoutOptions` does not list.
    cy.layout({ ...LAYOUT, fit } as unknown as LayoutOptions).run();
  };

  return {
    show(topology, positions) {
      const wanted = new Set([...topology.nodes.map((n) => n.id), ...topology.edges.map((e) => e.id)]);
      let added = false;
      cy.batch(() => {
        cy.elements()
          .filter((el) => !wanted.has(el.id()))
          .remove();
        for (const node of topology.nodes) {
          const el = cy.getElementById(node.id);
          if (el.empty()) {
            cy.add(elementOf(node));
            added = true;
          } else {
            el.data(elementOf(node).data);
          }
        }
        for (const edge of topology.edges) {
          const el = cy.getElementById(edge.id);
          if (el.empty()) {
            cy.add({ group: 'edges', data: { id: edge.id, source: edge.source, target: edge.target, kind: edge.kind, label: edge.label } });
            added = true;
          } else {
            el.data('label', edge.label);
          }
        }
      });
      // The first layout fits the drawing to its element; a later one, for a queue that appeared, leaves the zoom alone.
      if (added) layout(!cy.scratch('laidOut'));
      cy.scratch('laidOut', true);
      cy.batch(() => {
        for (const [id, at] of Object.entries(positions)) {
          const el = cy.getElementById(id);
          // A node under the operator's hand is theirs until they let go: a refresh must not snap it back mid-drag.
          if (el.nonempty() && !el.grabbed() && (el.position('x') !== at.x || el.position('y') !== at.y)) el.position(at);
        }
      });
    },
    relayout() {
      layout(true);
    },
    destroy() {
      resize.disconnect();
      cy.destroy();
    },
  };
}
