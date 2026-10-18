# Orleans documentation guidance

These rules apply recursively to documentation, snippets, and samples under
`docs/`. The repository-level guidance also applies.

## Code examples

- Put every C# example in a `snippets` project and include it with a named `:::code`
  region. Add hidden declarations outside the region when a displayed fragment
  needs context; don't use inline C# fences.
- Keep snippets minimal, current, and self-contained, including declarations
  required by the displayed region.
- Compile every affected snippet project. Don't publish pseudo-code as if it were a copyable example.
- Documentation and snippet projects target `net10.0`.
- Every `Microsoft.Orleans.*` package reference must use the approved version `10.2.2`. Keep the Orleans package family aligned and centralize versions where the project structure supports it.
- Use an older Orleans package only for a narrow migration example whose purpose
  requires that version. Keep it under `migration` and document the reason in
  `OrleansDocumentationVersionException` in that project.
- Keep direct dependency versions at or above the minimums required by the selected Orleans packages.
- Don't demonstrate an unreleased API using an older package that doesn't contain it. Link to its API reference until a compilable source- or package-based example is available.

## Links

- Orleans documentation links should be relative so they work under `https://dotnet.github.io/orleans`.
- Use fully qualified, locale-neutral canonical external links, such as `https://learn.microsoft.com/azure/...`.
- Don't carry migrated repository `.md` suffixes into published links.
- Verify newly added or changed external links and run the documentation site's link validation.

## API references

- Link public .NET symbols to generated API documentation using DocFX xref syntax instead of formatting the symbol only as inline code.
- Use `<xref:Namespace.Type>` for types and `<xref:Namespace.Type.Member*>` for members or overload groups.
- Add `?displayProperty=nameWithType` when the fully qualified display name improves clarity.
- Use inline code for literals, configuration values, CLI commands, filenames,
  provider names, and syntax that isn't a linkable public symbol. Avoid
  repeatedly linking a symbol when an earlier contextual link is clearer.
- Confirm the xref target exists in the generated API surface before publishing.

Examples:

```markdown
Use <xref:Orleans.Runtime.IPersistentState`1> for persistent grain state.

Configure it with <xref:Orleans.Hosting.AzureTableSiloBuilderExtensions.AddAzureTableGrainStorage*?displayProperty=nameWithType>.
```

## Documentation types

Address relevant operational concerns: correctness, security, availability,
upgrades, capacity, cost, and diagnosis under load.

- **Architecture**: components, protocols, invariants, consistency, failure and
  concurrency semantics, and design trade-offs under scale and version skew.
- **How-to/cookbook**: one task, with prerequisites, pitfalls, and verification.
- **Tutorial**: a working start-to-finish learning path.
- **Conceptual**: the mental model, suitability, trade-offs, and verified constraints.
- **API reference**: semantics, thread-safety, lifetime, exceptions, defaults, and examples.
- **FAQ/troubleshooting**: observed symptoms, causes, remedies, and prevention.

Place updates in the appropriate category and cross-link rather than mix purposes.

## Content

- Keep ordinary conceptual and how-to documentation timeless. Name Orleans releases only in migration or upgrade guidance where the release boundary matters.
- Describe implemented behavior and verified constraints: relevant triggers,
  runtime actions, outcomes, and operator responses. Attribute responsibilities to
  the mechanism that performs them.
- Preserve useful content and authoritative references; update affected contracts
  and behavior. Remove obsolete or redundant material.
- Treat hub pages as overviews: link to peer detail pages instead of singling out one provider or feature for inline configuration guidance.
- Preserve stable URLs and anchors when moving content, or provide an explicit redirect or compatibility anchor.

## Sources and generated output

- Register every new page in `docs/site/src/content/docs/toc.yml` under the
  section that matches its documentation type. A compatibility page retained
  only for stable inbound routes can set `navigation: hidden`; link it to the
  current focused pages.
- Keep recursive includes within the documentation source tree. Missing,
  circular, traversal, absolute, drive-relative, or symlink-escaping includes
  are invalid. Edit the include source and ensure active includes participate in
  link validation.
- Don't hand-edit generated site output, generated API data, dependency folders,
  or build output such as generated `.mdx` siblings, `dist`, `node_modules`,
  `bin`, or `obj`.

## Validation

- Run the parser-backed link, include, redirect, navigation, and project-policy
  checks that cover the changed content.
- Run `docs/site/src/content/docs/validate-snippets.ps1` for changed snippets
  and snippet project policy changes. It builds ordinary snippet projects and
  runs projects marked with `IsTestProject=true`, so executable testing examples
  are validated behaviorally.
- Run `samples/Validate-Samples.ps1` when samples change.
- When `docs/Docs.slnx` is present after integration, build it as the aggregate documentation project.
- From `docs/site`, run `npm run validate`, including redirect and rendered
  output auditing.
- Check `git diff --check` before committing.
