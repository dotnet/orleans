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
  packages: defineCollection({
    loader: glob({
      base: './src/data/pkgs',
      pattern: '**/*.json',
    }),
    schema: z
      .object({
        $schema: z.string().optional(),
        schemaVersion: z.string().optional(),
        package: z.object({
          name: z.string(),
          version: z.string(),
          targetFramework: z.string(),
          sourceRepository: z.string().optional(),
          sourceCommit: z.string().optional(),
        }),
        apiHash: z.string().optional(),
        types: z.array(z.unknown()),
      })
      .loose(),
  }),
};
