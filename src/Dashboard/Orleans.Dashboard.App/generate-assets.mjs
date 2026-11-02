import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const check = process.argv[2] === '--check';
if (process.argv.length > 2 && !(check && process.argv.length === 3)) {
  throw new Error('Usage: npm run assets:generate OR npm run assets:check');
}

const excluded = new Set(['README.md', '.gitignore', 'assets.inputs.sha256', 'assets.outputs.sha256']);
const textExtensions = new Set(['.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.mts', '.cts', '.cs', '.css', '.json', '.html', '.svg', '.txt']);
const ordinal = (left, right) => left < right ? -1 : left > right ? 1 : 0;
const relative = (directory, file) => path.relative(directory, file).split(path.sep).join('/');
const isText = file => textExtensions.has(path.extname(file).toLowerCase())
  || ['.npmrc', '.node-version'].includes(path.basename(file)) || path.basename(file).startsWith('.env');
const canonical = file => isText(file) ? Buffer.from(readFileSync(file, 'utf8').replace(/\r\n/g, '\n')) : readFileSync(file);

function files(directory, recursive = true) {
  if (!existsSync(directory)) return [];
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const file = path.join(directory, entry.name);
    if (entry.isFile()) return [file];
    if (entry.isDirectory()) return recursive ? files(file) : [];
    throw new Error(`Frontend inputs and outputs must be regular files: ${file}`);
  });
}

function inputs() {
  return [...files(root, false).filter(file => !excluded.has(path.basename(file))),
    ...files(path.join(root, 'src')), ...files(path.join(root, 'public')), ...files(path.join(root, 'scripts'))];
}

function manifest(directory, paths, normalizeText) {
  return paths.map(file => {
    const bytes = normalizeText ? canonical(file) : readFileSync(file);
    return `${createHash('sha256').update(bytes).digest('hex').toUpperCase()}  ${relative(directory, file)}`;
  }).sort(ordinal).join('\n') + '\n';
}

