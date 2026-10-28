import { OutputLine } from '../../core/host/host-types';
import { GameDayLineReader } from './game-day-lines';

const AT = '2026-10-28T10:00:00.000Z';

function line(sequence: number, text: string): OutputLine {
  return { sequence, at: AT, stream: 'Stdout', text };
}

describe('GameDayLineReader', () => {
  it('reads the alert a cause is about to fire from the tool’s causing line', () => {
    const reader = new GameDayLineReader();

    expect(reader.add(line(1, 'error-rate: causing ErrorRateGateway; it fires within 480s once caused'))).toBe(true);

    expect(reader.snapshot()).toEqual([
      { kind: 'cause', alert: 'ErrorRateGateway', startedAt: Date.parse(AT), state: null, windowed: true },
    ]);
  });

  it('reads each phase’s state and whether the tool promised a window from the restore lines', () => {
    const reader = new GameDayLineReader();

    reader.add(line(1, 'ContactReadRefused is firing after the restore; its window clears it within 1980s'));
    reader.add(line(2, 'OutboxGrowth is inactive after the restore'));

    expect(reader.snapshot().map((w) => [w.kind, w.alert, w.state, w.windowed])).toEqual([
      ['restore', 'ContactReadRefused', 'firing', true],
      ['restore', 'OutboxGrowth', 'inactive', false],
    ]);
  });

  it('leaves every other line of the tool in the log', () => {
    const reader = new GameDayLineReader();
    const others = [
      'ErrorRateGateway: the cause is running in the background (pid 4242); --restore ends it',
      'game day: cause OK in 0m05s',
      'ErrorRateGateway: its state could not be read (connection refused)',
      'FINDING: ErrorRateGateway: already firing before the cause; restore the stack and rerun',
    ];

    for (const [i, text] of others.entries()) expect(reader.add(line(i, text))).toBe(false);

    expect(reader.snapshot()).toEqual([]);
    expect(others.some((text) => GameDayLineReader.claims(text))).toBe(false);
  });

  it('keeps a line read twice once, as a stream resumed may deliver it', () => {
    const reader = new GameDayLineReader();
    const causing = line(3, 'outbox-growth: causing OutboxGrowth; it fires within 780s once caused');

    reader.add(causing);
    reader.add(causing);

    expect(reader.snapshot()).toHaveLength(1);
  });
});
