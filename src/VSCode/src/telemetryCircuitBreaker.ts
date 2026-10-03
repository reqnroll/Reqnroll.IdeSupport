/**
 * Best-effort delivery policy for telemetry (issue #859): after the first definitive failure to
 * reach the analytics endpoint the transmitter stops sending for the rest of the session and
 * tells the user exactly once. Same behaviour and wording as VS's `TelemetryCircuitBreaker.cs`
 * and Rider's `TelemetryCircuitBreaker.kt`.
 */
export const TELEMETRY_UNAVAILABLE_NOTICE =
  'Telemetry endpoint unreachable; telemetry for this session will be dropped';

export class TelemetryCircuitBreaker {
  private open = false;

  /**
   * @param onFirstFailure receives the one-time notice (Reqnroll output channel + log file).
   * @param onSuppressedFailure receives every failure after the first; verbose logging only.
   */
  constructor(
    private readonly onFirstFailure: (notice: string) => void,
    private readonly onSuppressedFailure: (message: string) => void = () => undefined,
  ) {}

  /** True once a failure has been recorded; callers must then skip all sending. */
  get isOpen(): boolean {
    return this.open;
  }

  recordFailure(reason: string): void {
    if (!this.open) {
      this.open = true;
      this.onFirstFailure(TELEMETRY_UNAVAILABLE_NOTICE);
      this.onSuppressedFailure(`Telemetry failure: ${reason}`);
    } else {
      this.onSuppressedFailure(`Telemetry failure (suppressed): ${reason}`);
    }
  }
}