function run(arguments_, directory, environment) {
  const result = spawnSync(process.execPath, arguments_, { cwd: directory, env: environment, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Frontend generation failed (exit ${result.status}, signal ${result.signal}).`);
}

function notices(directory) {
  const lock = JSON.parse(readFileSync(path.join(directory, 'package-lock.json'), 'utf8'));
  const result = ['Third-party notices for Orleans Dashboard frontend production dependencies.',
    'Dependency versions and integrity hashes are recorded in package-lock.json.', ''];
  for (const [location, metadata] of Object.entries(lock.packages).sort(([left], [right]) => ordinal(left, right))) {
    if (!location || metadata.dev || !existsSync(path.join(directory, location, 'package.json'))) continue;
    const packageRoot = path.join(directory, location);
    const package_ = JSON.parse(readFileSync(path.join(packageRoot, 'package.json'), 'utf8'));
    result.push(`===== ${package_.name}@${package_.version} =====`, `License: ${package_.license ?? metadata.license ?? 'See included notices'}`);
    for (const file of files(packageRoot, false).filter(file => /^(licen[cs]e|copying|notice)([.-]|$)/i.test(path.basename(file))).sort(ordinal)) {
      result.push(`--- ${path.basename(file)} ---`, readFileSync(file, 'utf8').replace(/\r\n/g, '\n').trimEnd());
    }
    result.push('');
  }
  return result.join('\n');
}

const package_ = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8'));
if (process.versions.node !== package_.engines.node || readFileSync(path.join(root, '.node-version'), 'utf8').trim() !== package_.engines.node) {
  throw new Error(`Generation requires Node.js ${package_.engines.node}; found ${process.versions.node}.`);
}
const npmCli = process.env.npm_execpath;
if (!npmCli) throw new Error('Run this script using npm run assets:generate or npm run assets:check.');
const npmVersion = JSON.parse(readFileSync(path.resolve(path.dirname(npmCli), '..', 'package.json'), 'utf8')).version;
if (npmVersion !== package_.engines.npm || package_.packageManager !== `npm@${npmVersion}`) {
  throw new Error(`Generation requires npm ${package_.engines.npm}; found ${npmVersion}.`);
}

const sourceFiles = inputs();
if (sourceFiles.some(file => /^\.env(?:\..*)?\.local$/.test(path.basename(file)))) {
  throw new Error('Move local .env overrides out of the frontend root before canonical generation.');
}
const inputManifest = manifest(root, sourceFiles, true);
const temporary = mkdtempSync(path.join(tmpdir(), 'orleans-dashboard-'));
try {
  const app = path.join(temporary, 'app');
  for (const file of sourceFiles) {
    const destination = path.join(app, relative(root, file));
    mkdirSync(path.dirname(destination), { recursive: true });
    writeFileSync(destination, canonical(file));
  }

  // A fresh dependency tree and an explicit environment make the committed bytes independent of developer state.
  const allowed = new Set(['path', 'systemroot', 'windir', 'comspec', 'pathext', 'temp', 'tmp', 'tmpdir',
    'home', 'userprofile', 'appdata', 'localappdata', 'http_proxy', 'https_proxy', 'no_proxy', 'all_proxy',
    'ssl_cert_file', 'ssl_cert_dir', 'node_extra_ca_certs']);
  const environment = Object.fromEntries(Object.entries(process.env).filter(([key]) => allowed.has(key.toLowerCase())));
  Object.assign(environment, { NODE_ENV: 'production', TZ: 'UTC', LANG: 'C', LC_ALL: 'C', npm_execpath: npmCli });
  const userConfig = path.join(temporary, 'user.npmrc');
  const globalConfig = path.join(temporary, 'global.npmrc');
  writeFileSync(userConfig, '');
  writeFileSync(globalConfig, '');
  run([path.join(app, 'scripts', 'install.mjs'), '--include=dev', '--ignore-scripts=false', '--legacy-peer-deps=false',
    '--install-links=false', '--bin-links=true', '--install-strategy=hoisted',
    `--userconfig=${userConfig}`, `--globalconfig=${globalConfig}`, '--prefer-offline', '--no-audit', '--no-fund',
    '--fetch-retries=5', '--fetch-retry-factor=3', '--fetch-retry-mintimeout=5000', '--fetch-retry-maxtimeout=15000'], app, environment);
  run([path.join(app, 'node_modules', 'vite', 'bin', 'vite.js'), 'build', '--mode', 'production'], app, environment);
  const output = path.join(app, 'dist');
  writeFileSync(path.join(output, 'THIRD-PARTY-NOTICES.txt'), notices(app));
  const outputManifest = manifest(output, files(output), false);
  if (manifest(root, inputs(), true) !== inputManifest) throw new Error('Frontend inputs changed during generation; run generation again.');

  if (check) {
    // Compare actual files, not just the committed manifest, to establish source/output correspondence.
    if (manifest(path.join(root, 'dist'), files(path.join(root, 'dist')), false) !== outputManifest
      || readFileSync(path.join(root, 'assets.inputs.sha256'), 'utf8') !== inputManifest
      || readFileSync(path.join(root, 'assets.outputs.sha256'), 'utf8') !== outputManifest) {
      throw new Error('Committed Dashboard assets differ from clean regeneration. Run npm run assets:generate and commit dist/ and both assets.*.sha256 manifests.');
    }
    console.log('Committed Dashboard assets match independent clean regeneration.');
  } else {
    rmSync(path.join(root, 'dist'), { recursive: true, force: true });
    cpSync(output, path.join(root, 'dist'), { recursive: true });
    writeFileSync(path.join(root, 'assets.inputs.sha256'), inputManifest);
    writeFileSync(path.join(root, 'assets.outputs.sha256'), outputManifest);
    console.log('Updated dist/ and both assets.*.sha256 manifests.');
  }
} finally {
  rmSync(temporary, { recursive: true, force: true });
}
