import { Component } from '@angular/core';
import { SagaStripWidget } from './widgets/saga-strip-widget';
import { ScenarioWidget } from './widgets/scenario-widget';

/**
 * The Scenario screen as the legacy screen widget renders it, in a page the operator customised before the Scenario
 * page was built from widgets (widget-workspace spec §5.3): the Scenario widget beside the Saga strip, as the screen
 * laid them out, so their logic has one owner until phase 6 retires this component. The run itself is
 * `ScenarioRunner`'s, so leaving this screen leaves it going.
 */
@Component({
  selector: 'app-scenario-page',
  imports: [ScenarioWidget, SagaStripWidget],
  templateUrl: './scenario-page.html',
  styleUrl: './scenario-page.css',
})
export class ScenarioPage {}
