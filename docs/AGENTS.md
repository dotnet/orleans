# Orleans documentation guidance

These rules apply recursively to documentation, snippets, and samples under
`docs/`. The repository-level guidance also applies.

## Code examples

- Put reusable examples in a `snippets` project near the page that consumes them.
- Include snippets with `:::code` and named snippet regions instead of duplicating fenced code in Markdown.
- Keep snippets minimal, complete, and current. Make examples self-contained:
  declare builders, configuration, services, and values used by the displayed
  region.
- Compile every affected snippet project. Don't publish pseudo-code as if it were a copyable example.
- Maintained documentation and snippet projects target `net10.0`.
- Every `Microsoft.Orleans.*` package reference must use the approved version `10.2.2`. Keep the Orleans package family aligned and centralize versions where the project structure supports it.
- Use an older Orleans package only for a narrow migration example whose purpose requires that version, and document the reason next to the exception.
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

## Content

- Keep ordinary conceptual and how-to documentation timeless. Name Orleans releases only in migration or upgrade guidance where the release boundary matters.
- Document implemented behavior and verified limitations. Don't promise planned capabilities.
- Prefer correcting or enhancing useful content over deleting it. Preserve
  authoritative references during rewrites, and remove them only when obsolete,
  redundant, or replaced with a clearer current source.
- Preserve valuable architecture and implementation detail, and keep it distinct from conceptual and task-oriented how-to guidance.
- Treat hub pages as overviews: link to peer detail pages instead of singling out one provider or feature for inline configuration guidance.
- Preserve stable URLs and anchors when moving content, or provide an explicit redirect or compatibility anchor.

## Sources and generated output

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
- Build every changed snippet project with `dotnet build`, and run
  `docs/site/src/content/docs/validate-snippets.ps1` when snippet or project
  policy changes.
- Run `samples/Validate-Samples.ps1` when maintained samples change.
- When `docs/Docs.slnx` is present after integration, build it as the aggregate documentation project.
- From `docs/site`, run `npm run validate`, including redirect and rendered
  output auditing.
- Check `git diff --check` before committing.
