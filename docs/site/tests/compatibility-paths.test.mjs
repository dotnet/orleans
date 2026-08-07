import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  compatibilityOutputPath,
  deploymentBase,
} from '../scripts/lib/compatibility-paths.mjs';

const outputRoot = path.resolve('dist');

describe('compatibility output paths', () => {
  test('resolves file and directory routes beneath the output root', () => {
    expect(compatibilityOutputPath(`${deploymentBase}/docs/guide.html`, outputRoot)).toBe(
      path.join(outputRoot, 'docs', 'guide.html'),
    );
    expect(compatibilityOutputPath(`${deploymentBase}/docs/guide/`, outputRoot)).toBe(
      path.join(outputRoot, 'docs', 'guide', 'index.html'),
    );
    expect(compatibilityOutputPath(`${deploymentBase}/`, outputRoot)).toBe(
      path.join(outputRoot, 'index.html'),
    );
  });

  test.each([
    [`${deploymentBase}/..`, 'exact parent'],
    [`${deploymentBase}/%2e%2e`, 'encoded exact parent'],
    [`${deploymentBase}/%2e%2e/escape.html`, 'encoded parent segment'],
    [`${deploymentBase}/docs/%2e/guide.html`, 'encoded current segment'],
    [`${deploymentBase}/docs%2f..%2fescape.html`, 'encoded separators and parent segment'],
    [`${deploymentBase}/docs%5c..%5cescape.html`, 'encoded Windows separators'],
    [`${deploymentBase}/C:%5cWindows%5cwin.ini`, 'Windows drive path'],
    [`${deploymentBase}/docs/C:%2e%2e/escape.html`, 'Windows drive-relative traversal'],
    [`${deploymentBase}/docs/C:escape.html`, 'Windows drive-relative path'],
    [`${deploymentBase}/docs/guide%3astream.html`, 'Windows alternate data stream'],
    [`${deploymentBase}/%5c%5cserver%5cshare%5cfile`, 'Windows UNC path'],
    [`${deploymentBase}/%2fetc%2fpasswd`, 'POSIX absolute path'],
    [`${deploymentBase}/docs//guide.html`, 'empty path segment'],
    [`${deploymentBase}/docs/%00guide.html`, 'null character'],
    [`${deploymentBase}/docs/%E0%A4%A`, 'invalid URL encoding'],
    ['/different-base/docs/guide.html', 'different deployment base'],
  ])('rejects %s (%s)', (route) => {
    expect(() => compatibilityOutputPath(route, outputRoot)).toThrow(/Compatibility path/);
  });
});
