# Orleans documentation guidance

These rules apply to documentation and samples under `docs/`.

## Code examples

- Put reusable examples in a `snippets` project near the page that consumes them.
- Include snippets with `:::code` and named snippet regions instead of duplicating fenced code in Markdown.
- Make examples self-contained: declare builders, configuration, services, and values used by the displayed region.
- Compile every affected snippet project. Don't publish pseudo-code as if it were a copyable example.
- When adding or revising a package-based snippet project, use the latest stable package versions available on NuGet and keep all `Microsoft.Orleans.*` package versions aligned within the project.
- Keep direct dependency versions at or above the minimums required by the selected Orleans packages.
- Don't demonstrate an unreleased API using an older package that doesn't contain it. Link to its API reference until a compilable source- or package-based example is available.

## Links

- Retain useful authoritative references when rewriting or condensing a page.
- Orleans documentation links should be relative so they work under `https://dotnet.github.io/orleans`.
- External documentation links must be fully qualified. For example, use `https://learn.microsoft.com/en-us/azure/...`, not `/azure/...`.
- Prefer canonical, current documentation over retired or version-specific pages.
- Verify newly added or changed external links and run the documentation site's link validation.

## API references

- Link public .NET symbols to generated API documentation using DocFX xref syntax instead of formatting the symbol only as inline code.
- Use `<xref:Namespace.Type>` for types and `<xref:Namespace.Type.Member*>` for members or overload groups.
- Add `?displayProperty=nameWithType` when the fully qualified display name improves clarity.
- Use inline code for literals, configuration keys, provider names, and syntax that isn't a linkable public symbol.
- Confirm the xref target exists in the generated API surface before publishing.

Examples:

```markdown
Use <xref:Orleans.Runtime.IPersistentState`1> for persistent grain state.

Configure it with <xref:Orleans.Hosting.AzureTableSiloBuilderExtensions.AddAzureTableGrainStorage*?displayProperty=nameWithType>.
```

## Content

- Keep ordinary conceptual and how-to documentation timeless. Name Orleans releases only in migration or upgrade guidance where the release boundary matters.
- Document implemented behavior and verified limitations. Don't promise planned capabilities.
- Treat hub pages as overviews: link to peer detail pages instead of singling out one provider or feature for inline configuration guidance.
- Preserve stable URLs and anchors when moving content, or provide an explicit redirect or compatibility anchor.

## Validation

- Build every changed snippet project with `dotnet build`.
- From `docs/site`, run `npm run validate`.
- Check `git diff --check` before committing.
