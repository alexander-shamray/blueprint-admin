import { Component, inject, input, signal } from '@angular/core';
import { HostClient } from '../../../core/host/host-client';
import { hostErrorText } from '../../../core/host/host-error';
import { DoctorView } from '../../../core/host/host-types';

/**
 * The workstation doctor (spec §5.3): read once when the widget opens and again on request, not polled. Its reads are
 * the ones a developer makes before Up, and none of them moves while the widget is open.
 */
@Component({
  selector: 'app-workstation-doctor-widget',
  templateUrl: './workstation-doctor-widget.html',
  styleUrl: './stack-widgets.css',
  // Angular strips whitespace-only text nodes between adjacent <td>s by default, which runs the cells' text
  // together; the specs assert on whitespace-collapsed row text.
  preserveWhitespaces: true,
})
export class WorkstationDoctorWidget {
  private readonly host = inject(HostClient);

  readonly config = input<Record<string, unknown>>({});
  readonly doctor = signal<DoctorView | null>(null);
  readonly error = signal<string | null>(null);
  readonly reading = signal(false);

  constructor() {
    this.check();
  }

  check(): void {
    if (this.reading()) {
      return;
    }

    this.reading.set(true);
    this.host.doctor().subscribe({
      next: (view) => {
        this.doctor.set(view);
        this.error.set(null);
        this.reading.set(false);
      },
      error: (e: unknown) => {
        this.error.set(hostErrorText(e));
        this.reading.set(false);
      },
    });
  }
}
