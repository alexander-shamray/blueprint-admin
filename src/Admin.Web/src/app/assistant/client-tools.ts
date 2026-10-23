import { ToolDescriptor } from './protocol';

/** What a client tool answers: an error result is one the model reads, never an exception. */
export interface ToolOutcome {
  ok: boolean;
  content: string;
}

/** One tool the client runs: its descriptor, sent when a conversation opens, and what running it does. */
export interface ClientTool {
  descriptor: ToolDescriptor;
  run(input: Record<string, unknown>): ToolOutcome | Promise<ToolOutcome>;
}

/**
 * The client's tools (AI assistant design §6.4), as a registry the shared client runs calls through. The mechanism is
 * the shared part (§13); the tools a client registers are its own. Framework-free.
 */
export class ClientToolRegistry {
  private readonly tools = new Map<string, ClientTool>();

  register(tool: ClientTool): void {
    if (this.tools.has(tool.descriptor.name)) throw new Error(`${tool.descriptor.name} is registered twice.`);
    this.tools.set(tool.descriptor.name, tool);
  }

  descriptors(): ToolDescriptor[] {
    return [...this.tools.values()].map((t) => t.descriptor);
  }

  /** Runs a call; an unknown tool, or one that throws, is an error result rather than an exception. */
  async run(name: string, input: Record<string, unknown>): Promise<ToolOutcome> {
    const tool = this.tools.get(name);
    if (!tool) return { ok: false, content: `This client has no tool named ${name}.` };
    try {
      return await tool.run(input);
    } catch (e) {
      return { ok: false, content: e instanceof Error ? e.message : String(e) };
    }
  }
}

/** A client tool's descriptor: read-only in the platform's sense, since a client tool changes only the view (§6.3). */
export function viewTool(name: string, title: string, description: string, inputSchema: Record<string, unknown>): ToolDescriptor {
  return {
    name,
    title,
    description,
    inputSchema,
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    side: 'client',
  };
}
