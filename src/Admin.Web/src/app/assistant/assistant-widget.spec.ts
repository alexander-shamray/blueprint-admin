import { Component, input, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AssistantPanel, STATUS_REASON_TEXT } from './assistant-panel';
import { AssistantService } from './assistant-service';
import { AssistantWidget } from './assistant-widget';
import { AssistantStatus } from './protocol';

// A panel stand-in: its own spec covers the conversation, this one covers what the widget makes of the assistant.
@Component({ selector: 'app-assistant-panel', template: '<p class="panel">{{ embedded() }}</p>' })
class PanelStub {
  readonly embedded = input(false);
}

function stub(status: AssistantStatus | null) {
  const enabled = signal(status?.enabled === true);
  return {
    status: signal(status),
    enabled,
    show: vi.fn(),
  };
}

async function render(status: AssistantStatus | null) {
  const assistant = stub(status);
  TestBed.configureTestingModule({ providers: [{ provide: AssistantService, useValue: assistant }] });
  TestBed.overrideComponent(AssistantWidget, { remove: { imports: [AssistantPanel] }, add: { imports: [PanelStub] } });
  const fixture = TestBed.createComponent(AssistantWidget);
  await fixture.whenStable();
  return { fixture, assistant, element: fixture.nativeElement as HTMLElement };
}

describe('AssistantWidget', () => {
  it('shows the conversation, embedded, when the assistant is on', async () => {
    const { element, assistant } = await render({ enabled: true, provider: 'fake', model: 'scripted', reason: null });

    expect(element.querySelector('.panel')?.textContent).toBe('true');
    expect(element.querySelector('.off')).toBeNull();
    expect(assistant.show).toHaveBeenCalledTimes(1);
  });

  it('says the assistant is off, rather than showing a dead panel, when it is switched off', async () => {
    const { element } = await render({ enabled: false, provider: null, model: null, reason: 'off' });

    expect(element.querySelector('.panel')).toBeNull();
    expect(element.querySelector('.off')?.textContent).toBe(STATUS_REASON_TEXT.off);
  });

  it('shows nothing while the status has not been read, so a notice does not flash', async () => {
    const { element } = await render(null);

    expect(element.querySelector('.panel')).toBeNull();
    expect(element.querySelector('.off')).toBeNull();
  });
});
