import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, test } from 'vitest';
import {
  cleanMarkdownOutputDirectory,
  cleanPublishedMarkdown,
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

{/* Source: snippets/HostingExamples.cs; region: local_silo_and_client */}
\`\`\`csharp
builder.UseOrleans();
\`\`\`
`;

    const markdown = cleanPublishedMarkdown(source);

    assert.match(markdown, /^# Orleans clients/m);
    assert.match(markdown, /builder\.UseOrleans\(\);/);
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
});
