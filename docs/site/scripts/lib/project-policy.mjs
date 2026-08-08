import { execFile } from 'node:child_process';
import { readdir } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
const projectExtensions = new Set(['.csproj', '.fsproj', '.vbproj']);
const ignoredDirectories = new Set([
  '.generated',
  '.git',
  '.vs',
  'artifacts',
  'bin',
  'dist',
  'node_modules',
  'obj',
]);

export function normalizeProjectPath(value) {
  return path.posix
    .normalize(value.replaceAll('\\', '/'))
    .replace(/^(?:\.\/)+/, '');
}

function projectKey(value) {
  return normalizeProjectPath(value).toLowerCase();
}

async function walkProjects(directory) {
  const projects = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && ignoredDirectories.has(entry.name.toLowerCase())) continue;
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      projects.push(...(await walkProjects(entryPath)));
    } else if (entry.isFile() && projectExtensions.has(path.extname(entry.name).toLowerCase())) {
      projects.push(entryPath);
    }
  }
  return projects;
}

export async function discoverMaintainedProjects(repoRoot) {
  const projects = [
    ...(await walkProjects(path.join(repoRoot, 'docs'))),
    ...(await walkProjects(path.join(repoRoot, 'samples'))),
  ];
  return projects
    .map((project) => normalizeProjectPath(path.relative(repoRoot, project)))
    .sort();
}

async function listSolutionProjectPaths(solutionFile) {
  const { stdout } = await execFileAsync(
    'dotnet',
    ['sln', solutionFile, 'list'],
    {
      maxBuffer: 4 * 1024 * 1024,
      windowsHide: true,
      env: {
        ...process.env,
        DOTNET_NOLOGO: 'true',
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
      },
    },
  );
  const lines = stdout.replaceAll('\r\n', '\n').split('\n');
  const separator = lines.findIndex((line) => /^-+$/.test(line.trim()));
  if (separator < 0) {
    throw new Error(`dotnet sln did not return a project-list separator for '${solutionFile}'.`);
  }
  return lines.slice(separator + 1).map((line) => line.trim()).filter(Boolean);
}

export async function readSolutionProjects({
  repoRoot,
  solutionFile,
  listProjects = listSolutionProjectPaths,
}) {
  const solutionDirectory = path.dirname(solutionFile);
  return (await listProjects(solutionFile)).map((solutionPath) => {
    return {
      solutionPath,
      project: normalizeProjectPath(
        path.relative(repoRoot, path.resolve(solutionDirectory, solutionPath)),
      ),
    };
  });
}

export function validateSolutionCoverage({ discoveredProjects, solutionProjects }) {
  const issues = [];
  const discovered = new Map(discoveredProjects.map((project) => [projectKey(project), project]));
  const membership = new Map();
  for (const entry of solutionProjects) {
    const key = projectKey(entry.project);
    membership.set(key, [...(membership.get(key) ?? []), entry]);
  }

  for (const [key, project] of discovered) {
    if (!membership.has(key)) {
      issues.push({
        rule: 'PROJECT001',
        project,
        message: 'Maintained project is missing from docs/Docs.slnx.',
        remediation: 'Add the project with dotnet sln and preserve its directory hierarchy.',
      });
    }
  }
  for (const entries of membership.values()) {
    if (entries.length > 1) {
      issues.push({
        rule: 'PROJECT002',
        project: entries[0].project,
        message: `Project occurs ${entries.length} times in docs/Docs.slnx.`,
        remediation: 'Remove duplicate solution entries.',
      });
    }
    const project = entries[0].project;
    const key = projectKey(project);
    if (!discovered.has(key)) {
      issues.push({
        rule: 'PROJECT003',
        project,
        message: project.startsWith('docs/') || project.startsWith('samples/')
          ? 'Solution entry is stale or does not name a maintained project.'
          : 'Solution entry is outside the maintained docs and samples trees.',
        remediation: 'Remove the stale entry; referenced source projects build transitively.',
      });
    }
  }
  return issues;
}

function evaluatedFrameworks(evaluation) {
  const values = [
    evaluation.Properties?.TargetFramework,
    evaluation.Properties?.TargetFrameworks,
  ].filter(Boolean);
  return [...new Set(values.flatMap((value) => value.split(';').map((item) => item.trim())))];
}

function effectivePackageVersion(reference, packageVersions, centralPackageManagementEnabled) {
  if (reference.VersionOverride) return String(reference.VersionOverride);
  if (reference.Version) return String(reference.Version);
  if (!centralPackageManagementEnabled) return '';
  return String(
    packageVersions.find(
      (candidate) => candidate.Identity.toLowerCase() === reference.Identity.toLowerCase(),
    )?.Version ?? '',
  );
}

function versionParts(value) {
  const match = /^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$/.exec(value);
  return match?.slice(1).map(Number);
}

function isOlderVersion(value, requiredVersion) {
  const actual = versionParts(value);
  const required = versionParts(requiredVersion);
  if (!actual || !required) return false;
  for (let index = 0; index < required.length; index += 1) {
    if (actual[index] !== required[index]) return actual[index] < required[index];
  }
  return false;
}

