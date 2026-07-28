import { unified } from '@astrojs/markdown-remark';
import starlight from '@astrojs/starlight';
import { defineConfig } from 'astro/config';
import remarkDirective from 'remark-directive';
import starlightLinksValidator from 'starlight-links-validator';
import starlightLlmsTxt from 'starlight-llms-txt';
import { createSidebar } from './scripts/lib/docfx.mjs';
import { remarkVersionZones } from './src/plugins/remark-version-zones.mjs';

const sidebar = await createSidebar(new URL('./src/content/docs/toc.yml', import.meta.url));
sidebar.splice(
  2,
  0,
  { label: 'Samples gallery', link: '/samples/' },
  { label: 'API reference', link: '/api/' },
);

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
      editLink: {
        baseUrl: 'https://github.com/dotnet/docs/edit/main/docs/orleans/',
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
        }),
        starlightLlmsTxt(),
      ],
    }),
  ],
  markdown: {
    processor: unified({
      remarkPlugins: [remarkDirective, remarkVersionZones],
    }),
  },
});
