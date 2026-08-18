import { unified } from '@astrojs/markdown-remark';
import starlight from '@astrojs/starlight';
import { defineConfig } from 'astro/config';
import remarkDirective from 'remark-directive';
import starlightDotMd from 'starlight-dot-md';
import starlightLinksValidator from 'starlight-links-validator';
import starlightLlmsTxt from 'starlight-llms-txt';
import { createSidebar } from './scripts/lib/docfx.mjs';
import { cleanMarkdownOutput } from './src/plugins/clean-markdown-output.mjs';
import { remarkMermaid } from './src/plugins/remark-mermaid.mjs';
import { remarkVersionZones } from './src/plugins/remark-version-zones.mjs';

const sidebar = await createSidebar(new URL('./src/content/docs/toc.yml', import.meta.url));
const buildConcurrency = Number(process.env.ORLEANS_DOCS_BUILD_CONCURRENCY) || 4;
const markdownPageExample =
  'https://dotnet.github.io/orleans/docs/implementation/streams-implementation.md';
sidebar.unshift({ label: 'Documentation', link: '/docs/' });

export default defineConfig({
  site: 'https://dotnet.github.io/orleans/',
  base: '/orleans/',
  integrations: [
    starlight({
      title: 'Microsoft Orleans',
      description: 'Build scalable, cloud-native distributed applications with .NET and Orleans.',
      logo: {
        src: './src/assets/orleans-logo.png',
        alt: 'Orleans logo',
      },
      favicon: '/favicon.svg',
      customCss: ['./src/styles/custom.css'],
      components: {
        MarkdownContent: './src/components/MarkdownContent.astro',
      },
      editLink: {
        baseUrl: 'https://github.com/dotnet/orleans/edit/main/docs/site/',
      },
      head: [
        {
          tag: 'meta',
          attrs: {
            name: 'theme-color',
            content: '#512bd4',
          },
        },
      ],
      social: [
        {
          icon: 'github',
          label: 'Orleans on GitHub',
          href: 'https://github.com/dotnet/orleans',
        },
        {
          icon: 'discord',
          label: 'Orleans on Discord',
          href: 'https://aka.ms/orleans-discord',
        },
      ],
      sidebar,
      plugins: [
        starlightLinksValidator({
          errorOnRelativeLinks: false,
          exclude: ['/orleans/docs/api/csharp/**'],
        }),
        starlightDotMd({
          includePatterns: ['docs', 'docs/**'],
        }),
        starlightLlmsTxt({
          details:
            'Every documentation page is available as Markdown. Replace the trailing `/` in a page URL with `.md` to retrieve it. ' +
            `For example, https://dotnet.github.io/orleans/docs/implementation/streams-implementation/ becomes ${markdownPageExample}.`,
        }),
      ],
    }),
    cleanMarkdownOutput(),
  ],
  markdown: {
    processor: unified({
      remarkPlugins: [remarkDirective, remarkVersionZones, remarkMermaid],
    }),
  },
  build: {
    concurrency: buildConcurrency,
  },
});
