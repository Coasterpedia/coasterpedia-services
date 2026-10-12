import { describe, expect, it } from 'vitest';
import { bucketWidth, histogram } from '../src/facets';

// The bucket rule is Module:AttractionBrowser's histogramWidth, so the browser
// draws the same bars from either source.
describe('histogram buckets', () => {
	it('keeps whole numbers in whole buckets', () => {
		// Inversions 0 to 14: fifteen bars of 1, never half-inversion buckets.
		expect(bucketWidth(0, 14, true)).toMatchObject({ w: 1 });
		const h = histogram('', [{ v: 0, n: 3 }, { v: 1, n: 1 }, { v: 14, n: 2 }])!;
		expect(h.buckets).toHaveLength(15);
		expect(h.buckets[0]).toEqual({ from: 0, to: 1, count: 3 });
	});

	it('reaches below 1 only when whole widths leave fewer than eight bars', () => {
		expect(bucketWidth(1.2, 3.8, false)).toMatchObject({ w: 0.1 });
		expect(bucketWidth(1, 3, true)).toMatchObject({ w: 1 });
		expect(bucketWidth(0, 1000, false)).toMatchObject({ w: 50 });
	});

	it('cuts at multiples of the width, and rounds the edges', () => {
		const h = histogram('m', [{ v: 0.3, n: 1 }, { v: 1.2, n: 2 }])!;
		// 0.3 to 1.2 at 0.05: 19 bars, the narrowest nice width under 32.
		expect(h.buckets[0]).toEqual({ from: 0.3, to: 0.35, count: 1 });
		expect(h.buckets.at(-1)).toEqual({ from: 1.2, to: 1.25, count: 2 });
		expect(h.buckets).toHaveLength(19);
	});

	it('has no buckets for a single value, and no range for none', () => {
		expect(histogram('m', [{ v: 5, n: 4 }])).toEqual({ unit: 'm', min: 5, max: 5, buckets: [] });
		expect(histogram('m', [])).toBeNull();
	});
});
