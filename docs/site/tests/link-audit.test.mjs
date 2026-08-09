import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import {
  auditRenderedInternalLinks,
  auditSourceLinks,
  collectLinkAuditDocuments,
  collectYamlLinkReferences,
  createPinnedRequestOptions,
  isPublicInternetAddress,
  probeExternalTargets,
} from '../scripts/lib/link-audit.mjs';

const temporaryDirectories = [];

async function temporaryDirectory() {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'orleans-link-audit-'));
  temporaryDirectories.push(directory);
  return directory;
}

afterEach(async () => {
  await Promise.all(
    temporaryDirectories.splice(0).map((directory) =>
      rm(directory, { recursive: true, force: true }),
    ),
  );
});

describe('source link audit', () => {
  test('rejects migrated Learn-relative and missing Orleans pages with source lines', async () => {
    const sourceRoot = await temporaryDirectory();
    const host = path.join(sourceRoot, 'host');
    await mkdir(host);
    const file = path.join(host, 'tls.md');
    const result = await auditSourceLinks({
      sourceRoot,
      documents: [
        {
          file,
          source: [
            '# TLS',
            '[Learn](../../core/extensions/sslstream-best-practices.md)',
            '[Missing](missing.md)',
          ].join('\r\n'),
        },
      ],
    });

    expect(result.issues).toEqual([
      expect.objectContaining({
        file: 'host/tls.md',
        line: 2,
        message: expect.stringContaining('resolves outside'),
        remediation: expect.stringContaining(
          'https://learn.microsoft.com/dotnet/core/extensions/sslstream-best-practices',
        ),
      }),
      expect.objectContaining({
        line: 3,
        message: expect.stringContaining('missing Orleans source page'),
      }),
    ]);
  });

  test('accepts internal, fragment, and absolute Learn links', async () => {
    const sourceRoot = await temporaryDirectory();
    await mkdir(path.join(sourceRoot, 'host'));
    await writeFile(path.join(sourceRoot, 'target.md'), '# Target\n');
    const file = path.join(sourceRoot, 'host', 'guide.md');
    const result = await auditSourceLinks({
      sourceRoot,
      documents: [
        {
          file,
          source: [
            '[Relative](../target.md)',
            '[Fragment](#section)',
            '[Local absolute](/orleans/docs/overview/)',
            '[Learn absolute](https://learn.microsoft.com/dotnet/orleans/)',
            '[Published absolute](https://dotnet.github.io/orleans/docs/overview/)',
          ].join('\n'),
        },
      ],
    });

    expect(result.issues).toEqual([]);
    expect([...result.externalTargets.keys()]).toEqual([
      'https://learn.microsoft.com/dotnet/orleans/',
    ]);
  });

  test('rejects Learn root-relative links with a canonical absolute URL', async () => {
    const sourceRoot = await temporaryDirectory();
    const file = path.join(sourceRoot, 'guide.md');
    const result = await auditSourceLinks({
      sourceRoot,
      documents: [
        {
          file,
          source:
            '[Managed identity](/sql/connect/ado-net/authentication.md#using-managed-identity)',
        },
      ],
    });

    expect(result.issues).toEqual([
      expect.objectContaining({
        file: 'guide.md',
        line: 1,
        message: expect.stringContaining('outside the Orleans site route space'),
        remediation: expect.stringContaining(
          'https://learn.microsoft.com/sql/connect/ado-net/authentication#using-managed-identity',
        ),
      }),
    ]);
    expect(result.externalTargets.size).toBe(0);
  });

  test('audits recursive external includes with original source provenance', async () => {
    const siteRoot = await temporaryDirectory();
    const sourceRoot = path.join(siteRoot, 'src', 'content', 'docs');
    const includesRoot = path.join(siteRoot, 'src', 'content', 'includes');
    await mkdir(sourceRoot, { recursive: true });
    await mkdir(includesRoot, { recursive: true });
    await writeFile(
      path.join(sourceRoot, 'guide.md'),
      '[!INCLUDE [outer](../includes/outer.md)]\n',
    );
    await writeFile(
      path.join(includesRoot, 'outer.md'),
      '[!INCLUDE [nested](nested.md)]\n',
    );
    await writeFile(
      path.join(includesRoot, 'nested.md'),
      [
        '# Authentication',
        '[Managed identity](/sql/connect/ado-net/authentication)',
        '[Canonical](https://learn.microsoft.com/sql/connect/ado-net/authentication)',
        '[Missing anchor](#missing)',
      ].join('\r\n'),
    );

    const documents = await collectLinkAuditDocuments({
      sourceRoot,
      allowedRoot: siteRoot,
    });
    const result = await auditSourceLinks({ documents, sourceRoot });

    expect(documents.map(({ file }) => path.basename(file))).toEqual([
      'guide.md',
      'nested.md',
      'outer.md',
    ]);
    expect(result.issues).toEqual([
      expect.objectContaining({
        file: 'includes/nested.md',
        line: 2,
        remediation: expect.stringContaining(
          'https://learn.microsoft.com/sql/connect/ado-net/authentication',
        ),
      }),
    ]);
    expect(
      result.externalTargets.get(
        'https://learn.microsoft.com/sql/connect/ado-net/authentication',
      ),
    ).toEqual([
      expect.objectContaining({
        relativeFile: 'includes/nested.md',
        line: 3,
      }),
    ]);

    const distRoot = path.join(siteRoot, 'dist');
    await mkdir(path.join(distRoot, 'docs', 'guide'), { recursive: true });
    await writeFile(
      path.join(distRoot, 'docs', 'guide', 'index.html'),
      '<h1>Guide</h1><a href="#missing">Missing anchor</a>',
    );
    expect(
      await auditRenderedInternalLinks({
        distRoot,
        internalProvenance: result.internalProvenance,
      }),
    ).toEqual([
      expect.stringContaining(
        "includes/nested.md:4: href '#missing' targets missing anchor '#missing'",
      ),
    ]);
  });

  test('ignores links inside literal Markdown and HTML code', async () => {
    const sourceRoot = await temporaryDirectory();
    const file = path.join(sourceRoot, 'guide.md');
    const result = await auditSourceLinks({
      sourceRoot,
      documents: [
        {
          file,
          source: [
            '```markdown',
            '[Missing](missing.md)',
            '```',
            '`[Missing](missing.md)`',
            '<pre><a href="https://example.invalid/missing">literal</a></pre>',
            '[!INCLUDE [fragment](../includes/fragment.md)]',
            '<a href="https://example.com/active">active</a>',
            '<img src="https://example.com/image.png">',
          ].join('\n'),
        },
      ],
    });

    expect(result.issues).toEqual([]);
    expect([...result.externalTargets.keys()]).toEqual([
      'https://example.com/active',
      'https://example.com/image.png',
    ]);
  });

  test('rejects malformed external URLs with source provenance', async () => {
    const sourceRoot = await temporaryDirectory();
    const file = path.join(sourceRoot, 'guide.md');
    const result = await auditSourceLinks({
      sourceRoot,
      documents: [{ file, source: '[Bad](https://[invalid)' }],
    });

    expect(result.issues).toEqual([
      expect.objectContaining({
        file: 'guide.md',
        line: 1,
        message: expect.stringContaining('Malformed external URL'),
      }),
    ]);
  });

  test('rejects unsafe, unknown, and protocol-relative URL schemes', async () => {
    const sourceRoot = await temporaryDirectory();
    const file = path.join(sourceRoot, 'guide.md');
    const result = await auditSourceLinks({
      sourceRoot,
      documents: [
        {
          file,
          source: [
            '[Typo](htps://example.com)',
            '<a href="javascript:alert(1)">Script</a>',
            '<a href="data:text/html,unsafe">Data</a>',
            '[Protocol relative](//example.com/path)',
          ].join('\n'),
        },
      ],
    });

    expect(result.issues).toHaveLength(4);
    expect(result.issues).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ message: expect.stringContaining('htps:') }),
        expect.objectContaining({ message: expect.stringContaining('javascript:') }),
        expect.objectContaining({ message: expect.stringContaining('data:') }),
        expect.objectContaining({ message: expect.stringContaining('//example.com/path') }),
      ]),
    );
  });

  test('fails a broken YAML URL with source provenance', async () => {
    const sourceRoot = path.join('content', 'docs');
    const file = path.join(sourceRoot, 'index.yml');
    const source = [
      'items:',
      '  - name: Broken',
      '    href: https://broken.example/yaml',
      'summary: See [details](https://broken.example/summary).',
    ].join('\r\n');

    expect(collectYamlLinkReferences({ source, file, sourceRoot })).toEqual([
      expect.objectContaining({
        url: 'https://broken.example/yaml',
        relativeFile: 'index.yml',
        line: 3,
      }),
      expect.objectContaining({
        url: 'https://broken.example/summary',
        relativeFile: 'index.yml',
        line: 4,
      }),
    ]);
    const audit = await auditSourceLinks({
      sourceRoot,
      documents: [{ file, source, kind: 'yaml' }],
    });
    const result = await probeExternalTargets({
      externalTargets: audit.externalTargets,
      lookupImpl: async () => [{ address: '8.8.8.8', family: 4 }],
      requestImpl: async () => ({
        status: 404,
        headers: { get: () => null },
      }),
      retries: 0,
    });
    expect(result.failures).toEqual(
      expect.arrayContaining([
        expect.stringContaining(
          'https://broken.example/yaml (index.yml:3): returned 404',
        ),
      ]),
    );
  });
});

