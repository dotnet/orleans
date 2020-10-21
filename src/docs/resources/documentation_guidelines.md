---
layout: page
title: Documentation Guidelines
---

# Documentation Guidelines

The Orleans documentation is built in [Markdown](https://help.github.com/articles/markdown-basics/).
We use a few simple conventions to ensure a homogeneous style throughout the full set of documents.

These standards are being introduced.
If you have issues with these guidelines then raise an issue or a Pull Request.
If you find documentation that fails to meet the guidelines, then make a fix and submit a pull request. Also if you are using windows 10 you can go to the store and find free MarkDown editors like [this](https://www.microsoft.com/store/apps/9wzdncrdd2p3)

# Structure

## Language

The documentation will follow US-English spelling.
Desktop tools like http://markdownpad.com and [Visual Studio Code](https://code.visualstudio.com/) have spell checking features.

## Paragraph structure

Each sentence should be written on a single line, and only one sentence per line.
This makes merging changes easier and helps identify verbose language.
Paragraphs in Markdown are just one or more lines of consecutive text followed by one or more blank lines.

## Headings

Headings should be used to structure a document.
Avoid using other emphasis features like ALLCAPS, *Italics* or **bold** to identify a new topic.
Using a header is not only more consistent, but also allows linking to the header.

## Footers

At the end of a page, it is helpful to link to the next logical page in the documentation.
If the page is the last in a sub-section, then linking back to the index page is useful.

# Styles

## Code formatting

Blocks of example code should be formatted with the triple back tick format followed by the language.

``` csharp
    [StorageProvider(ProviderName="store1")]
    public class MyGrain<IMyGrainState>
    {
      ...
    }
```

Which will render as

``` csharp

[StorageProvider(ProviderName="store1")]
public class MyGrain<IMyGrainState> ...
{
  ...
}
```

Inline code should be marked with a single backtick (\`).

This include references to:

 * type names e.g. `Task<T>`
 * variable names e.g. `game`
 * namespaces e.g. `Orleans.Storage.AzureTableStorage`

If showing text that is an output (e.g. text file content or console output) you can either use the triple back tick without specifying a language or you can indent the content. For example:

    1 said: Welcome to my team!
    0 said: Thanks!
    1 said: Thanks!
    0 said: Thanks!

## File names and paths

When referencing a filename, directory/folder or URI then use standard italics to format.
This can be done by surrounding the string with either with a single asterisk (`*`) or a single underscore (`_`)

Examples:

* *OrleansRuntimeInterfaces.dll*
* *C:\Binaries*
* *../src/Grain.cs*

## Tables

Markdown supports [tabular data](https://help.github.com/articles/github-flavored-markdown/#tables).
Tables could be used to structure data so that is is easily consumable for the reader.

Suffix |     Unit
-------|-------------
ms     | millisecond(s)
s      | second(s)
m      | minute(s)

## Links

When referencing another concept, provide a link to that concept.
Forward and backward references within a page can be linked via the header. e.g. link back to [Structure](#structure)
Links to other documents can either link to the page or to a sub-section/header within the page.
External links should be exposed as the full link. e.g. https://github.com/dotnet/roslyn

# Contribution

The Orleans documentation is managed as Markdown files in a Git repository hosted on [GitHub in the gh-pages branch](https://github.com/dotnet/orleans/tree/docs).
See the [GitHub Pages](https://pages.github.com/) documentation on how to use the `gh-pages` branch convention for "Project site" documents.
