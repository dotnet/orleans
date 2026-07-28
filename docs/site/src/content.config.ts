import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { z } from 'astro/zod';
import { docsSchema } from '@astrojs/starlight/schema';

export const collections = {
  docs: defineCollection({
    loader: glob({
      base: './src/content/docs',
      pattern: '**/*.mdx',
    }),
    schema: docsSchema({
      extend: z.object({
        docfx: z.record(z.string(), z.unknown()).optional(),
      }),
    }),
  }),
};