describe('rendered internal link audit', () => {
  async function writePage(distRoot, relative, html) {
    const file = path.join(distRoot, relative);
    await mkdir(path.dirname(file), { recursive: true });
    await writeFile(file, html);
  }

  test('accepts relative, absolute, fragment, encoded, and redirect links', async () => {
    const distRoot = await temporaryDirectory();
    await writePage(
      distRoot,
      'docs/a/index.html',
      [
        '<h1 id="top">A</h1>',
        '<a href="../b/">relative</a>',
        '<a href="/orleans/docs/b/#%74arget">absolute</a>',
        '<a href="#top">fragment</a>',
        '<a href="/orleans/docs/space%20page/">encoded</a>',
      ].join(''),
    );
    await writePage(distRoot, 'docs/b/index.html', '<h1 id="target">B</h1>');
    await writePage(distRoot, 'docs/space page/index.html', '<h1>Space</h1>');
    await writePage(
      distRoot,
      'legacy.html',
      '<meta http-equiv="refresh" content="0; url=/orleans/docs/b/#target"><a href="/orleans/docs/b/">moved</a>',
    );

    expect(await auditRenderedInternalLinks({ distRoot })).toEqual([]);
  });

  test('rejects unsafe and unknown rendered navigation schemes', async () => {
    const distRoot = await temporaryDirectory();
    await writePage(
      distRoot,
      'docs/a/index.html',
      [
        '<a href="javascript:alert(1)">script</a>',
        '<a href="data:text/html,unsafe">data</a>',
        '<a href="htps://example.com">typo</a>',
        '<img src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==">',
      ].join(''),
    );

    expect(await auditRenderedInternalLinks({ distRoot })).toEqual(
      expect.arrayContaining([
        expect.stringContaining("unsupported URL protocol 'javascript:'"),
        expect.stringContaining("unsupported URL protocol 'data:'"),
        expect.stringContaining("unsupported URL protocol 'htps:'"),
      ]),
    );
  });

  test('validates generated repository source links locally', async () => {
    const repositoryRoot = await temporaryDirectory();
    const distRoot = path.join(repositoryRoot, 'dist');
    await mkdir(path.join(repositoryRoot, 'src'));
    await writeFile(path.join(repositoryRoot, 'src', 'Widget.cs'), 'line 1\nline 2\n');
    const commit = 'a'.repeat(40);
    await writePage(
      distRoot,
      'docs/a/index.html',
      [
        `<a href="https://github.com/dotnet/orleans/blob/${commit}/src/Widget.cs#L2">valid</a>`,
        `<a href="https://github.com/dotnet/orleans/blob/${commit}/src/Missing.cs#L1">missing</a>`,
        `<a href="https://github.com/dotnet/orleans/blob/${commit}/src/Widget.cs#L4">line</a>`,
      ].join(''),
    );
    const externalTargets = new Map();

    const issues = await auditRenderedInternalLinks({
      distRoot,
      repositoryRoot,
      externalTargets,
    });

    expect(issues).toEqual(
      expect.arrayContaining([
        expect.stringContaining('targets a missing file'),
        expect.stringContaining('targets line 4'),
      ]),
    );
    expect(externalTargets.size).toBe(0);
  });

  test('rejects missing pages, anchors, malformed encodings, and base escapes', async () => {
    const distRoot = await temporaryDirectory();
    await writePage(
      distRoot,
      'docs/a/index.html',
      [
        '<h1>A</h1>',
        '<a href="../missing/">missing</a>',
        '<a href="../b/#missing">anchor</a>',
        '<a href="/orleans/docs/%ZZ/">encoding</a>',
        '<a href="/outside/">escape</a>',
        '<meta http-equiv="refresh" content="0; url=/orleans/docs/redirect-missing/">',
      ].join(''),
    );
    await writePage(distRoot, 'docs/b/index.html', '<h1 id="target">B</h1>');

    const issues = await auditRenderedInternalLinks({
      distRoot,
      internalProvenance: new Map([
        [
          '/orleans/docs/a/\0/orleans/docs/b/#missing',
          [{ relativeFile: 'docs/a.md', line: 7 }],
        ],
      ]),
    });
    expect(issues).toEqual(
      expect.arrayContaining([
        expect.stringContaining('missing rendered path'),
        expect.stringContaining("docs/a.md:7: href '../b/#missing' targets missing anchor '#missing'"),
        expect.stringMatching(/malformed/i),
        expect.stringMatching(/escapes/i),
        expect.stringContaining('redirect-missing'),
      ]),
    );
  });

  test('does not satisfy a trailing-slash route with a legacy html file', async () => {
    const distRoot = await temporaryDirectory();
    await writePage(
      distRoot,
      'docs/a/index.html',
      '<a href="/orleans/legacy/">legacy</a>',
    );
    await writePage(distRoot, 'legacy.html', '<h1>Legacy compatibility file</h1>');

    expect(await auditRenderedInternalLinks({ distRoot })).toEqual([
      expect.stringContaining(
        "href '/orleans/legacy/' targets a missing rendered path",
      ),
    ]);
  });

  test('collects rendered xref, component, media, and meta external targets', async () => {
    const distRoot = await temporaryDirectory();
    await writePage(
      distRoot,
      'docs/generated/index.html',
      [
        '<a href="https://learn.microsoft.com/dotnet/api/orleans.igrain">xref</a>',
        '<script src="https://cdn.example/component.js"></script>',
        '<img src="https://cdn.example/image.png">',
        '<source srcset="https://cdn.example/small.png 1x, https://cdn.example/large.png 2x">',
        '<meta http-equiv="refresh" content="0; url=https://redirect.example/new">',
      ].join(''),
    );
    const externalTargets = new Map();

    expect(
      await auditRenderedInternalLinks({ distRoot, externalTargets }),
    ).toEqual([]);
    expect([...externalTargets.keys()].sort()).toEqual(
      [
        'https://cdn.example/component.js',
        'https://cdn.example/image.png',
        'https://cdn.example/large.png',
        'https://cdn.example/small.png',
        'https://learn.microsoft.com/dotnet/api/orleans.igrain',
        'https://redirect.example/new',
      ].sort(),
    );
    expect(
      externalTargets.get(
        'https://learn.microsoft.com/dotnet/api/orleans.igrain',
      ),
    ).toEqual([
      expect.objectContaining({
        relativeFile: '/orleans/docs/generated/',
        rendered: true,
      }),
    ]);
  });

  test('deduplicates source and rendered external targets while retaining source provenance', async () => {
    const distRoot = await temporaryDirectory();
    const url = 'https://broken.example/duplicate';
    await writePage(
      distRoot,
      'docs/guide/index.html',
      `<a href="${url}">duplicate</a>`,
    );
    const externalTargets = new Map([
      [
        url,
        [
          { relativeFile: 'guide.md', line: 5 },
          { relativeFile: 'includes/shared.md', line: 2 },
        ],
      ],
    ]);
    await auditRenderedInternalLinks({ distRoot, externalTargets });
    let requests = 0;
    const result = await probeExternalTargets({
      externalTargets,
      lookupImpl: async () => [{ address: '8.8.8.8', family: 4 }],
      requestImpl: async () => {
        requests += 1;
        return { status: 404, headers: { get: () => null } };
      },
      retries: 0,
    });

    expect(result.probed).toBe(1);
    expect(requests).toBe(1);
    expect(result.failures).toEqual([
      expect.stringContaining('guide.md:5, includes/shared.md:2'),
    ]);
  });
});

