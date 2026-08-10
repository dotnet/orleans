import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { generateDevSearchIndex } from './lib/dev-search.mjs';

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const entries = await generateDevSearchIndex({
  contentRoot: path.join(siteRoot, 'src', 'content', 'docs'),
  outputRoot: path.join(siteRoot, '.generated', 'pagefind'),
  siteBase: '/orleans/',
});

console.log(`Prepared development search index with ${entries} entries.`);
