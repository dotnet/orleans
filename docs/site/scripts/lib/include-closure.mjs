import { access, readFile, realpath } from 'node:fs/promises';
import path from 'node:path';
import {
  lineOverlapsRanges,
  markdownDirectiveProtectedLineRanges,
} from './markdown-ranges.mjs';

async function pathExists(filePath) {
  try {
    await access(filePath);
    return true;
  } catch {
    return false;
  }
}

function splitFrontmatterWithoutYaml(source) {
  const normalized = source.replace(/^\uFEFF/, '');
  const match = /^---\r?\n[\s\S]*?\r?\n---(?:\r?\n|$)/.exec(normalized);
  return match
    ? {
        body: normalized.slice(match[0].length),
        bodyStartLine: match[0].split(/\r?\n/).length,
      }
    : { body: normalized, bodyStartLine: 1 };
}

export async function collectIncludeTargets(markdownFiles, options = {}) {
  const allowedRoot = options.allowedRoot ? await realpath(path.resolve(options.allowedRoot)) : undefined;
  const onIssue = options.onIssue;
  const onTarget = options.onTarget;
  const splitFrontmatter = options.splitFrontmatter ?? splitFrontmatterWithoutYaml;
  const targets = new Set();
  const visitedContexts = new Set();

  function report(file, line, message) {
    if (onIssue) {
      onIssue({ file, line, message });
      return;
    }
    throw new Error(`${message} (${file}:${line})`);
  }

  function isWithinAllowedRoot(filePath) {
    if (!allowedRoot) {
      return true;
    }
    const relative = path.relative(allowedRoot, filePath);
    return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
  }

  async function collect(filePath, stack = []) {
    const logicalPath = path.resolve(filePath);
    const resolvedPath = await realpath(logicalPath);
    const context = `${resolvedPath}\0${path.dirname(logicalPath)}`;
    if (visitedContexts.has(context)) {
      return;
    }
    visitedContexts.add(context);

    const source = await readFile(logicalPath, 'utf8');
    const { body, bodyStartLine } = splitFrontmatter(source);
    const lines = body.replaceAll('\r\n', '\n').split('\n');
    const protectedLineRanges = body.includes('[!INCLUDE')
      ? markdownDirectiveProtectedLineRanges(body)
      : [];
    for (let index = 0; index < lines.length; index += 1) {
      const line = lines[index];
      if (lineOverlapsRanges(index + 1, protectedLineRanges)) {
        continue;
      }
      const match = /^\s*\[!INCLUDE\s+\[[^\]]+\]\(([^)]+)\)\]\s*$/.exec(line);
      if (!match) {
        if (line.includes('[!INCLUDE')) {
          report(resolvedPath, bodyStartLine + index, `Unsupported INCLUDE syntax: ${line.trim()}`);
        }
        continue;
      }

      const reference = match[1];
      if (
        reference.length === 0 ||
        reference.includes('\0') ||
        reference.includes('\\') ||
        reference.split('/').some((segment) => segment.includes(':')) ||
        path.isAbsolute(reference) ||
        path.posix.isAbsolute(reference) ||
        path.win32.isAbsolute(reference) ||
        /^[a-z][a-z\d+.-]*:/i.test(reference)
      ) {
        report(resolvedPath, bodyStartLine + index, `Unsafe INCLUDE path '${reference}'.`);
        continue;
      }

      const requestedTarget = path.resolve(path.dirname(logicalPath), reference);
      if (!isWithinAllowedRoot(requestedTarget)) {
        report(
          resolvedPath,
          bodyStartLine + index,
          `INCLUDE '${match[1]}' resolves outside the allowed documentation tree '${allowedRoot}'.`,
        );
        continue;
      }
      if (!(await pathExists(requestedTarget))) {
        report(
          resolvedPath,
          bodyStartLine + index,
          `INCLUDE '${match[1]}' does not exist (${requestedTarget}).`,
        );
        continue;
      }
      const target = await realpath(requestedTarget);
      if (!isWithinAllowedRoot(target)) {
        report(
          resolvedPath,
          bodyStartLine + index,
          `INCLUDE '${match[1]}' resolves outside the allowed documentation tree '${allowedRoot}' through a link.`,
        );
        continue;
      }
      if (stack.includes(target) || target === resolvedPath) {
        report(
          resolvedPath,
          bodyStartLine + index,
          `Circular INCLUDE detected: ${[...stack, resolvedPath, target].join(' -> ')}`,
        );
        continue;
      }
      onTarget?.({
        path: requestedTarget,
        physicalPath: target,
        sourcePath: logicalPath,
        sourcePhysicalPath: resolvedPath,
        line: bodyStartLine + index,
      });
      targets.add(target);
      await collect(requestedTarget, [...stack, resolvedPath]);
    }
  }

  for (const file of markdownFiles) {
    await collect(file);
  }
  return targets;
}