describe('external link audit', () => {
  const publicLookup = async () => [{ address: '8.8.8.8', family: 4 }];
  const response = (status, location) => ({
    status,
    headers: {
      get: (name) =>
        name.toLowerCase() === 'location' ? location ?? null : null,
    },
  });

  test('deduplicates, follows redirects, and falls back when HEAD is unsupported', async () => {
    const counts = new Map();
    const target = 'https://public.example/head-rejected';
    const externalTargets = new Map([
      [
        target,
        [
          { relativeFile: 'a.md', line: 1 },
          { relativeFile: 'b.md', line: 2 },
        ],
      ],
      ['https://public.example/redirect', [{ relativeFile: 'a.md', line: 3 }]],
    ]);

    const result = await probeExternalTargets({
      externalTargets,
      timeoutMs: 1_000,
      retries: 0,
      concurrency: 2,
      lookupImpl: publicLookup,
      requestImpl: async (url, options) => {
        const key = `${options.method} ${url.pathname}`;
        counts.set(key, (counts.get(key) ?? 0) + 1);
        if (url.pathname === '/redirect') {
          return response(302, '/ok');
        }
        if (url.pathname === '/head-rejected' && options.method === 'HEAD') {
          return response(405);
        }
        return response(200);
      },
    });
    expect(result.failures).toEqual([]);
    expect(result.probed).toBe(2);
    expect(counts.get('HEAD /head-rejected')).toBe(1);
    expect(counts.get('GET /head-rejected')).toBe(1);
    expect(counts.get('HEAD /redirect')).toBe(1);
    expect(counts.get('HEAD /ok')).toBe(1);
  });

  test('falls back when known hosts reject HEAD requests', async () => {
    const counts = new Map();
    const headStatuses = new Map([
      ['azure.microsoft.com', 404],
      ['twitter.com', 403],
      ['www.nuget.org', 404],
    ]);
    const result = await probeExternalTargets({
      externalTargets: new Map(
        [...headStatuses.keys()].map((host) => [
          `https://${host}/resource`,
          [{ relativeFile: 'guide.md', line: 1 }],
        ]),
      ),
      retries: 0,
      lookupImpl: publicLookup,
      requestImpl: async (url, options) => {
        const key = `${url.hostname} ${options.method}`;
        counts.set(key, (counts.get(key) ?? 0) + 1);
        return response(
          options.method === 'HEAD' ? headStatuses.get(url.hostname) : 200,
        );
      },
    });

    expect(result.failures).toEqual([]);
    for (const host of headStatuses.keys()) {
      expect(counts.get(`${host} HEAD`)).toBe(1);
      expect(counts.get(`${host} GET`)).toBe(1);
    }
  });

  test('fails definitive missing statuses and redirect loops', async () => {
    const externalTargets = new Map(
      ['/missing', '/gone', '/loop-a'].map((route) => [
        `https://public.example${route}`,
        [{ relativeFile: 'guide.md', line: 4 }],
      ]),
    );

    const result = await probeExternalTargets({
      externalTargets,
      timeoutMs: 1_000,
      retries: 0,
      maxRedirects: 4,
      lookupImpl: publicLookup,
      requestImpl: async (url) => {
        if (url.pathname === '/loop-a') return response(302, '/loop-b');
        if (url.pathname === '/loop-b') return response(302, '/loop-a');
        if (url.pathname === '/gone') return response(410);
        return response(404);
      },
    });
    expect(result.failures).toEqual(
      expect.arrayContaining([
        expect.stringContaining('returned 404'),
        expect.stringContaining('returned 410'),
        expect.stringContaining('Redirect loop'),
      ]),
    );
  });

  test('rejects redirects to invalid destinations', async () => {
    const result = await probeExternalTargets({
      externalTargets: new Map([
        ['https://public.example/invalid', [{ relativeFile: 'guide.md', line: 1 }]],
      ]),
      retries: 0,
      lookupImpl: publicLookup,
      requestImpl: async () => response(302, 'file:///etc/passwd'),
    });

    expect(result.failures).toEqual([
      expect.stringContaining('invalid destination'),
    ]);
  });

  test('reports timeout and rate-limit failures explicitly without failing transient CI', async () => {
    const externalTargets = new Map(
      ['/rate', '/slow'].map((route) => [
        `https://public.example${route}`,
        [{ relativeFile: 'guide.md', line: 5 }],
      ]),
    );

    let rateRequests = 0;
    const result = await probeExternalTargets({
      externalTargets,
      timeoutMs: 20,
      retries: 1,
      lookupImpl: publicLookup,
      requestImpl: async (url) => {
        if (url.pathname === '/rate') {
          rateRequests += 1;
          return response(429);
        }
        const error = new Error('timed out');
        error.name = 'TimeoutError';
        error.code = 'ETIMEDOUT';
        throw error;
      },
    });
    expect(result.failures).toEqual([]);
    expect(result.warnings).toEqual(
      expect.arrayContaining([
        expect.stringContaining('429'),
        expect.stringContaining('Transient external link failure'),
      ]),
    );
    expect(rateRequests).toBe(2);
  });

  test('fails permanent DNS or TLS-style network errors', async () => {
    const error = new TypeError('fetch failed', {
      cause: Object.assign(new Error('not found'), { code: 'ENOTFOUND' }),
    });
    const result = await probeExternalTargets({
      externalTargets: new Map([
        [
          'https://definitely-missing-link-audit.invalid/',
          [{ relativeFile: 'guide.md', line: 1 }],
        ],
      ]),
      lookupImpl: async () => [{ address: '8.8.8.8', family: 4 }],
      requestImpl: async () => {
        throw error;
      },
      retries: 2,
    });

    expect(result.warnings).toEqual([]);
    expect(result.failures).toEqual([
      expect.stringContaining('ENOTFOUND'),
    ]);
  });

  test('rejects private, special-use, mapped, credentialed, and arbitrary-port targets', async () => {
    expect(isPublicInternetAddress('8.8.8.8')).toBe(true);
    expect(isPublicInternetAddress('2606:4700:4700::1111')).toBe(true);
    expect(isPublicInternetAddress('127.0.0.1')).toBe(false);
    expect(isPublicInternetAddress('169.254.169.254')).toBe(false);
    expect(isPublicInternetAddress('::1')).toBe(false);
    expect(isPublicInternetAddress('::ffff:7f00:1')).toBe(false);
    expect(isPublicInternetAddress('fc00::1')).toBe(false);
    expect(isPublicInternetAddress('fe80::1')).toBe(false);
    expect(isPublicInternetAddress('fec0::1')).toBe(false);

    let requests = 0;
    const urls = [
      'http://127.0.0.1/',
      'http://2130706433/',
      'http://0177.0.0.1/',
      'http://0x7f000001/',
      'http://[::1]/',
      'http://[::ffff:7f00:1]/',
      'http://[fc00::1]/',
      'http://[fe80::1]/',
      'http://[fec0::1]/',
      'http://localhost/',
      'https://user:secret@example.com/',
      'https://example.com:8443/',
    ];
    const result = await probeExternalTargets({
      externalTargets: new Map(
        urls.map((url) => [url, [{ relativeFile: 'guide.md', line: 1 }]]),
      ),
      lookupImpl: async (hostname) => [
        {
          address: hostname === 'localhost' ? '127.0.0.1' : '8.8.8.8',
          family: 4,
        },
      ],
      requestImpl: async () => {
        requests += 1;
        throw new Error('must not connect');
      },
      retries: 0,
    });

    expect(requests).toBe(0);
    expect(result.failures).toHaveLength(urls.length);
    expect(result.failures).toEqual(
      expect.arrayContaining([
        expect.stringContaining('non-public address'),
        expect.stringContaining('must not contain credentials'),
        expect.stringContaining('disallowed port'),
      ]),
    );
  });

  test('validates every redirect and rejects mixed public-private DNS answers', async () => {
    let requests = 0;
    const redirectResult = await probeExternalTargets({
      externalTargets: new Map([
        ['https://public.example/redirect', [{ relativeFile: 'guide.md', line: 2 }]],
      ]),
      lookupImpl: async () => [{ address: '8.8.8.8', family: 4 }],
      requestImpl: async () => {
        requests += 1;
        return {
          status: 302,
          headers: {
            get: (name) =>
              name.toLowerCase() === 'location'
                ? 'http://169.254.169.254/latest/meta-data/'
                : null,
          },
        };
      },
      retries: 0,
    });
    expect(requests).toBe(1);
    expect(redirectResult.failures).toEqual([
      expect.stringContaining("non-public address '169.254.169.254'"),
    ]);

    const mixedResult = await probeExternalTargets({
      externalTargets: new Map([
        ['https://mixed.example/', [{ relativeFile: 'guide.md', line: 3 }]],
      ]),
      lookupImpl: async () => [
        { address: '8.8.8.8', family: 4 },
        { address: '10.0.0.1', family: 4 },
      ],
      requestImpl: async () => {
        throw new Error('must not connect');
      },
      retries: 0,
    });
    expect(mixedResult.failures).toEqual([
      expect.stringContaining("non-public address '10.0.0.1'"),
    ]);
  });

  test('pins the validated DNS address passed to the HTTP transport', async () => {
    let lookups = 0;
    let destination;
    const result = await probeExternalTargets({
      externalTargets: new Map([
        ['https://public.example/', [{ relativeFile: 'guide.md', line: 4 }]],
      ]),
      lookupImpl: async () => {
        lookups += 1;
        return [{ address: '8.8.4.4', family: 4 }];
      },
      requestImpl: async (_url, options) => {
        destination = options.destination;
        return { status: 204, headers: { get: () => null } };
      },
      retries: 0,
    });
    expect(result.failures).toEqual([]);
    expect(lookups).toBe(1);
    expect(destination).toMatchObject({
      hostname: 'public.example',
      address: '8.8.4.4',
      family: 4,
    });
  });

  test('preserves the original host and TLS SNI while pinning DNS', async () => {
    const options = createPinnedRequestOptions(
      new URL('https://docs.example/path'),
      {
        method: 'GET',
        destination: {
          hostname: 'docs.example',
          address: '8.8.8.8',
          family: 4,
        },
      },
    );
    expect(options.headers).toEqual({
      Host: 'docs.example',
      Range: 'bytes=0-0',
    });
    expect(options.servername).toBe('docs.example');
    expect(options.rejectUnauthorized).toBe(true);
    await new Promise((resolve, reject) => {
      options.lookup('docs.example', { all: false }, (error, address, family) => {
        if (error) reject(error);
        else {
          expect(address).toBe('8.8.8.8');
          expect(family).toBe(4);
          resolve();
        }
      });
    });
  });

  test('enforces an aggregate request target cap', async () => {
    const result = await probeExternalTargets({
      externalTargets: new Map([
        ['https://one.example/', [{ relativeFile: 'guide.md', line: 1 }]],
        ['https://two.example/', [{ relativeFile: 'guide.md', line: 2 }]],
      ]),
      maxTargets: 1,
      requestImpl: async () => {
        throw new Error('must not connect');
      },
    });
    expect(result.probed).toBe(0);
    expect(result.failures).toEqual([
      expect.stringContaining('exceeding the request cap'),
    ]);
  });

  test('requires exact, reasoned, non-stale allowlist entries', async () => {
    const url = 'https://example.com/unprobeable';
    const privateUrl = 'http://127.0.0.1/internal';
    const result = await probeExternalTargets({
      externalTargets: new Map([
        [url, [{ relativeFile: 'guide.md', line: 1 }]],
        [privateUrl, [{ relativeFile: 'guide.md', line: 2 }]],
      ]),
      allowlist: {
        urls: {
          [url]: 'This endpoint blocks automated probes but is reviewed manually.',
          [privateUrl]: 'This reason is deliberately long but cannot bypass destination safety.',
          'not a URL': 'This reason is long enough but the URL is malformed.',
          'https://example.com/stale': 'This exact target is no longer referenced anywhere.',
        },
      },
      lookupImpl: async () => [{ address: '8.8.8.8', family: 4 }],
    });

    expect(result.probed).toBe(0);
    expect(result.warnings).toContainEqual(expect.stringContaining('Allowlisted'));
    expect(result.failures).toEqual(
      expect.arrayContaining([
        expect.stringContaining('malformed URL'),
        expect.stringContaining('stale'),
        expect.stringContaining('unsafe or unresolved destination'),
        expect.stringContaining("non-public address '127.0.0.1'"),
      ]),
    );
  });
});
