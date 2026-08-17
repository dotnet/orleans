# Orleans documentation guidance

These rules apply recursively to documentation, snippets, and samples under
`docs/`. The repository-level guidance also applies.

## Code examples

- Put every C# example in a `snippets` project and include it with a named `:::code`
  region. Add hidden declarations outside the region when a displayed fragment
  needs context; don't use inline C# fences.
- Include snippets with `:::code` and named snippet regions instead of duplicating fenced code in Markdown.
- Keep snippets minimal, complete, and current. Make examples self-contained:
  declare builders, configuration, services, and values used by the displayed
  region.
- Compile every affected snippet project. Don't publish pseudo-code as if it were a copyable example.
- Maintained documentation and snippet projects target `net10.0`.
- Every `Microsoft.Orleans.*` package reference must use the approved version `10.2.2`. Keep the Orleans package family aligned and centralize versions where the project structure supports it.
- Use an older Orleans package only for a narrow migration example whose purpose
  requires that version. Keep it under `migration` and document the reason in
  `OrleansDocumentationVersionException` in that project.
- Keep direct dependency versions at or above the minimums required by the selected Orleans packages.
- Don't demonstrate an unreleased API using an older package that doesn't contain it. Link to its API reference until a compilable source- or package-based example is available.

## Links

- Retain useful authoritative references when rewriting or condensing a page.
- Orleans documentation links should be relative so they work under `https://dotnet.github.io/orleans`.
- External documentation links must be fully qualified. For example, use the locale-neutral canonical form `https://learn.microsoft.com/azure/...`, not `/azure/...` or a hard-coded locale such as `/en-us/`.
- Don't carry migrated repository `.md` suffixes into published links.
- Never use a root-relative path for an external site.
- Prefer canonical, current documentation over retired or version-specific pages.
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

Documentation and samples are a primary deliverable in this repository. They
must address the concerns involved in operating secure, high-scale systems:
correctness, security, availability, upgrades, capacity, cost, and diagnosis of
failures under load. The repository doesn't have complete coverage of every
category below yet; don't treat existing gaps as precedent.

Each of the following serves a distinct purpose, and none substitutes for
another:

- **Architecture and implementation detail** — how Orleans actually works
  internally: components and their responsibilities, protocols and message
  flows, state machines, invariants, consistency and failure semantics,
  concurrency and threading model, and the reasoning behind design decisions
  along with their trade-offs. This is what operators and contributors need to
  reason about behavior under failure, scale, and version skew, and to predict
  what a change will do in production.
- **Cookbook and how-to guides** — focused, task-oriented recipes that address
  one goal ("configure ADO.NET clustering", "roll a cluster without
  downtime", "secure silo-to-silo traffic"). They assume the reader has a goal
  and want the shortest correct path, including prerequisites, pitfalls, and
  verification steps.
- **Walkthroughs and tutorials** — guided, start-to-finish learning paths that
  build something working from an empty directory. They optimize for a first
  successful outcome and teach concepts in the order the reader needs them.
- **Conceptual documentation** — explains the ideas and the model: grains,
  activations, placement, persistence, streaming, and when and why to use each.
  It builds the mental model that makes the other categories comprehensible, and
  it should be honest about what Orleans is not suited for.
- **API reference** — accurate, complete, and example-bearing documentation of
  the public surface, including semantics, thread-safety, lifetime, exceptions,
  and defaults. Reference pages should link out to the conceptual and how-to
  content that gives them context.
- **FAQ and troubleshooting** — commonly asked questions and encountered
  failures, organized by observed symptom (exception, log message, metric, or
  behavior) with root cause, remedy, and prevention. This documentation should
  support diagnosis during production incidents.

Prefer adding a missing category over repeatedly expanding an existing page.
Keep the categories distinct, and cross-link between them instead of blending a
tutorial into a reference or burying architecture detail inside a how-to.

## Content

- Keep ordinary conceptual and how-to documentation timeless. Name Orleans releases only in migration or upgrade guidance where the release boundary matters.
- Document implemented behavior and verified limitations. Don't promise planned capabilities.
- Rewrite guidance around affirmative runtime behavior and outcomes. Describe relevant triggers, runtime actions, resulting states, and operator responses when those details help readers understand or operate the feature.
- Assign each responsibility to the mechanism which performs it. For example, an autoscaler changes cluster capacity, placement selects an activation host, and a rebalancer migrates activations.
- State what a feature is and does. Remove obvious statements and descriptions framed around what the feature isn't, doesn't do, or doesn't replace.
- Prefer correcting or enhancing useful content over deleting it. Preserve
  authoritative references during rewrites, and remove them only when obsolete,
  redundant, or replaced with a clearer current source.
- Preserve and expand architecture and implementation detail, and keep it distinct from conceptual and task-oriented how-to guidance.
- Treat hub pages as overviews: link to peer detail pages instead of singling out one provider or feature for inline configuration guidance.
- Preserve stable URLs and anchors when moving content, or provide an explicit redirect or compatibility anchor.

## Sources and generated output

- Register every new page in `docs/site/src/content/docs/toc.yml` under the
  section that matches its documentation type. An unlisted page is effectively
  unpublished.
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
- Run `samples/Validate-Samples.ps1` when maintained samples change.
- When `docs/Docs.slnx` is present after integration, build it as the aggregate documentation project.
- From `docs/site`, run `npm run validate`, including redirect and rendered
  output auditing.
- Check `git diff --check` before committing.
