import { readFile } from 'node:fs/promises';
import { describe, expect, test } from 'vitest';

describe('legacy redirects', () => {
  test('routes the original onboarding tutorials to their modern successors', async () => {
    const redirects = JSON.parse(
      await readFile(new URL('../src/data/redirects.json', import.meta.url), 'utf8'),
    );

    expect(
      redirects['/orleans/Tutorials/Minimal-Orleans-Application.html'],
    ).toBe('/orleans/docs/tutorials-and-samples/hello-world/');
    expect(
      redirects['/orleans/Tutorials/Running-in-a-Stand-alone-Silo.html'],
    ).toBe('/orleans/docs/quickstarts/build-your-first-orleans-app/');
  });

  test('routes the Azure Web Apps legacy page to the restored App Service guide', async () => {
    const redirects = JSON.parse(
      await readFile(new URL('../src/data/redirects.json', import.meta.url), 'utf8'),
    );

    expect(
      redirects[
        '/orleans/docs/deployment/azure_web_apps_with_azure_cloud_services.html'
      ],
    ).toBe('/orleans/docs/deployment/deploy-to-azure-app-service/');
  });
});
