import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, test } from 'vitest';

const repositoryRoot = path.resolve('../..');
const docsRoot = path.join(repositoryRoot, 'docs');
const snippetRoot = path.resolve('src/content/docs');
const defaultProperties = readFileSync(
  path.join(snippetRoot, 'Directory.Build.props'),
  'utf8',
);

function filesUnder(directory, extension) {
  return readdirSync(directory, { recursive: true })
    .filter(
      (file) =>
        file.endsWith(extension) &&
        !file.split(/[\\/]/).some((segment) => ['bin', 'obj'].includes(segment)),
    )
    .map((file) => path.join(directory, file));
}

function attributes(tag) {
  return new Map(
    [...tag.matchAll(/([A-Za-z]+)="([^"]+)"/g)].map((match) => [
      match[1],
      match[2],
    ]),
  );
}

describe('documentation project policy', () => {
  test('targets net10.0 without maintained v3 projects', () => {
    expect(defaultProperties).toContain('<TargetFramework>net10.0</TargetFramework>');

    const failures = [];
    for (const project of filesUnder(docsRoot, '.csproj')) {
      const relative = path.relative(repositoryRoot, project).replaceAll('\\', '/');
      if (relative.split('/').includes('snippets-v3')) {
        failures.push(`${relative}: inactive snippets-v3 project`);
        continue;
      }

      const source = readFileSync(project, 'utf8');
      const target = /<TargetFramework>([^<]+)<\/TargetFramework>/.exec(source)?.[1];
      if (target !== undefined && target !== 'net10.0') {
        failures.push(`${relative}: TargetFramework=${target}`);
      }
      if (/<TargetFrameworks>/.test(source)) {
        failures.push(`${relative}: multi-targeting is not supported`);
      }
    }

    expect(failures).toEqual([]);
  });

  test('uses Orleans 10.2.2 unless a migration project documents an exception', () => {
    const failures = [];
    for (const file of [
      ...filesUnder(docsRoot, '.csproj'),
      ...filesUnder(docsRoot, '.props'),
      ...filesUnder(docsRoot, '.targets'),
    ]) {
      const relative = path.relative(repositoryRoot, file).replaceAll('\\', '/');
      const source = readFileSync(file, 'utf8');
      const properties = new Map(
        [...source.matchAll(/<([A-Za-z]+Version)>([^<]+)<\/\1>/g)].map((match) => [
          match[1],
          match[2],
        ]),
      );

      for (const match of source.matchAll(/<PackageReference\b[^>]+>/g)) {
        const item = attributes(match[0]);
        const packageName = item.get('Include') ?? item.get('Update');
        if (!packageName?.startsWith('Microsoft.Orleans.')) {
          continue;
        }

        const version = item.get('Version') ?? item.get('VersionOverride');
        const propertyName = /^\$\(([^)]+)\)$/.exec(version ?? '')?.[1];
        const resolvedVersion = propertyName ? properties.get(propertyName) : version;
        if (resolvedVersion !== '10.2.2') {
          failures.push(`${relative}: ${packageName}=${version ?? '<missing>'}`);
        }
      }
    }

    expect(failures).toEqual([]);
  });
});
