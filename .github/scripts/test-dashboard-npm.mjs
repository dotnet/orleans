import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const npmCli = process.env.npm_execpath;
assert.ok(npmCli, 'Run through npm run test:install so the test uses the selected npm CLI.');
const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'Dashboard', 'Orleans.Dashboard.App');
const installScript = join(appRoot, 'scripts', 'install.mjs');
const fetchArgs = [
  '--prefer-offline', '--no-audit', '--no-fund',
  '--fetch-retries=5', '--fetch-retry-factor=3',
  '--fetch-retry-mintimeout=1', '--fetch-retry-maxtimeout=2',
];

function run(args, cwd) {
  return new Promise((resolveResult, reject) => {
    const child = spawn(process.execPath, args, {
      cwd,
      env: { ...process.env, npm_config_cache: join(cwd, 'cache') },
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', (code, signal) => resolveResult({ code, signal, stdout, stderr }));
  });
}

async function fixture(t, resetCount, scripts = {}, status = 200, canonical = false) {
  const directory = await mkdtemp(join(tmpdir(), 'orleans-npm-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const name = canonical ? 'vite' : 'download-fixture';
  await writeFile(join(directory, 'package.json'), JSON.stringify({ name, version: '1.0.0' }));
  if (canonical) {
    await mkdir(join(directory, 'bin'));
    await writeFile(join(directory, 'bin', 'vite.js'), `
const fs = require('node:fs');
fs.mkdirSync('dist', { recursive: true });
for (const name of ['index.html', 'index.min.js', 'index.css']) fs.writeFileSync('dist/' + name, 'asset');
`);
  }
  const packed = await run([npmCli, 'pack', '--ignore-scripts', '--json'], directory);
  assert.equal(packed.code, 0, packed.stderr);
  const tarball = await readFile(join(directory, JSON.parse(packed.stdout)[0].filename));
  await rm(join(directory, 'cache'), { recursive: true, force: true });
  let requests = 0;
  const server = createServer((request, response) => {
    requests++;
    response.writeHead(status, { 'Content-Length': tarball.length });
    if (requests <= resetCount) {
      response.write(tarball.subarray(0, Math.floor(tarball.length / 2)));
      setTimeout(() => response.destroy(), 50);
    } else {
      response.end(tarball);
    }
  });
  await new Promise(resolveReady => server.listen(0, '127.0.0.1', resolveReady));
  t.after(() => new Promise(resolveClosed => server.close(resolveClosed)));
  const url = `http://127.0.0.1:${server.address().port}/download-fixture.tgz`;
  await writeFile(join(directory, '.npmrc'), 'registry=http://127.0.0.1:1/\n');
  await writeFile(join(directory, 'package.json'), JSON.stringify({
    name: 'install-fixture', version: '1.0.0', scripts, dependencies: { [name]: url },
  }));
  await writeFile(join(directory, 'package-lock.json'), JSON.stringify({
    name: 'install-fixture', version: '1.0.0', lockfileVersion: 3,
    packages: {
      '': {
        name: 'install-fixture', version: '1.0.0', hasInstallScript: Object.keys(scripts).length > 0,
        dependencies: { [name]: url },
      },
      ['node_modules/' + name]: {
        version: '1.0.0', resolved: url,
        integrity: `sha512-${createHash('sha512').update(tarball).digest('base64')}`,
      },
    },
  }));
  return { directory, requests: () => requests };
}

test('native fetch retries do not retry a reset after response headers', async t => {
  const f = await fixture(t, 1);
  const result = await run([npmCli, 'ci', '--json', ...fetchArgs], f.directory);
  assert.notEqual(result.code, 0);
  assert.equal(JSON.parse(result.stdout).error.code, 'ECONNRESET', result.stderr);
  assert.equal(f.requests(), 1);
});

test('installation recovers a response-body reset and executes lifecycle scripts', async t => {
  const f = await fixture(t, 1, {
    postinstall: 'node -e "require(\'fs\').appendFileSync(\'installed.txt\', \'complete\')"',
  });
  const result = await run([installScript, ...fetchArgs], f.directory);
  assert.equal(result.code, 0, result.stderr);
  assert.equal(f.requests(), 2);
  assert.equal(await readFile(join(f.directory, 'installed.txt'), 'utf8'), 'complete');
  assert.equal(JSON.parse(await readFile(
    join(f.directory, 'node_modules/download-fixture/package.json'), 'utf8')).version, '1.0.0');
  assert.match(result.stderr, /ECONNRESET/);
  assert.match(result.stderr, /Retrying npm ci/);
});

test('persistent connection resets exhaust a bounded install budget', async t => {
  const f = await fixture(t, Infinity);
  const result = await run([installScript, ...fetchArgs], f.directory);
  assert.notEqual(result.code, 0);
  assert.equal(f.requests(), 3);
  assert.match(result.stderr, /ECONNRESET/);
  assert.match(result.stderr, /npm debug log/);
  assert.doesNotMatch(result.stderr, /SIGXCPU/);
});

test('a lifecycle failure mentioning ECONNRESET and returning 152 is not retried', async t => {
  const f = await fixture(t, 0, {
    postinstall: 'node -e "require(\'fs\').appendFileSync(\'lifecycle.txt\', \'failed\'); console.error(\'ECONNRESET\'); process.exit(152)"',
  });
  const result = await run([installScript, ...fetchArgs], f.directory);
  assert.equal(result.code, 152);
  assert.equal(await readFile(join(f.directory, 'lifecycle.txt'), 'utf8'), 'failed');
  assert.match(result.stderr, /ECONNRESET/);
  assert.match(result.stderr, /command failed/);
  assert.doesNotMatch(result.stderr, /Retrying npm ci|SIGXCPU/);
});

test('a missing lockfile fails without a retry or a download', async t => {
  const f = await fixture(t, 0);
  await rm(join(f.directory, 'package-lock.json'));
  const result = await run([installScript, ...fetchArgs], f.directory);
  assert.notEqual(result.code, 0);
  assert.equal(f.requests(), 0);
  assert.match(result.stderr, /EUSAGE/);
  assert.doesNotMatch(result.stderr, /Retrying npm ci/);
});

test('an authentication failure preserves the configured source and fails immediately', async t => {
  const f = await fixture(t, 0, {}, 401);
  const result = await run([installScript, ...fetchArgs], f.directory);
  assert.notEqual(result.code, 0);
  assert.equal(f.requests(), 1);
  assert.match(result.stderr, /E401/);
  assert.doesNotMatch(result.stderr, /Retrying npm ci/);
});

test('an integrity failure is left to npm and fails without a clean-install retry', async t => {
  const f = await fixture(t, 0);
  const lockfile = JSON.parse(await readFile(join(f.directory, 'package-lock.json'), 'utf8'));
  lockfile.packages['node_modules/download-fixture'].integrity =
    `sha512-${createHash('sha512').update('incorrect integrity').digest('base64')}`;
  await writeFile(join(f.directory, 'package-lock.json'), JSON.stringify(lockfile));
  const result = await run([installScript, ...fetchArgs], f.directory);
  assert.notEqual(result.code, 0);
  assert.match(result.stderr, /EINTEGRITY/);
  assert.doesNotMatch(result.stderr, /Retrying npm ci/);
});

async function runCanonicalGeneration(f) {
  await mkdir(join(f.directory, 'build'));
  await copyFile(join(appRoot, 'build', 'generate-assets.mjs'), join(f.directory, 'build', 'generate-assets.mjs'));
  await copyFile(join(appRoot, 'build', 'VerifyDashboardFrontendAssets.cs'), join(f.directory, 'build', 'VerifyDashboardFrontendAssets.cs'));
  await copyFile(join(appRoot, 'build', 'Orleans.Dashboard.Frontend.targets'), join(f.directory, 'build', 'Orleans.Dashboard.Frontend.targets'));
  await mkdir(join(f.directory, 'scripts'));
  await copyFile(installScript, join(f.directory, 'scripts', 'install.mjs'));
  const package_ = JSON.parse(await readFile(join(f.directory, 'package.json'), 'utf8'));
  const pinned = JSON.parse(await readFile(join(appRoot, 'package.json'), 'utf8'));
  Object.assign(package_, { engines: pinned.engines, packageManager: pinned.packageManager });
  await writeFile(join(f.directory, 'package.json'), JSON.stringify(package_));
  await writeFile(join(f.directory, '.node-version'), pinned.engines.node);
  await mkdir(join(f.directory, 'dist'));
  await writeFile(join(f.directory, 'dist', 'index.min.js'), 'previous deployable output');
  await writeFile(join(f.directory, 'build', 'assets.inputs.sha256'), 'previous inputs');
  await writeFile(join(f.directory, 'build', 'assets.outputs.sha256'), 'previous outputs');
  return run([join(f.directory, 'build', 'generate-assets.mjs')], f.directory);
}

test('canonical generation publishes complete assets after reset recovery', async t => {
  const f = await fixture(t, 1, {}, 200, true);
  const result = await runCanonicalGeneration(f);
  assert.equal(result.code, 0, result.stderr);
  assert.equal(f.requests(), 2);
  assert.equal(await readFile(join(f.directory, 'dist', 'index.min.js'), 'utf8'), 'asset');
  const inputs = await readFile(join(f.directory, 'build', 'assets.inputs.sha256'), 'utf8');
  assert.match(inputs, /  scripts\/install\.mjs/);
  assert.match(inputs, /  build\/VerifyDashboardFrontendAssets\.cs/);
  assert.match(inputs, /  build\/Orleans\.Dashboard\.Frontend\.targets/);
  assert.match(inputs, /  build\/generate-assets\.mjs/);
  assert.doesNotMatch(inputs, /  build\/assets\.(?:inputs|outputs)\.sha256/);
  const outputs = await readFile(join(f.directory, 'build', 'assets.outputs.sha256'), 'utf8');
  for (const file of ['index.html', 'index.min.js', 'index.css', 'THIRD-PARTY-NOTICES.txt']) {
    assert.ok(outputs.includes('  ' + file), `Missing complete generated output: ${file}`);
  }
  assert.match(result.stderr, /ECONNRESET/);
});

test('canonical generation preserves prior deployable outputs and identities after install failure', async t => {
  const f = await fixture(t, 0, { postinstall: 'node -e "console.error(\'ECONNRESET\'); process.exit(152)"' }, 200, true);
  const result = await runCanonicalGeneration(f);
  assert.notEqual(result.code, 0);
  assert.equal(f.requests(), 1);
  assert.equal(await readFile(join(f.directory, 'dist', 'index.min.js'), 'utf8'), 'previous deployable output');
  assert.equal(await readFile(join(f.directory, 'build', 'assets.inputs.sha256'), 'utf8'), 'previous inputs');
  assert.equal(await readFile(join(f.directory, 'build', 'assets.outputs.sha256'), 'utf8'), 'previous outputs');
  assert.match(result.stderr, /command failed/);
  assert.doesNotMatch(result.stderr, /Retrying npm ci|SIGXCPU/);
});
