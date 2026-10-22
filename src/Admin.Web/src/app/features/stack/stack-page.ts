import { Component } from '@angular/core';
import { JobOutputWidget } from '../../workspace/job-output-widget';
import { AlertsWidget } from './widgets/alerts-widget';
import { ComposeServicesWidget } from './widgets/compose-services-widget';
import { GoldenSignalsWidget } from './widgets/golden-signals-widget';
import { LoadRunWidget } from './widgets/load-run-widget';
import { ReachabilityWidget } from './widgets/reachability-widget';
import { ReferenceClientWidget } from './widgets/reference-client-widget';
import { StackControlsWidget } from './widgets/stack-controls-widget';
import { WorkstationDoctorWidget } from './widgets/workstation-doctor-widget';

/**
 * The Stack screen as the legacy screen widget renders it, in a page the operator customised before the Stack page
 * was built from widgets (widget-workspace spec §5.3): the same widgets, one under another in the screen's old order,
 * so their logic has one owner until phase 6 retires this component.
 */
@Component({
  selector: 'app-stack-page',
  imports: [
    AlertsWidget,
    ComposeServicesWidget,
    GoldenSignalsWidget,
    JobOutputWidget,
    LoadRunWidget,
    ReachabilityWidget,
    ReferenceClientWidget,
    StackControlsWidget,
    WorkstationDoctorWidget,
  ],
  templateUrl: './stack-page.html',
  styleUrl: './stack-page.css',
})
export class StackPage {}
