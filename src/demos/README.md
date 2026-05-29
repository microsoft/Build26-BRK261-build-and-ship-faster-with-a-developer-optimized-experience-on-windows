# build2026Demo

A collection of self-contained demos for Build 2026. Each subproject is fully
isolated — its own Containerfile (where applicable), its own tests, its own CI job.

## Demos

| Path | What it is | Tech | Status |
| --- | --- | --- | --- |
| [`DemoContainer/`](./DemoContainer) | Drag-drop web UI + REST API container that converts arbitrary documents to Markdown via [microsoft/markitdown](https://github.com/microsoft/markitdown). | FastAPI · Python 3.13 · WSL Containers | ✅ Shipped |
| [`MarkItDownApp/`](./MarkItDownApp) | Native WinUI 3 desktop app wrapping the MarkItDown container — drag-drop files, convert to Markdown, copy/save results. Manages the container lifecycle via the WSL Containers SDK. | WinUI 3 · C# · .NET 8 · WSL Containers | ✅ Shipped |

> Adding a new demo? Create a sibling directory (e.g. `AnotherDemo/`), give it
> its own README, and either (a) add a job to `.github/workflows/ci.yml` with
> `defaults.run.working-directory: AnotherDemo` and a `paths:` filter, or (b)
> add a second workflow file for it. CI is already wired so changes inside
> `DemoContainer/` only trigger that demo's job.

```powershell
# cd to DemoContainer folder
# Build the image (using Containerfile)
wslc build -t markitdown-service .
 
# Run it, mapping container port 8000 -> host 8000
wslc run --rm --name markitdown -p 8000:8000 markitdown-service

# UI: http://localhost:8000 
# Docs: http://localhost:8000/docs

# Stop the container
wslc stop markitdown
```

## Repository conventions

- **One root for shared concerns:** `LICENSE`, `.gitignore`, `.gitattributes`,
  and `.github/` workflows live at the repo root. Everything else (source,
  tests, Containerfiles, fixtures, project READMEs) lives inside the demo folder
  it belongs to.
- **Binary fixtures stay binary** — see [`.gitattributes`](./.gitattributes).
  PDFs, Office files, images, and audio are marked binary so git on Windows
  cannot mangle them with line-ending normalization.
- **CI runs only what changed.** Each demo's workflow has a `paths:` filter so
  edits to one demo never trigger the test/build matrix of another.

## License

MIT — see [`LICENSE`](./LICENSE).
