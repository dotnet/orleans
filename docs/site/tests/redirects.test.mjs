import { readFile } from 'node:fs/promises';
import { describe, expect, test } from 'vitest';
import { mergeLegacyRedirects } from '../scripts/lib/legacy-routes.mjs';

async function readJson(relativePath) {
  return JSON.parse(await readFile(new URL(relativePath, import.meta.url), 'utf8'));
}

describe('legacy redirects', () => {
  test('routes the original onboarding tutorials to their modern successors', async () => {
    const redirects = await readJson('../src/data/redirects.json');

    expect(
      redirects['/orleans/Tutorials/Minimal-Orleans-Application.html'],
    ).toBe('/orleans/docs/tutorials-and-samples/hello-world/');
    expect(
      redirects['/orleans/Tutorials/Running-in-a-Stand-alone-Silo.html'],
    ).toBe('/orleans/docs/quickstarts/build-your-first-orleans-app/');
    expect(
      redirects[
        '/orleans/Documentation/Getting-Started-With-Orleans/Running-the-Application.html'
      ],
    ).toBe('/orleans/docs/quickstarts/build-your-first-orleans-app/');
  });

  test('routes the Azure Web Apps legacy page to the restored App Service guide', async () => {
    const redirects = await readJson('../src/data/redirects.json');

    expect(
      redirects[
        '/orleans/docs/deployment/azure_web_apps_with_azure_cloud_services.html'
      ],
    ).toBe('/orleans/docs/deployment/deploy-to-azure-app-service/');
  });

  test('routes the pre-DocFX Jekyll sitemap', async () => {
    const redirects = await readJson('../src/data/redirects.json');
    const legacyJekyllPages = await readJson('../src/data/legacy-jekyll-pages.json');
    const merged = mergeLegacyRedirects(redirects, legacyJekyllPages);

    expect(
      merged['/orleans/Orleans-Streams/Streams-Programming-APIs.html'],
    ).toBe('/orleans/docs/streaming/streams-programming-apis/');
    expect(
      merged['/orleans/Advanced-Concepts/Two-Way-Client-Observers-old.html'],
    ).toBe('/orleans/docs/grains/observers/');
    expect(
      merged['/orleans/Getting-Started-With-Orleans/Running-the-Application.html'],
    ).toBe('/orleans/docs/quickstarts/build-your-first-orleans-app/');
  });

  test('rejects conflicting legacy aliases', () => {
    expect(() =>
      mergeLegacyRedirects(
        { '/orleans/Introduction.html': '/orleans/docs/' },
        { '/orleans/Introduction.html': '/orleans/docs/overview/' },
      ),
    ).toThrow(/conflicting targets/);
  });
});
