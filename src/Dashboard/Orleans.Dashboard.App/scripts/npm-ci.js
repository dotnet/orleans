import { spawn } from "node:child_process";
import path from "node:path";
import { pathToFileURL } from "node:url";

export const defaultRetryDelays = [5_000, 15_000];

const transientErrorPatterns = [
  /\bECONNRESET\b/i,
  /\bECONNREFUSED\b/i,
  /\bEAI_AGAIN\b/i,
  /\bEHOSTUNREACH\b/i,
  /\bENETUNREACH\b/i,
  /\bETIMEDOUT\b/i,
  /\bERR_SOCKET_TIMEOUT\b/i,
  /\b(?:429|500|502|503|504)\s+(?:Bad Gateway|Gateway Timeout|Internal Server Error|Service Unavailable|Too Many Requests)\b/i,
];

export function isTransientNpmFailure(output) {
  return transientErrorPatterns.some((pattern) => pattern.test(output));
}

function runNpmCommand(args) {
  const isWindows = process.platform === "win32";
  const npmExecutable = isWindows ? process.execPath : "npm";
  const npmArguments = isWindows
    ? [
        process.env.npm_execpath ??
          path.join(
            path.dirname(process.execPath),
            "node_modules",
            "npm",
            "bin",
            "npm-cli.js",
          ),
        "ci",
        ...args,
      ]
    : ["ci", ...args];

  return new Promise((resolve) => {
    let output = "";
    const child = spawn(npmExecutable, npmArguments, {
      stdio: ["inherit", "pipe", "pipe"],
    });

    child.stdout.on("data", (data) => {
      output += data;
      process.stdout.write(data);
    });

    child.stderr.on("data", (data) => {
      output += data;
      process.stderr.write(data);
    });

    child.on("error", (error) => {
      const message = `${error.stack ?? error.message}\n`;
      output += message;
      process.stderr.write(message);
      resolve({ exitCode: 1, output });
    });

    child.on("close", (exitCode) => {
      resolve({ exitCode: exitCode ?? 1, output });
    });
  });
}

function wait(delay) {
  return new Promise((resolve) => setTimeout(resolve, delay));
}

export async function runNpmCi(
  args,
  {
    execute = runNpmCommand,
    delay = wait,
    log = console.error,
    retryDelays = defaultRetryDelays,
  } = {},
) {
  for (let attempt = 0; ; attempt++) {
    const result = await execute(args);
    if (result.exitCode === 0) {
      return 0;
    }

    if (
      attempt >= retryDelays.length ||
      !isTransientNpmFailure(result.output)
    ) {
      return result.exitCode;
    }

    const retryDelay = retryDelays[attempt];
    log(
      `Transient npm failure detected. Retrying npm ci in ${retryDelay / 1_000}s ` +
        `(attempt ${attempt + 2}/${retryDelays.length + 1}).`,
    );
    await delay(retryDelay);
  }
}

const isMain =
  process.argv[1] &&
  pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url;

if (isMain) {
  process.exitCode = await runNpmCi(process.argv.slice(2));
}
