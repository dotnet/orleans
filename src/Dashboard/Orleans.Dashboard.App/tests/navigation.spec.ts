import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.route('**/DashboardCounters*', route => route.fulfill({
    json: { hosts: [], simpleGrainStats: [], totalActivationCount: 0, totalActiveHostCount: 0, totalActiveHostCountHistory: [] },
  }));
  await page.route('**/version', route => route.fulfill({ json: { version: '10.0.0' } }));
});

test.afterEach(async ({ page }) => {
  expect(await page.pageErrors()).toEqual([]);
});

test('all silo counters refresh while the page stays open', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') });
  await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'));
  let reports = 0;
  await page.route('**/SiloCounters/*', route => route.fulfill({
    json: [{ name: 'orleans-messaging-received-messages-size', value: ++reports === 1 ? '7' : '12', delta: '5' }],
  }));
  await page.goto('/#/host/test/counters');
  await expect(page.getByRole('cell', { name: '7', exact: true })).toBeVisible();
  await page.clock.runFor(1_000);
  await expect(page.getByRole('cell', { name: '12', exact: true })).toBeVisible();
});

test('silo summary refreshes exported metrics instead of runtime statistics', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') });
  await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'));
  await page.route('**/HistoricalStats/*', route => route.fulfill({
    json: [{ activationCount: 1, clientCount: 999, receivedMessages: 999, sentMessages: 999 }],
  }));
  await page.route('**/SiloStats/*', route => route.fulfill({ json: {} }));
  await page.route('**/SiloProperties/*', route => route.fulfill({ json: {} }));
  await page.route('**/SiloMetadata/*', route => route.fulfill({ json: {} }));
  let received = 123;
  await page.route('**/SiloCounters/*', route => route.fulfill({
    json: [
      { name: 'orleans-gateway-connected-clients', value: '2', delta: '0' },
      { name: 'orleans-messaging-received-messages-size', value: String(received), delta: '0' },
      { name: 'orleans-messaging-sent-messages-size', value: '456', delta: '0' },
    ],
  }));
  await page.goto('/#/host/test');
  await expect(page.getByRole('row', { name: 'Clients 2', exact: true })).toBeVisible();
  await expect(page.getByRole('row', { name: 'Bytes received 123', exact: true })).toBeVisible();
  await expect(page.getByRole('row', { name: 'Bytes sent 456', exact: true })).toBeVisible();
  await expect(page.getByRole('row', { name: /(?:Receive|Request|Send) queue/ })).toHaveCount(0);

  received = 789;
  await page.clock.runFor(1_000);
  await expect(page.getByRole('row', { name: 'Bytes received 789', exact: true })).toBeVisible();
});

for (const width of [375, 991]) {
  test(`navigation remains accessible at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 800 });
    await page.goto('/#/preferences');
    const toggle = page.getByRole('button', { name: 'Toggle navigation' });
    const silos = page.getByRole('link', { name: 'Silos', exact: true });
    await expect(toggle).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(silos).not.toBeInViewport();

    await toggle.focus();
    await page.keyboard.press('Enter');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await expect(silos).toBeInViewport();
    await silos.click();
    await expect(page).toHaveURL(/#\/silos$/);
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');

    await toggle.click();
    await page.keyboard.press('Escape');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle).toBeFocused();

    await toggle.click();
    await page.getByRole('button', { name: 'Close navigation' }).click({ position: { x: width - 10, y: 100 } });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });
}

test('desktop navigation stays expanded across viewport changes', async ({ page }) => {
  await page.setViewportSize({ width: 992, height: 800 });
  await page.goto('/#/preferences');
  const toggle = page.getByRole('button', { name: 'Toggle navigation' });
  const silos = page.getByRole('link', { name: 'Silos', exact: true });
  await expect(toggle).toBeHidden();
  await expect(silos).toBeInViewport();
  await silos.click();
  await expect(page).toHaveURL(/#\/silos$/);

  await page.setViewportSize({ width: 375, height: 800 });
  await expect(toggle).toBeVisible();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await toggle.click();
  await page.setViewportSize({ width: 1200, height: 800 });
  await expect(toggle).toBeHidden();
  await expect(silos).toBeInViewport();
  await page.setViewportSize({ width: 375, height: 800 });
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(silos).not.toBeInViewport();
});
