import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, test } from 'vitest';
import {
  cleanMarkdownOutputDirectory,
  cleanPublishedMarkdown,
  publishMarkdownOverview,
} from '../src/plugins/clean-markdown-output.mjs';

const temporaryDirectories = [];

afterEach(async () => {
  await Promise.all(
    temporaryDirectories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })),
  );
});

describe('published Markdown cleaning', () => {
  test('converts prepared Starlight content to plain Markdown', () => {
    const source = `---
title: Orleans clients
description: Choose and host an Orleans client.
---

[Client configuration](/orleans/docs/host/configuration-guide/client-configuration/)
[Streams](https://dotnet.github.io/orleans/docs/streaming/#providers)
[External](https://learn.microsoft.com/dotnet/)

{/* Source: snippets/HostingExamples.cs; region: local_silo_and_client */}
\`\`\`csharp
builder.UseOrleans();
\`\`\`
`;

    const markdown = cleanPublishedMarkdown(source);

    assert.match(markdown, /^# Orleans clients/m);
    assert.match(markdown, /builder\.UseOrleans\(\);/);
    assert.match(
      markdown,
      /\[Client configuration\]\(\/orleans\/docs\/host\/configuration-guide\/client-configuration\.md\)/,
    );
    assert.match(
      markdown,
      /\[Streams\]\(https:\/\/dotnet\.github\.io\/orleans\/docs\/streaming\.md#providers\)/,
    );
    assert.match(markdown, /\[External\]\(https:\/\/learn\.microsoft\.com\/dotnet\/\)/);
    assert.doesNotMatch(markdown, /^---$/m);
    assert.doesNotMatch(markdown, /\{\/\*/);
  });

  test('cleans documentation routes without changing API Markdown', async () => {
    const outputRoot = await mkdtemp(path.join(os.tmpdir(), 'orleans-docs-markdown-'));
    temporaryDirectories.push(outputRoot);
    const conceptual = path.join(outputRoot, 'docs', 'host', 'client.md');
    const api = path.join(outputRoot, 'docs', 'api', 'csharp.md');
    await mkdir(path.dirname(conceptual), { recursive: true });
    await mkdir(path.dirname(api), { recursive: true });
    await writeFile(conceptual, '---\ntitle: Orleans clients\n---\n\nContent\n');
    await writeFile(api, '# API\n');

    assert.equal(await cleanMarkdownOutputDirectory(outputRoot), 1);
    assert.equal(await readFile(conceptual, 'utf8'), '# Orleans clients\n\nContent');
    assert.equal(await readFile(api, 'utf8'), '# API\n');
  });

  test('adds a two-level conceptual overview to llms.txt', async () => {
    const outputRoot = await mkdtemp(path.join(os.tmpdir(), 'orleans-docs-overview-'));
    temporaryDirectories.push(outputRoot);
    const docsIndex = path.join(outputRoot, 'docs.md');
    const grains = path.join(outputRoot, 'docs', 'grains.md');
    const eventSourcing = path.join(outputRoot, 'docs', 'grains', 'event-sourcing.md');
    const details = path.join(outputRoot, 'docs', 'grains', 'event-sourcing', 'details.md');
    const grainIdentity = path.join(outputRoot, 'docs', 'grains', 'grain-identity.md');
    const api = path.join(outputRoot, 'docs', 'api', 'csharp.md');
    await mkdir(path.dirname(grains), { recursive: true });
    await mkdir(path.dirname(eventSourcing), { recursive: true });
    await mkdir(path.dirname(details), { recursive: true });
    await mkdir(path.dirname(api), { recursive: true });
    await writeFile(docsIndex, '# Orleans documentation\n');
    await writeFile(grains, '# Grains\n');
    await writeFile(eventSourcing, '# Event sourcing\n');
    await writeFile(details, '# Event sourcing details\n');
    await writeFile(grainIdentity, '# Grain identity\n');
    await writeFile(api, '# API\n');
    await writeFile(path.join(outputRoot, 'llms.txt'), '# Microsoft Orleans\n');

    assert.equal(
      await publishMarkdownOverview(outputRoot, new URL('https://dotnet.github.io/orleans/')),
      3,
    );
    const llmsText = await readFile(path.join(outputRoot, 'llms.txt'), 'utf8');
    assert.match(llmsText, /## Documentation Overview/);
    assert.match(
      llmsText,
      /- \[Orleans documentation\]\(https:\/\/dotnet\.github\.io\/orleans\/docs\.md\)/,
    );
    assert.match(
      llmsText,
      /  - \[Grains\]\(https:\/\/dotnet\.github\.io\/orleans\/docs\/grains\.md\)/,
    );
    assert.match(
      llmsText,
      /    - \[Event sourcing\]\(https:\/\/dotnet\.github\.io\/orleans\/docs\/grains\/event-sourcing\.md\)/,
    );
    assert.doesNotMatch(llmsText, /Event sourcing details|Grain identity|\[API\]/);
  });
});
