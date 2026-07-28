import type { APIRoute } from 'astro';
import { getPackages } from '../../../lib/api/collection';
import { renderApiIndexMarkdown, markdownResponse } from '../../../lib/api/markdown';

export const prerender = true;

export const GET: APIRoute = async () => {
  const packages = (await getPackages()).map((entry) => entry.data);
  return markdownResponse(renderApiIndexMarkdown(packages, import.meta.env.BASE_URL));
};
