# Orleans Documentation

## Building the Documentation

To build the documentation you will need a copy of [DocFX](https://dotnet.github.io/docfx/) on your path.

If you're on Linux/OSX you can run DocFX with Mono.

To build the documentation, run:

```
> docfx src\docfx.json
```

To view the documentation:

```
> docfx serve
```

You'll then be able to browse the website on [http://localhost:8080](http://localhost:8080)

## Editing the Documentation

Please edit the markdown files in the `src\` directory.

The `toc.yml` files are used to create the menus. Any new markdown files you add will need to be included in the appropriate `toc.yml` file. This may include the `toc.yml` file at the same level in the directory structure, and possibly in the parent directory too.

All files (both markdown and html) are added to git.
