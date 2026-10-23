import { TestBed } from '@angular/core/testing';
import { Subject, throwError } from 'rxjs';
import { HostClient } from '../../core/host/host-client';
import { QueuesView } from '../../core/host/host-types';
import { AUTO_REFRESH_MS, BrokerFeed, exchangeName, filterOf } from './broker-feed';
import { brokerHost, plan, queues } from './broker-testing';

describe('BrokerFeed', () => {
  let host: ReturnType<typeof brokerHost>;

  beforeEach(() => {
    host = brokerHost();
    TestBed.configureTestingModule({ providers: [{ provide: HostClient, useValue: host }] });
  });

  it('polls every 5 seconds', () => {
    expect(AUTO_REFRESH_MS).toBe(5000);
  });

  it('reads nothing until a widget asks, and reads each of the four apart', () => {
    const feed = TestBed.inject(BrokerFeed);

    expect(host.brokerQueues).not.toHaveBeenCalled();
    feed.exchanges.refresh();

    expect(host.brokerExchanges).toHaveBeenCalledTimes(1);
    expect(host.brokerQueues).not.toHaveBeenCalled();
    expect(host.brokerBindings).not.toHaveBeenCalled();
    expect(host.brokerPermissions).not.toHaveBeenCalled();
  });

  it('holds the two tools\' plans, and undefined for one the host does not answer', () => {
    host.deadLetterPlan.mockReturnValue(throwError(() => new Error('down')));
    const feed = TestBed.inject(BrokerFeed);

    expect(feed.projectionPlan()).toEqual(plan);
    expect(feed.deadLetterPlan()).toBeUndefined();
  });

  it('reads the queues at once for something that happened, and owes a read when one is out', () => {
    const feed = TestBed.inject(BrokerFeed);
    feed.readQueuesAgain();
    expect(host.brokerQueues).toHaveBeenCalledTimes(1);

    const inFlight = new Subject<QueuesView>();
    host.brokerQueues.mockReturnValueOnce(inFlight);
    feed.queues.refresh();
    feed.readQueuesAgain();
    expect(host.brokerQueues).toHaveBeenCalledTimes(2);

    inFlight.next(queues);
    inFlight.complete();
    TestBed.tick();

    expect(host.brokerQueues).toHaveBeenCalledTimes(3);
  });

  it('numbers an open request, so the same queue asked twice is two requests', () => {
    const feed = TestBed.inject(BrokerFeed);

    feed.requestOpen('a_error');
    feed.requestOpen('a_error');

    expect(feed.openRequest()).toEqual({ queue: 'a_error', n: 2 });
  });

  it('names the default exchange and reads a config filter case-insensitively', () => {
    expect(exchangeName('')).toBe('(default)');
    expect(exchangeName('amq.topic')).toBe('amq.topic');
    expect(filterOf({ filter: '  Bff ' })).toBe('bff');
    expect(filterOf({ filter: 7 })).toBe('');
    expect(filterOf({})).toBe('');
  });
});
