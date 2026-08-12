import { describe, expect, test } from 'vitest';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  discoverMaintainedProjects,
  normalizeProjectPath,
  readSolutionProjects,
  validateProjectEvaluations,
  validateSolutionCoverage,
} from '../scripts/lib/project-policy.mjs';

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(siteRoot, '..', '..');

function evaluation({
  framework = 'net10.0',
  packages = [],
  versionException = '',
} = {}) {
  return {
    Properties: {
      TargetFramework: framework,
      TargetFrameworks: '',
      OrleansDocumentationVersionException: versionException,
    },
    Items: {
      PackageReference: packages.map(([Identity, Version]) => ({ Identity, Version })),
      PackageVersion: [],
      ProjectReference: [],
    },
  };
}

describe('documentation project policy', () => {
  test('normalizes Windows and Linux project paths consistently', () => {
    expect(normalizeProjectPath('.\\samples\\HelloWorld\\HelloWorld.csproj')).toBe(
      'samples/HelloWorld/HelloWorld.csproj',
    );
    expect(normalizeProjectPath('./docs/tools/../tools/Tool.csproj')).toBe(
      'docs/tools/Tool.csproj',
    );
  });

  test('reports missing, duplicate, stale, and external solution entries', () => {
    const issues = validateSolutionCoverage({
      discoveredProjects: ['docs/a.csproj', 'samples/b.fsproj'],
      solutionProjects: [
        { project: 'docs/a.csproj' },
        { project: '.\\docs\\a.csproj' },
        { project: 'samples/stale.csproj' },
        { project: 'src/runtime.csproj' },
      ],
    });

    expect(issues).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ rule: 'PROJECT001', project: 'samples/b.fsproj' }),
        expect.objectContaining({ rule: 'PROJECT002', project: 'docs/a.csproj' }),
        expect.objectContaining({ rule: 'PROJECT003', project: 'samples/stale.csproj' }),
        expect.objectContaining({ rule: 'PROJECT003', project: 'src/runtime.csproj' }),
      ]),
    );
  });

  test('normalizes project paths returned by dotnet solution tooling', async () => {
    const solutionFile = path.join(repoRoot, 'docs', 'Docs.slnx');
    expect(
      await readSolutionProjects({
        repoRoot,
        solutionFile,
        listProjects: async (requestedSolution) => {
          expect(requestedSolution).toBe(solutionFile);
          return ['site\\Tool.csproj', '../samples/App/App.csproj'];
        },
      }),
    ).toEqual([
      { solutionPath: 'site\\Tool.csproj', project: 'docs/site/Tool.csproj' },
      {
        solutionPath: '../samples/App/App.csproj',
        project: 'samples/App/App.csproj',
      },
    ]);
  });

  test('reports target framework and exact Orleans package drift', () => {
    const result = validateProjectEvaluations({
      projectEvaluations: [
        {
          project: 'docs/current.csproj',
          evaluation: evaluation({
            framework: 'net8.0',
            packages: [['Microsoft.Orleans.Server', '10.2.1']],
          }),
        },
      ],
      targetFramework: 'net10.0',
      orleansPackageVersion: '10.2.2',
    });

    expect(result.issues).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ rule: 'PROJECT004' }),
        expect.objectContaining({ rule: 'PROJECT005' }),
      ]),
    );
  });

  test('uses evaluated central package versions', () => {
    const current = evaluation();
    current.Properties.ManagePackageVersionsCentrally = 'true';
    current.Items.PackageReference = [{ Identity: 'Microsoft.Orleans.Client' }];
    current.Items.PackageVersion = [
      { Identity: 'Microsoft.Orleans.Client', Version: '10.2.2' },
    ];
    const result = validateProjectEvaluations({
      projectEvaluations: [{ project: 'samples/client.csproj', evaluation: current }],
      targetFramework: 'net10.0',
      orleansPackageVersion: '10.2.2',
    });

    expect(result.issues).toEqual([]);
    expect(result.orleansPackageReferences).toBe(1);
  });

  test('does not use PackageVersion items when central management is disabled', () => {
    const current = evaluation();
    current.Properties.ManagePackageVersionsCentrally = 'false';
    current.Items.PackageReference = [{ Identity: 'Microsoft.Orleans.Client' }];
    current.Items.PackageVersion = [
      { Identity: 'Microsoft.Orleans.Client', Version: '10.2.2' },
    ];
    const result = validateProjectEvaluations({
      projectEvaluations: [{ project: 'samples/client.csproj', evaluation: current }],
      targetFramework: 'net10.0',
      orleansPackageVersion: '10.2.2',
    });

    expect(result.issues).toEqual([
      expect.objectContaining({
        rule: 'PROJECT005',
        message: expect.stringContaining("'(missing)'"),
      }),
    ]);
  });

  test('allows only used, reasoned migration and sample package exceptions', () => {
    const project = 'docs/site/src/content/docs/migration/snippets/Legacy.csproj';
    const reason = 'Compiles the Orleans 9 source side of the migration example.';
    const accepted = validateProjectEvaluations({
      projectEvaluations: [
        {
          project,
          evaluation: evaluation({
            packages: [['Microsoft.Orleans.Server', '9.2.1']],
            versionException: reason,
          }),
        },
      ],
      targetFramework: 'net10.0',
      orleansPackageVersion: '10.2.2',
    });
    expect(accepted.issues).toEqual([]);

    const prereleaseSample = validateProjectEvaluations({
      projectEvaluations: [
        {
          project: 'samples/Experimental/Experimental.csproj',
          evaluation: evaluation({
            packages: [['Microsoft.Orleans.Experimental', '10.2.2-rc.2.alpha.1']],
            versionException:
              'Uses an unpublished prerelease package for an experimental sample.',
          }),
        },
      ],
      targetFramework: 'net10.0',
      orleansPackageVersion: '10.2.2',
    });
    expect(prereleaseSample.issues).toEqual([]);

    const olderSample = validateProjectEvaluations({
      projectEvaluations: [
        {
          project: 'samples/Legacy/Legacy.csproj',
          evaluation: evaluation({
            packages: [['Microsoft.Orleans.Server', '10.2.1']],
            versionException:
              'Samples cannot use historical versions outside migration guidance.',
          }),
        },
      ],
      targetFramework: 'net10.0',
      orleansPackageVersion: '10.2.2',
    });
    expect(olderSample.issues).toEqual([
      expect.objectContaining({ rule: 'PROJECT005' }),
    ]);

    const rejected = validateProjectEvaluations({
      projectEvaluations: [
        {
          project: 'docs/site/src/content/docs/host/snippets/Legacy.csproj',
          evaluation: evaluation({
            packages: [['Microsoft.Orleans.Server', '9.2.1']],
            versionException: 'Too short',
          }),
        },
      ],
      targetFramework: 'net10.0',
      orleansPackageVersion: '10.2.2',
    });
    expect(rejected.issues).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ rule: 'PROJECT005' }),
        expect.objectContaining({ rule: 'PROJECT006' }),
      ]),
    );

    const stale = validateProjectEvaluations({
      projectEvaluations: [
        {
          project,
          evaluation: evaluation({
            packages: [['Microsoft.Orleans.Server', '10.2.2']],
            versionException: reason,
          }),
        },
      ],
      targetFramework: 'net10.0',
      orleansPackageVersion: '10.2.2',
    });
    expect(stale.issues).toEqual([
      expect.objectContaining({ rule: 'PROJECT006', message: expect.stringContaining('stale') }),
    ]);
  });

  test('checked-in solution exactly covers a clean project discovery', async () => {
    const discoveredProjects = await discoverMaintainedProjects(repoRoot);
    const docsSolutionProjects = await readSolutionProjects({
      repoRoot,
      solutionFile: path.join(repoRoot, 'docs', 'Docs.slnx'),
    });
    const sampleSolutionProjects = await readSolutionProjects({
      repoRoot,
      solutionFile: path.join(repoRoot, 'samples', 'Samples.slnx'),
    });
    expect(
      validateSolutionCoverage({
        discoveredProjects: discoveredProjects.filter((project) =>
          project.startsWith('docs/'),
        ),
        solutionProjects: docsSolutionProjects,
        solutionName: 'docs/Docs.slnx',
      }),
    ).toEqual([]);
    expect(
      validateSolutionCoverage({
        discoveredProjects: discoveredProjects.filter((project) =>
          project.startsWith('samples/'),
        ),
        solutionProjects: sampleSolutionProjects,
        solutionName: 'samples/Samples.slnx',
      }),
    ).toEqual([]);
    expect(discoveredProjects).not.toContainEqual(
      expect.stringContaining('/snippets-v3/'),
    );
  });
});
