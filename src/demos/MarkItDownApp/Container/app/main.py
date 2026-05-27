"""FastAPI application: REST API + drag-drop web UI."""

from __future__ import annotations

import logging
import time
import uuid
from pathlib import Path
from typing import Annotated

from fastapi import FastAPI, File, HTTPException, Request, UploadFile, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, PlainTextResponse, Response
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field, HttpUrl

from . import __version__
from .config import settings
from .converter import (
    SUPPORTED_EXTENSIONS,
    ConversionFailedError,
    ConversionResult,
    UnsupportedFileError,
    convert_bytes,
)

logger = logging.getLogger("markitdown-service")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")

app = FastAPI(
    title="MarkItDown Service",
    description=(
        "Convert documents (PDF, Office, images, HTML, audio, etc.) to Markdown via "
        "[microsoft/markitdown](https://github.com/microsoft/markitdown).\n\n"
        "Drag-drop UI at `/`. REST API under `/api/v1`. OpenAPI spec at `/openapi.json`."
    ),
    version=__version__,
    docs_url="/docs",
    redoc_url="/redoc",
)

if settings.cors_origin_list:
    app.add_middleware(
        CORSMiddleware,
        allow_origins=settings.cors_origin_list,
        allow_credentials=False,
        allow_methods=["GET", "POST", "OPTIONS"],
        allow_headers=["*"],
        expose_headers=[settings.request_id_header],
    )


@app.middleware("http")
async def request_id_middleware(request: Request, call_next):
    rid = request.headers.get(settings.request_id_header) or uuid.uuid4().hex[:12]
    request.state.request_id = rid
    response = await call_next(request)
    response.headers[settings.request_id_header] = rid
    return response


# -------------------- Models --------------------


class HealthResponse(BaseModel):
    status: str = "ok"
    version: str


class FormatsResponse(BaseModel):
    extensions: list[str]


class ConvertResponse(BaseModel):
    markdown: str = Field(description="The converted Markdown text.")
    filename: str | None = Field(default=None, description="Filename echoed back from the upload.")
    content_type: str | None = Field(
        default=None, description="Content-Type as reported by the client."
    )
    bytes: int = Field(description="Size of the uploaded payload in bytes.")
    duration_ms: int = Field(description="Server-side conversion time in milliseconds.")
    title: str | None = Field(default=None, description="Document title if detected.")


class ConvertUrlRequest(BaseModel):
    url: HttpUrl


class ErrorResponse(BaseModel):
    error: str
    detail: str | None = None
    trace_id: str | None = None


# -------------------- Helpers --------------------


def _trace_id(request: Request) -> str:
    return getattr(request.state, "request_id", "")


def _error(request: Request, code: int, error: str, detail: str | None = None) -> JSONResponse:
    return JSONResponse(
        status_code=code,
        content=ErrorResponse(error=error, detail=detail, trace_id=_trace_id(request)).model_dump(),
    )


async def _read_upload(request: Request, file: UploadFile) -> bytes:
    """Read an UploadFile but enforce the configured max size."""
    limit = settings.max_upload_bytes
    chunks: list[bytes] = []
    total = 0
    while True:
        chunk = await file.read(1024 * 1024)
        if not chunk:
            break
        total += len(chunk)
        if total > limit:
            raise HTTPException(
                status_code=status.HTTP_413_CONTENT_TOO_LARGE,
                detail=f"Upload exceeds {settings.max_upload_mb} MB limit.",
            )
        chunks.append(chunk)
    return b"".join(chunks)


def _do_convert(data: bytes, file: UploadFile) -> tuple[ConversionResult, int]:
    start = time.perf_counter()
    try:
        result = convert_bytes(
            data,
            filename=file.filename,
            content_type=file.content_type,
        )
    except UnsupportedFileError as exc:
        raise HTTPException(
            status_code=status.HTTP_415_UNSUPPORTED_MEDIA_TYPE,
            detail=f"Unsupported file: {exc}",
        ) from exc
    except ConversionFailedError as exc:
        # markitdown matched a converter but the converter blew up — typically
        # malformed/corrupt input or a flaky upstream converter. Treat as 422
        # so clients can distinguish "I don't handle this type" (415) from
        # "I tried, but the file is broken" (422).
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail=f"Conversion failed: {exc}",
        ) from exc
    duration_ms = int((time.perf_counter() - start) * 1000)
    return result, duration_ms


# -------------------- API routes --------------------


@app.get("/api/v1/health", response_model=HealthResponse, tags=["meta"])
async def health() -> HealthResponse:
    return HealthResponse(status="ok", version=__version__)


