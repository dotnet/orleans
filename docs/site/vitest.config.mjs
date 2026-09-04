import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // Give repository-wide parsers and process-backed policy checks a deterministic worker budget.
    fileParallelism: false,
  },
});