export function validateProjectEvaluations({
  projectEvaluations,
  targetFramework,
  orleansPackageVersion,
}) {
  const issues = [];
  let orleansPackageReferences = 0;

  for (const { project, evaluation } of projectEvaluations) {
    const frameworks = evaluatedFrameworks(evaluation);
    if (frameworks.length !== 1 || frameworks[0] !== targetFramework) {
      issues.push({
        rule: 'PROJECT004',
        project,
        message: `Project evaluates to target framework(s) '${frameworks.join(';') || '(missing)'}'.`,
        remediation: `Target exactly ${targetFramework}; maintained docs and samples do not multi-target.`,
      });
    }

    const packageVersions = evaluation.Items?.PackageVersion ?? [];
    const centralPackageManagementEnabled =
      String(evaluation.Properties?.ManagePackageVersionsCentrally).toLowerCase() === 'true';
    const exceptionReason = String(
      evaluation.Properties?.OrleansDocumentationVersionException ?? '',
    ).trim();
    const migrationProject = project.startsWith(
      'docs/site/src/content/docs/migration/',
    );
    const validException =
      migrationProject && exceptionReason.length >= 20;
    let exceptionUsed = false;
    if (exceptionReason && !migrationProject) {
      issues.push({
        rule: 'PROJECT006',
        project,
        message: 'OrleansDocumentationVersionException is restricted to migration projects.',
        remediation: 'Remove the exception or move the intentional historical example under migration/.',
      });
    } else if (exceptionReason && exceptionReason.length < 20) {
      issues.push({
        rule: 'PROJECT006',
        project,
        message: 'OrleansDocumentationVersionException is missing a meaningful reason.',
        remediation: 'Explain the migration scenario which requires the historical package release.',
      });
    }
    for (const reference of evaluation.Items?.PackageReference ?? []) {
      const packageName = String(reference.Identity);
      if (!packageName.toLowerCase().startsWith('microsoft.orleans')) continue;
      orleansPackageReferences += 1;
      const version = effectivePackageVersion(
        reference,
        packageVersions,
        centralPackageManagementEnabled,
      );
      if (version === orleansPackageVersion) continue;
      if (validException && isOlderVersion(version, orleansPackageVersion)) {
        exceptionUsed = true;
        continue;
      }
      issues.push({
        rule: 'PROJECT005',
        project,
        message: `Orleans package '${packageName}' evaluates to '${version || '(missing)'}'.`,
        remediation: `Use exactly ${orleansPackageVersion}; an older migration snippet requires a meaningful OrleansDocumentationVersionException project property.`,
      });
    }
    if (validException && !exceptionUsed) {
      issues.push({
        rule: 'PROJECT006',
        project,
        message: 'OrleansDocumentationVersionException is stale because no older Orleans package reference uses it.',
        remediation: 'Remove the exception or restore the intentional historical package reference.',
      });
    }
  }
  return { issues, orleansPackageReferences };
}

export async function evaluateProject(projectFile) {
  const { stdout } = await execFileAsync(
    'dotnet',
    [
      'msbuild',
      projectFile,
      '-nologo',
      '-getProperty:TargetFramework',
      '-getProperty:TargetFrameworks',
      '-getProperty:ManagePackageVersionsCentrally',
      '-getProperty:OrleansDocumentationVersionException',
      '-getItem:PackageReference',
      '-getItem:PackageVersion',
      '-getItem:ProjectReference',
    ],
    {
      maxBuffer: 16 * 1024 * 1024,
      windowsHide: true,
      env: {
        ...process.env,
        DOTNET_NOLOGO: 'true',
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
      },
    },
  );
  return JSON.parse(stdout);
}

async function mapConcurrent(items, concurrency, callback) {
  const results = new Array(items.length);
  let next = 0;
  async function worker() {
    while (next < items.length) {
      const index = next;
      next += 1;
      results[index] = await callback(items[index], index);
    }
  }
  await Promise.all(
    Array.from({ length: Math.min(concurrency, items.length) }, () => worker()),
  );
  return results;
}

export async function auditProjectPolicy({
  repoRoot,
  solutionFile,
  policy,
  evaluate = evaluateProject,
  concurrency = 8,
}) {
  const discoveredProjects = await discoverMaintainedProjects(repoRoot);
  const solutionProjects = await readSolutionProjects({ repoRoot, solutionFile });
  const issues = validateSolutionCoverage({ discoveredProjects, solutionProjects });
  const projectEvaluations = await mapConcurrent(
    discoveredProjects,
    concurrency,
    async (project) => {
      try {
        return {
          project,
          evaluation: await evaluate(path.join(repoRoot, project)),
        };
      } catch (error) {
        return { project, error };
      }
    },
  );
  for (const result of projectEvaluations) {
    if (!result.error) continue;
    issues.push({
      rule: 'PROJECT000',
      project: result.project,
      message: `MSBuild evaluation failed: ${result.error.message}`,
      remediation: 'Fix project/import evaluation errors.',
    });
  }
  const evaluationAudit = validateProjectEvaluations({
    projectEvaluations: projectEvaluations.filter((result) => !result.error),
    ...policy,
  });
  issues.push(...evaluationAudit.issues);
  return {
    issues,
    projects: discoveredProjects,
    docsProjects: discoveredProjects.filter((project) => project.startsWith('docs/')).length,
    sampleProjects: discoveredProjects.filter((project) => project.startsWith('samples/')).length,
    solutionEntries: solutionProjects.length,
    orleansPackageReferences: evaluationAudit.orleansPackageReferences,
  };
}