@app.get("/api/v1/formats", response_model=FormatsResponse, tags=["meta"])
async def formats() -> FormatsResponse:
    return FormatsResponse(extensions=list(SUPPORTED_EXTENSIONS))


@app.post(
    "/api/v1/convert",
    response_model=ConvertResponse,
    tags=["convert"],
    summary="Convert an uploaded file to Markdown (JSON envelope).",
    responses={
        400: {"model": ErrorResponse, "description": "Missing or invalid file field."},
        413: {"model": ErrorResponse, "description": "Upload exceeds size limit."},
        415: {"model": ErrorResponse, "description": "Unsupported file format."},
        422: {"model": ErrorResponse, "description": "Converter matched, but conversion failed (e.g. corrupt file)."},
    },
)
async def convert(
    request: Request,
    file: Annotated[UploadFile, File(description="The file to convert.")],
) -> ConvertResponse:
    if file is None or not file.filename:
        raise HTTPException(status_code=400, detail="Missing 'file' field.")
    data = await _read_upload(request, file)
    result, duration_ms = _do_convert(data, file)
    return ConvertResponse(
        markdown=result.markdown,
        filename=file.filename,
        content_type=file.content_type,
        bytes=len(data),
        duration_ms=duration_ms,
        title=result.title,
    )


@app.post(
    "/api/v1/convert/raw",
    response_class=PlainTextResponse,
    tags=["convert"],
    summary="Convert an uploaded file to Markdown (raw text/markdown response).",
    responses={
        200: {
            "content": {"text/markdown": {"schema": {"type": "string"}}},
            "description": "Markdown text.",
        },
        400: {"model": ErrorResponse},
        413: {"model": ErrorResponse},
        415: {"model": ErrorResponse},
        422: {"model": ErrorResponse},
    },
)
async def convert_raw(
    request: Request,
    file: Annotated[UploadFile, File(description="The file to convert.")],
) -> Response:
    if file is None or not file.filename:
        raise HTTPException(status_code=400, detail="Missing 'file' field.")
    data = await _read_upload(request, file)
    result, _ = _do_convert(data, file)
    return Response(content=result.markdown, media_type="text/markdown; charset=utf-8")


@app.post(
    "/api/v1/convert/url",
    response_model=ConvertResponse,
    tags=["convert"],
    summary="Fetch a URL and convert its body to Markdown (disabled by default).",
)
async def convert_url(request: Request, body: ConvertUrlRequest) -> ConvertResponse:
    if not settings.allow_url_fetch:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="URL fetch is disabled. Set MARKITDOWN_ALLOW_URL_FETCH=true to enable.",
        )
    import requests

    start = time.perf_counter()
    try:
        response = requests.get(
            str(body.url),
            timeout=settings.url_fetch_timeout_seconds,
            stream=True,
        )
        response.raise_for_status()
        total = 0
        limit = settings.url_fetch_max_mb * 1024 * 1024
        chunks: list[bytes] = []
        for chunk in response.iter_content(chunk_size=1024 * 1024):
            total += len(chunk)
            if total > limit:
                raise HTTPException(
                    status_code=413,
                    detail=f"Remote content exceeds {settings.url_fetch_max_mb} MB limit.",
                )
            chunks.append(chunk)
        data = b"".join(chunks)
    except requests.RequestException as exc:
        raise HTTPException(status_code=502, detail=f"Fetch failed: {exc}") from exc

    fake = UploadFile(filename=str(body.url).rsplit("/", 1)[-1] or "download", file=None)  # type: ignore[arg-type]
    fake.content_type = response.headers.get("content-type", "").split(";")[0].strip() or None
    try:
        from .converter import convert_bytes as _cb

        result = _cb(data, filename=fake.filename, content_type=fake.content_type)
    except UnsupportedFileError as exc:
        raise HTTPException(status_code=415, detail=f"Unsupported file: {exc}") from exc

    return ConvertResponse(
        markdown=result.markdown,
        filename=fake.filename,
        content_type=fake.content_type,
        bytes=len(data),
        duration_ms=int((time.perf_counter() - start) * 1000),
        title=result.title,
    )


# -------------------- Static UI --------------------

_STATIC_DIR = Path(__file__).parent / "static"
app.mount("/static", StaticFiles(directory=_STATIC_DIR), name="static")


@app.get("/", include_in_schema=False)
async def root() -> Response:
    index = _STATIC_DIR / "index.html"
    return Response(content=index.read_text(encoding="utf-8"), media_type="text/html; charset=utf-8")
