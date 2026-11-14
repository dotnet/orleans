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

for (const theme of ['light', 'dark']) {
  test(`production lifecycle diagrams use the single bundle in the ${theme} theme`, async ({ page }) => {
    await page.addInitScript(value => localStorage.setItem('theme', value), theme);
    await page.route('**/LifecycleStages', route => route.fulfill({
      json: [
        {
          stage: 1000, stageName: 'Initialize', isNamedStage: true,
          observers: [{ name: 'Storage', hasOnStart: true, hasOnStop: true,
            onStartMethod: 'StartStorage', onStopMethod: 'StopStorage' }],
        },
        {
          stage: 2000, stageName: 'Active', isNamedStage: true,
          observers: [{ name: 'Gateway', hasOnStart: true, hasOnStop: true,
            onStartMethod: 'OpenGateway', onStopMethod: 'CloseGateway' }],
        },
      ],
    }));
    const scripts: string[] = [];
    page.on('request', request => {
      if (request.resourceType() === 'script') scripts.push(new URL(request.url()).pathname);
    });
    await page.goto('/#/lifecycle');
    const nodes = page.getByRole('list', { name: 'Startup stages' }).getByRole('listitem');
    await expect(nodes).toHaveCount(2);
    await expect(nodes.nth(0)).toContainText('Initialize (start)');
    await expect(nodes.nth(0)).toContainText('StartStorage');
    await expect(nodes.nth(1)).toContainText('OpenGateway');
    expect(await nodes.nth(0).evaluate(node => getComputedStyle(node, '::after').content)).toBe('"\u2193"');
    await expect(page.getByRole('row', { name: 'Initialize Storage StartStorage' })).toBeVisible();
    await expect(page.getByRole('row', { name: 'Active Gateway OpenGateway' })).toBeVisible();
    const startupBackground = await nodes.nth(0).evaluate(node => getComputedStyle(node).backgroundColor);

    await page.getByRole('tab', { name: 'Shutdown', exact: true }).click();
    const shutdown = page.getByRole('list', { name: 'Shutdown stages' }).getByRole('listitem');
    await expect(shutdown).toHaveCount(2);
    await expect(shutdown.nth(0)).toContainText('Active (stop)');
    await expect(shutdown.nth(0)).toContainText('CloseGateway');
    await expect(shutdown.nth(1)).toContainText('StopStorage');
    await expect(page.getByRole('row', { name: 'Active Gateway CloseGateway' })).toBeVisible();
    expect(await shutdown.nth(0).evaluate(node => getComputedStyle(node).backgroundColor)).not.toBe(startupBackground);
    await expect(page.locator('body')).toHaveAttribute('data-bs-theme', theme);
    expect(scripts).toEqual(['/index.min.js']);
  });
}

test('lifecycle shows loading followed by the empty cluster state', async ({ page }) => {
  let release!: () => void;
  const pending = new Promise<void>(resolve => release = resolve);
  await page.route('**/LifecycleStages', async route => {
    await pending;
    await route.fulfill({ json: [] });
  });
  await page.goto('/#/lifecycle');
  await expect(page.getByText('Loading lifecycle data…')).toBeVisible();
  release();
  await expect(page.getByText('No active silo is available to report its lifecycle.')).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Startup', exact: true })).toHaveCount(0);
});

test('lifecycle renders literal labels, empty stages, and unknown methods', async ({ page }) => {
  const name = 'Observer "<img src=x onerror=alert(1)>" & [label]\nsecond line';
  await page.route('**/LifecycleStages', route => route.fulfill({
    json: [
      { stage: 1000, stageName: 'Initialize "<tag>" & [stage]', isNamedStage: true, observers: [] },
      { stage: 1500, stageName: '1500', isNamedStage: false,
        observers: [{ name, hasOnStart: true, hasOnStop: false }] },
    ],
  }));
  await page.goto('/#/lifecycle');
  const diagram = page.getByRole('region', { name: 'Startup stages' });
  const nodes = diagram.getByRole('listitem');
  await expect(nodes.nth(0)).toContainText('Initialize "<tag>" & [stage] (start)');
  await expect(nodes.nth(0)).toContainText('(no participants)');
  expect(await nodes.nth(1).textContent()).toBe(`1500 (start)- ${name}`);
  await expect(diagram.locator('img, tag')).toHaveCount(0);
  await expect(page.getByRole('cell', { name: '(unknown)', exact: true })).toBeVisible();
  expect(await nodes.nth(0).evaluate(node => getComputedStyle(node).backgroundColor))
    .not.toBe(await nodes.nth(1).evaluate(node => getComputedStyle(node).backgroundColor));

  await page.getByRole('tab', { name: 'Shutdown', exact: true }).click();
  await expect(page.getByRole('cell', { name: '—', exact: true })).toHaveCount(2);
  await expect(page.getByRole('region', { name: 'Shutdown stages' }).getByRole('listitem').nth(0))
    .toContainText('1500 (stop)');
});

test('large lifecycle diagrams scroll to their complete labels and last stage', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 800 });
  const stages = Array.from({ length: 12 }, (_, index) => ({
    stage: index,
    stageName: `Stage ${index} with a deliberately long label to verify horizontal scrolling`,
    isNamedStage: index === 0,
    observers: [{ name: 'Storage', hasOnStart: true, hasOnStop: true,
      onStartMethod: 'StartStorage', onStopMethod: 'StopStorage' }],
  }));
  await page.route('**/LifecycleStages', route => route.fulfill({ json: stages }));
  await page.goto('/#/lifecycle');
  const diagram = page.getByRole('region', { name: 'Startup stages' });
  expect(await diagram.evaluate(node => node.scrollWidth > node.clientWidth && node.scrollHeight > node.clientHeight))
    .toBe(true);
  await diagram.focus();
  await page.keyboard.press('End');
  await expect.poll(() => diagram.evaluate(node => node.scrollTop)).toBeGreaterThan(0);
  await diagram.evaluate(node => {
    node.scrollTop = node.scrollHeight;
    node.scrollLeft = node.scrollWidth;
  });
  const last = diagram.getByRole('listitem').last();
  await expect(last).toContainText(stages[11].stageName);
  await expect(last).toBeInViewport();
  expect(await last.evaluate(node => getComputedStyle(node, '::after').content)).toBe('none');
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

