import { describe, expect, test } from 'vitest';
import remarkParse from 'remark-parse';
import { unified } from 'unified';
import { remarkMermaid } from '../src/plugins/remark-mermaid.mjs';

describe('Mermaid Markdown', () => {
  test('converts Mermaid fences into client-rendered diagram containers', async () => {
    const processor = unified().use(remarkParse).use(remarkMermaid);
    const tree = await processor.run(
      processor.parse('```mermaid\nflowchart LR\n  A["one & two"] --> B\n```'),
    );

    expect(tree.children).toHaveLength(1);
    expect(tree.children[0]).toMatchObject({
      type: 'html',
    });
    expect(tree.children[0].value).toContain('class="mermaid-diagram"');
    expect(tree.children[0].value).toContain(
      'data-mermaid-source="flowchart LR\n  A[&quot;one &amp; two&quot;] --&gt; B"',
    );
    expect(tree.children[0].value).not.toContain('language-mermaid');
  });

  test('leaves other code fences unchanged', async () => {
    const processor = unified().use(remarkParse).use(remarkMermaid);
    const tree = await processor.run(
      processor.parse('```csharp\nConsole.WriteLine("Hello");\n```'),
    );

    expect(tree.children[0]).toMatchObject({
      type: 'code',
      lang: 'csharp',
    });
  });
});
