# build2026Demo — MarkItDown Service

A single WSL container that ships two things on top of
[microsoft/markitdown](https://github.com/microsoft/markitdown):

1. **A drag‑and‑drop web UI** at `/` — drop a file, see the Markdown on the left and a rendered
   preview on the right.
2. **A small REST API** under `/api/v1` — POST a file from a native app (WinUI / .NET / Swift /
   anything that can do `multipart/form-data`) and get Markdown back.

OpenAPI/Swagger UI is auto‑generated at **`/docs`** so you can generate a typed client (NSwag,
kiota, OpenAPI Generator, …) in one command.

> ⚠️ **No auth.** This is a demo. Run on `localhost` or behind a reverse proxy/auth layer in any
> environment you don't fully control. See *Security* below.

---

## Quick start

```bash
wslc compose up --build
```

Then open <http://localhost:8000> for the UI, or <http://localhost:8000/docs> for the API docs.

Or with plain wslc:

```bash
wslc build -t markitdown-service .
wslc run --rm -p 8000:8000 markitdown-service
```

### Local dev (no container)

```bash
python -m venv .venv
source .venv/bin/activate     # Windows: .venv\Scripts\Activate.ps1
pip install -e ".[dev]"
uvicorn app.main:app --reload
```

Run tests:

```bash
pytest -q
```

---

## API

All endpoints live under `/api/v1`. Full schema at `/openapi.json` and `/docs`.

| Method | Path | Body | Response | Purpose |
| --- | --- | --- | --- | --- |
| `GET` | `/api/v1/health` | — | `{"status":"ok","version":"…"}` | Liveness probe |
| `GET` | `/api/v1/formats` | — | `{"extensions":["pdf","docx",…]}` | Supported extensions |
| `POST` | `/api/v1/convert` | `multipart/form-data` field `file` | JSON `{markdown, filename, content_type, bytes, duration_ms, title}` | **Primary** — JSON envelope for native apps |
| `POST` | `/api/v1/convert/raw` | `multipart/form-data` field `file` | `text/markdown` | Plain Markdown body for shell pipelines |
| `POST` | `/api/v1/convert/url` | `{"url":"https://…"}` | same as `/convert` | Opt‑in (see env vars). Fetches the URL server‑side. |

### Errors

| Code | Meaning |
| --- | --- |
| `400` / `422` | `file` field missing |
| `413` | Upload exceeds `MARKITDOWN_MAX_UPLOAD_MB` |
| `415` | No markitdown converter handles the file |
| `403` | URL fetch disabled and `/convert/url` was called |
| `5xx` | `{"error":"…","detail":"…","trace_id":"…"}` — `trace_id` echoes the `X-Request-ID` header |

### Examples

**curl** — JSON:

```bash
curl -F "file=@./report.pdf" http://localhost:8000/api/v1/convert | jq .markdown
```

**curl** — raw Markdown:

```bash
curl -F "file=@./report.pdf" http://localhost:8000/api/v1/convert/raw > report.md
```

**C# / .NET (typical native‑app shape):**

```csharp
using var client = new HttpClient();
using var form = new MultipartFormDataContent();
using var fs = File.OpenRead("report.pdf");
form.Add(new StreamContent(fs), "file", "report.pdf");

var resp = await client.PostAsync("http://localhost:8000/api/v1/convert", form);
resp.EnsureSuccessStatusCode();
var result = await resp.Content.ReadFromJsonAsync<ConvertResponse>();
Console.WriteLine(result!.Markdown);

record ConvertResponse(string Markdown, string Filename, int Bytes, int DurationMs);
```

**Python:**

```python
import requests

with open("report.pdf", "rb") as f:
    r = requests.post(
        "http://localhost:8000/api/v1/convert",
        files={"file": ("report.pdf", f, "application/pdf")},
    )
r.raise_for_status()
print(r.json()["markdown"])
```

---

## Configuration

All settings come from `MARKITDOWN_*` environment variables.

| Variable | Default | Description |
| --- | --- | --- |
| `MARKITDOWN_HOST` | `0.0.0.0` | Bind host |
| `MARKITDOWN_PORT` | `8000` | Bind port |
| `MARKITDOWN_MAX_UPLOAD_MB` | `100` | Max request body size for uploads |
| `MARKITDOWN_CORS_ORIGINS` | `*` | Comma‑separated CORS allowlist (`*` = any) |
| `MARKITDOWN_ALLOW_URL_FETCH` | `false` | Enable `POST /api/v1/convert/url` |
| `MARKITDOWN_URL_FETCH_TIMEOUT_SECONDS` | `15` | Timeout for URL fetch |
| `MARKITDOWN_URL_FETCH_MAX_MB` | `25` | Max bytes pulled from a remote URL |
| `MARKITDOWN_REQUEST_ID_HEADER` | `X-Request-ID` | Header used for request correlation |

---

## Supported file types

Inherits whatever the installed `markitdown[all]` build supports — PDF, DOCX, PPTX, XLSX, XLS,
HTML, plain text, CSV/JSON/XML, EPUB, ZIP (recurses), images (with EXIF), audio (with EXIF), and
more. Hit `/api/v1/formats` for the runtime list, or see the upstream
[markitdown README](https://github.com/microsoft/markitdown).

This image deliberately ships **without** LLM features (no OpenAI key, no `markitdown-ocr`, no
Azure Document Intelligence) to keep the image small and zero‑config. Add them later by extending
the Containerfile.

---

## Security

`markitdown` performs I/O with the process's privileges. We mitigate by:

- Using `convert_stream()` exclusively for uploaded files — never the permissive `convert()`
  entry point that accepts arbitrary paths/URIs.
- Disabling the URL‑fetch endpoint by default (`MARKITDOWN_ALLOW_URL_FETCH=false`).
- Enforcing an upload size limit (`MARKITDOWN_MAX_UPLOAD_MB`).
- Running the container as a non‑root user (`appuser`, uid 10001).

The server does **not** implement authentication. For anything beyond localhost, put it behind a
reverse proxy that handles authn/authz (Caddy, nginx, Cloudflare, an APIM, …).

---

## Project layout

```
build2026Demo/
├── Containerfile            # multi-stage build
├── docker-compose.yml
├── pyproject.toml
├── app/
│   ├── main.py              # FastAPI app + routes + static mount
│   ├── converter.py         # markitdown wrapper (convert_stream)
│   ├── config.py            # pydantic-settings
│   └── static/              # index.html + app.js + styles.css (drag-drop UI)
├── tests/                   # pytest + FastAPI TestClient
│   └── fixtures/            # tiny .txt / .html / .csv samples
└── .github/workflows/ci.yml # ruff + pytest + wslc build smoke
```

## License

MIT — see [`LICENSE`](./LICENSE).