test('silo summary uses gateway clients and refreshes collected message bytes', async ({ page }) => {
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
  await expect(page.getByRole('row', { name: 'Clients 999', exact: true })).toBeVisible();
  await expect(page.getByRole('row', { name: 'Bytes received 123 B', exact: true })).toBeVisible();
  await expect(page.getByRole('row', { name: 'Bytes sent 456 B', exact: true })).toBeVisible();
  await expect(page.getByRole('row', { name: /(?:Receive|Request|Send) queue/ })).toHaveCount(0);

  received = 2048;
  await page.clock.runFor(1_000);
  await expect(page.getByRole('row', { name: 'Bytes received 2.0 KB', exact: true })).toBeVisible();
});

test('counter refresh keeps one request in flight', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') });
  await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'));
  let release!: () => void;
  let started!: () => void;
  const pending = new Promise<void>(resolve => release = resolve);
  const firstRequest = new Promise<void>(resolve => started = resolve);
  let requests = 0;
  await page.route('**/SiloCounters/*', async route => {
    if (++requests === 1) {
      started();
      await pending;
    }
    await route.fulfill({
      json: [{ name: 'messages', value: requests === 1 ? '7' : '12', delta: '5' }],
    });
  });
  await page.goto('/#/host/test/counters');
  await firstRequest;
  await page.clock.runFor(5_000);
  expect(requests).toBe(1);
  release();
  await expect(page.getByRole('cell', { name: '7', exact: true })).toBeVisible();
  await page.clock.runFor(1_000);
  await expect(page.getByRole('cell', { name: '12', exact: true })).toBeVisible();
});

for (const path of ['/#/host/test', '/#/host/test/counters']) {
  test(`failed counter requests keep refreshing without page errors on ${path}`, async ({ page }) => {
    await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') });
    await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'));
    await page.route('**/HistoricalStats/*', route => route.fulfill({ json: [{ activationCount: 1, clientCount: 1 }] }));
    await page.route('**/SiloStats/*', route => route.fulfill({ json: {} }));
    await page.route('**/SiloProperties/*', route => route.fulfill({ json: {} }));
    await page.route('**/SiloMetadata/*', route => route.fulfill({ json: {} }));
    let requests = 0;
    await page.route('**/SiloCounters/*', route => {
      requests++;
      return route.fulfill({ status: 503 });
    });
    await page.goto(path);
    await expect.poll(() => requests).toBe(1);
    await page.clock.runFor(1_000);
    await expect.poll(() => requests).toBe(2);
  });
}

for (const { path, endpoint } of [
  { path: '/#/host/test', endpoint: '**/HistoricalStats/*' },
  { path: '/#/host/test', endpoint: '**/SiloStats/*' },
  { path: '/#/host/test', endpoint: '**/SiloCounters/*' },
  { path: '/#/grain/TestGrain', endpoint: '**/GrainStats/*' },
]) {
  test(`refresh keeps one ${endpoint} request in flight on ${path}`, async ({ page }) => {
    await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') });
    await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'));
    await page.route('**/HistoricalStats/*', route => route.fulfill({ json: [{ activationCount: 1, clientCount: 1 }] }));
    await page.route('**/SiloStats/*', route => route.fulfill({ json: {} }));
    await page.route('**/SiloProperties/*', route => route.fulfill({ json: {} }));
    await page.route('**/SiloMetadata/*', route => route.fulfill({ json: {} }));
    await page.route('**/SiloCounters/*', route => route.fulfill({ json: [] }));
    await page.route('**/GrainStats/*', route => route.fulfill({ json: {} }));
    let release!: () => void;
    let started!: () => void;
    const pending = new Promise<void>(resolve => release = resolve);
    const firstRequest = new Promise<void>(resolve => started = resolve);
    let requests = 0;
    await page.route(endpoint, async route => {
      if (++requests === 1) {
        started();
        await pending;
      }
      await route.fallback();
    });
    await page.goto(path);
    await firstRequest;
    await page.clock.runFor(5_000);
    expect(requests).toBe(1);
    const response = page.waitForResponse(endpoint);
    release();
    await (await response).finished();
    await page.clock.runFor(1_000);
    await expect.poll(() => requests).toBe(2);
  });
}

test('late counter responses preserve the current route', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') });
  await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'));
  let release!: () => void;
  let started!: () => void;
  const pending = new Promise<void>(resolve => release = resolve);
  const firstRequest = new Promise<void>(resolve => started = resolve);
  await page.route('**/SiloCounters/*', async route => {
    started();
    await pending;
    await route.fulfill({ json: [{ name: 'messages', value: '7', delta: '0' }] });
  });
  await page.goto('/#/host/test/counters');
  await firstRequest;
  await page.evaluate(() => window.location.hash = '#/preferences');
  await expect(page.getByRole('heading', { name: 'Preferences', exact: true, level: 1 })).toBeVisible();
  const response = page.waitForResponse('**/SiloCounters/*');
  release();
  await (await response).finished();
  await page.clock.runFor(1);
  await expect(page.getByRole('heading', { name: 'Preferences', exact: true, level: 1 })).toBeVisible();
  await expect(page.getByRole('cell', { name: '7', exact: true })).toHaveCount(0);
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
