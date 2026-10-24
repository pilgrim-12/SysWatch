import { describe, expect, it } from 'vitest';
import { shortGpu } from '../src/components/Scopes';

describe('shortGpu', () => {
  it.each([
    ['NVIDIA GeForce RTX 4060 Laptop GPU', 'RTX 4060'],
    ['Intel(R) UHD Graphics 620', 'Intel UHD 620'],
    ['Intel(R) Iris(R) Xe Graphics', 'Intel Iris Xe'],
    ['AMD Radeon RX 7600', 'Radeon RX 7600'],
    ['AMD Radeon(TM) Graphics', 'Radeon'],
    ['NVIDIA RTX A2000 8GB Laptop GPU', 'RTX A2000 8GB'],
  ])('%s → %s', (a, b) => expect(shortGpu(a)).toBe(b));
});
