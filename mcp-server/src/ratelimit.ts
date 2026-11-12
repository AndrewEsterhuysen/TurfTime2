/** In-memory token bucket per key (uid). Per instance only — fine for Phase 1 / single Cloud Run instance. */
export class RateLimiter {
  private buckets = new Map<string, { tokens: number; at: number }>();
  constructor(private capacity = 60, private refillPerSec = 1, private now = () => Date.now()) {}
  take(key: string): boolean {
    const t = this.now();
    const b = this.buckets.get(key) ?? { tokens: this.capacity, at: t };
    b.tokens = Math.min(this.capacity, b.tokens + ((t - b.at) / 1000) * this.refillPerSec);
    b.at = t;
    if (b.tokens < 1) { this.buckets.set(key, b); return false; }
    b.tokens -= 1;
    this.buckets.set(key, b);
    return true;
  }
}

/** Daily count cap per key (uid). Per instance, UTC day — same scope as RateLimiter. */
export class DailyCap {
  private counts = new Map<string, { day: string; n: number }>();
  constructor(readonly limit = 20, private today: () => string = () => new Date().toISOString().slice(0, 10)) {}
  atLimit(key: string): boolean { return this.peek(key) >= this.limit; }
  peek(key: string): number {
    const day = this.today();
    const c = this.counts.get(key);
    return (!c || c.day !== day) ? 0 : c.n;
  }
  /** Record one successful create. Returns false (and does not increment) when already at the limit. */
  take(key: string): boolean {
    const day = this.today();
    const c = this.counts.get(key);
    if (!c || c.day !== day) { this.counts.set(key, { day, n: 1 }); return true; }
    if (c.n >= this.limit) return false;
    c.n += 1;
    return true;
  }
}
