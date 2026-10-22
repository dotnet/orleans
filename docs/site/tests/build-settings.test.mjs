import assert from 'node:assert/strict';
import { describe, test } from 'vitest';
import { getBuildConcurrency } from '../scripts/lib/build-settings.mjs';

describe('documentation build settings', () => {
  test('serializes generated output on Windows by default', () => {
    const configured = process.env.ORLEANS_DOCS_BUILD_CONCURRENCY;
    delete process.env.ORLEANS_DOCS_BUILD_CONCURRENCY;
    try {
      assert.equal(getBuildConcurrency('win32'), 1);
    } finally {
      if (configured === undefined) {
        delete process.env.ORLEANS_DOCS_BUILD_CONCURRENCY;
      } else {
        process.env.ORLEANS_DOCS_BUILD_CONCURRENCY = configured;
      }
    }
  });

  test('keeps concurrent generation on other platforms', () => {
    assert.equal(getBuildConcurrency('linux', ''), 4);
  });

  test('honors an explicit concurrency override on Windows', () => {
    assert.equal(getBuildConcurrency('win32', '4'), 4);
  });

  test('honors an explicit concurrency override on other platforms', () => {
    assert.equal(getBuildConcurrency('linux', '1'), 1);
  });

  test.each(['0', '-2', '1.5', 'four'])('rejects invalid concurrency %s', (value) => {
    assert.throws(
      () => getBuildConcurrency('win32', value),
      /ORLEANS_DOCS_BUILD_CONCURRENCY must be a positive integer/,
    );
  });
});
