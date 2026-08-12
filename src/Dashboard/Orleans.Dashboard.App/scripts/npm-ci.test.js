import assert from "node:assert/strict";
import test from "node:test";

import { isTransientNpmFailure, runNpmCi } from "./npm-ci.js";

test("classifies transient npm failures", () => {
  assert.equal(isTransientNpmFailure("npm error code ECONNRESET"), true);
  assert.equal(
    isTransientNpmFailure("npm error 503 Service Unavailable"),
    true,
  );
  assert.equal(isTransientNpmFailure("npm error code EUSAGE"), false);
});

test("retries a transient failure with backoff", async () => {
  const results = [
    { exitCode: 1, output: "npm error code ECONNRESET" },
    { exitCode: 0, output: "added 100 packages" },
  ];
  const delays = [];
  const messages = [];

  const exitCode = await runNpmCi([], {
    execute: async () => results.shift(),
    delay: async (value) => delays.push(value),
    log: (message) => messages.push(message),
  });

  assert.equal(exitCode, 0);
  assert.deepEqual(delays, [5_000]);
  assert.match(messages[0], /attempt 2\/3/);
});

test("does not retry a deterministic failure", async () => {
  let attempts = 0;

  const exitCode = await runNpmCi([], {
    execute: async () => {
      attempts++;
      return { exitCode: 17, output: "npm error code EUSAGE" };
    },
  });

  assert.equal(exitCode, 17);
  assert.equal(attempts, 1);
});

test("preserves the final exit code after exhausting retries", async () => {
  const results = [
    { exitCode: 1, output: "npm error code ECONNRESET" },
    { exitCode: 2, output: "npm error code ETIMEDOUT" },
    { exitCode: 23, output: "npm error code EAI_AGAIN" },
  ];
  const delays = [];

  const exitCode = await runNpmCi([], {
    execute: async () => results.shift(),
    delay: async (value) => delays.push(value),
    log: () => {},
  });

  assert.equal(exitCode, 23);
  assert.deepEqual(delays, [5_000, 15_000]);
});
