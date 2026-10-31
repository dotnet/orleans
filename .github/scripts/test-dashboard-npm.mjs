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
const installScript = resolve(dirname(fileURLToPath(import.meta.url)),
  '..', '..', 'src', 'Dashboard', 'Orleans.Dashboard.App', 'scripts', 'install.mjs');
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

async function fixture(t, resetCount, scripts = {}, status = 200) {
  const directory = await mkdtemp(join(tmpdir(), 'orleans-npm-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  await writeFile(join(directory, 'package.json'), JSON.stringify({
    name: 'download-fixture', version: '1.0.0',
  }));
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
    name: 'install-fixture', version: '1.0.0', scripts,
    dependencies: { 'download-fixture': url },
  }));
  await writeFile(join(directory, 'package-lock.json'), JSON.stringify({
    name: 'install-fixture', version: '1.0.0', lockfileVersion: 3,
    packages: {
      '': {
        name: 'install-fixture', version: '1.0.0', hasInstallScript: Object.keys(scripts).length > 0,
        dependencies: { 'download-fixture': url },
      },
      'node_modules/download-fixture': {
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
  assert.equal(f.requests(), 1, 'Five request retries still leave the body reset unretried.');
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

async function runFrontendTarget(f) {
  await mkdir(join(f.directory, 'scripts'), { recursive: true });
  await copyFile(installScript, join(f.directory, 'scripts', 'install.mjs'));
  const packageJson = JSON.parse(await readFile(join(f.directory, 'package.json'), 'utf8'));
  packageJson.scripts = {
    ...packageJson.scripts,
    restore: 'node scripts/install.mjs',
    build: 'node build.mjs',
  };
  await writeFile(join(f.directory, 'package.json'), JSON.stringify(packageJson));
  await writeFile(join(f.directory, 'index.html'), '<html/>');
  await writeFile(join(f.directory, 'build.mjs'), `
import { mkdirSync, writeFileSync } from 'node:fs';
mkdirSync('dist', { recursive: true });
for (const file of ['index.html', 'index.min.js', 'index.css']) {
  writeFileSync('dist/' + file, 'asset');
}
writeFileSync('built.txt', 'built');
`);
  const targets = resolve(dirname(fileURLToPath(import.meta.url)),
    '..', '..', 'src', 'Dashboard', 'Orleans.Dashboard', 'Orleans.Dashboard.Frontend.targets');
  const xml = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;');
  const project = join(f.directory, 'frontend.proj');
  await writeFile(project, `<Project>
  <PropertyGroup>
    <BaseIntermediateOutputPath>obj/</BaseIntermediateOutputPath>
  </PropertyGroup>
  <Import Project="${xml(targets)}" />
  <PropertyGroup>
    <NpmAppRootDirectory>${xml(f.directory)}</NpmAppRootDirectory>
    <NpmBuildOutput>${xml(join(f.directory, 'dist'))}/</NpmBuildOutput>
    <NpmAppInstallMarker>${xml(join(f.directory, 'node_modules', '.orleans.install.marker'))}</NpmAppInstallMarker>
    <NpmAppBuildMarker>${xml(join(f.directory, 'frontend.build.marker'))}</NpmAppBuildMarker>
  </PropertyGroup>
</Project>`);
  return new Promise((resolveResult, reject) => {
    const child = spawn('dotnet', [
      'msbuild', project, '-nologo', '-target:BuildNpmApp', '-verbosity:minimal',
    ], {
      cwd: f.directory,
      env: { ...process.env, npm_config_cache: join(f.directory, 'cache') },
    });
    let output = '';
    child.stdout.on('data', chunk => { output += chunk; });
    child.stderr.on('data', chunk => { output += chunk; });
    child.on('error', reject);
    child.on('close', code => resolveResult({ code, output }));
  });
}

test('the MSBuild target compiles and stamps the frontend after reset recovery', async t => {
  const f = await fixture(t, 1);
  const result = await runFrontendTarget(f);
  assert.equal(result.code, 0, result.output);
  assert.equal(f.requests(), 2);
  assert.equal(await readFile(join(f.directory, 'built.txt'), 'utf8'), 'built');
  await readFile(join(f.directory, 'frontend.build.marker'));
  await readFile(join(f.directory, 'node_modules', '.orleans.install.marker'));
  assert.match(result.output, /ECONNRESET/);
  assert.doesNotMatch(result.output, /error MSB/);
  const reused = await runFrontendTarget(f);
  assert.equal(reused.code, 0, reused.output);
  assert.equal(f.requests(), 2);
  assert.doesNotMatch(reused.output, /Installing Orleans Dashboard frontend|Building Orleans Dashboard frontend/);
});

test('the MSBuild target fails without compiling or stamping a failed installation', async t => {
  const f = await fixture(t, 0, { postinstall: 'node -e "process.exit(152)"' });
  const result = await runFrontendTarget(f);
  assert.notEqual(result.code, 0);
  assert.match(result.output, /error MSB3073/);
  assert.doesNotMatch(result.output, /SIGXCPU|Retrying npm ci/);
  await assert.rejects(readFile(join(f.directory, 'built.txt')), { code: 'ENOENT' });
  await assert.rejects(readFile(join(f.directory, 'frontend.build.marker')), { code: 'ENOENT' });
  await assert.rejects(readFile(join(f.directory, 'node_modules', '.orleans.install.marker')), { code: 'ENOENT' });
});
