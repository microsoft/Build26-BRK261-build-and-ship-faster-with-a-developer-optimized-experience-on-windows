"""Thin wrapper around the markitdown library.

We intentionally use the narrowest conversion entry points (``convert_stream`` and
``convert_response``) per upstream's security guidance: never pass user-supplied
paths or URIs to the permissive top-level ``convert()`` method.
"""

from __future__ import annotations

import io
import logging
from dataclasses import dataclass
from typing import BinaryIO

from markitdown import (
    FileConversionException,
    MarkItDown,
    StreamInfo,
    UnsupportedFormatException,
)

logger = logging.getLogger("markitdown-service.converter")

_md = MarkItDown(enable_plugins=False)


class UnsupportedFileError(Exception):
    """Raised when no markitdown converter accepts the input."""


class ConversionFailedError(Exception):
    """Raised when a converter matched the input but failed to convert it.

    This is distinct from :class:`UnsupportedFileError`: markitdown *did* find a
    converter that recognized the file, but the conversion itself blew up
    (e.g. ffmpeg crash on a malformed audio header, a corrupt PDF, etc.).
    """


@dataclass
class ConversionResult:
    markdown: str
    title: str | None


def convert_bytes(
    data: bytes,
    *,
    filename: str | None = None,
    content_type: str | None = None,
) -> ConversionResult:
    """Convert an in-memory blob to Markdown."""
    return convert_stream(io.BytesIO(data), filename=filename, content_type=content_type)


def convert_stream(
    stream: BinaryIO,
    *,
    filename: str | None = None,
    content_type: str | None = None,
) -> ConversionResult:
    """Convert a binary stream to Markdown.

    ``filename`` and ``content_type`` are passed as hints to markitdown so it can
    pick the right converter when the magic bytes alone aren't enough.
    """
    stream_info = StreamInfo(
        extension=_extension_from_filename(filename),
        mimetype=content_type,
        filename=filename,
    )
    try:
        result = _md.convert_stream(stream, stream_info=stream_info)
    except UnsupportedFormatException as exc:
        raise UnsupportedFileError(str(exc)) from exc
    except FileConversionException as exc:
        # markitdown wraps the underlying converter exception in its `attempts` list.
        details = _summarize_attempts(exc)
        logger.warning("markitdown conversion failed for %r: %s", filename, details)
        raise ConversionFailedError(details) from exc
    except Exception as exc:  # noqa: BLE001 - we surface this as a clean error
        logger.exception("Unexpected error converting %r", filename)
        raise ConversionFailedError(f"{type(exc).__name__}: {exc}") from exc

    return ConversionResult(
        markdown=result.text_content or "",
        title=getattr(result, "title", None),
    )


def _summarize_attempts(exc: FileConversionException) -> str:
    attempts = getattr(exc, "attempts", None) or []
    if not attempts:
        return str(exc) or "Conversion failed."
    parts = []
    for a in attempts:
        # Each attempt typically has .converter and .exc_info or .exception.
        conv = getattr(a, "converter", None)
        name = getattr(conv, "__class__", type(conv)).__name__ if conv else "Converter"
        underlying = getattr(a, "exception", None) or (
            getattr(a, "exc_info", (None, None, None))[1]
        )
        if underlying is not None:
            parts.append(f"{name} -> {type(underlying).__name__}: {underlying}")
        else:
            parts.append(name)
    return "; ".join(parts)


def _extension_from_filename(filename: str | None) -> str | None:
    if not filename or "." not in filename:
        return None
    ext = filename.rsplit(".", 1)[-1].lower()
    return f".{ext}" if ext else None


SUPPORTED_EXTENSIONS: tuple[str, ...] = (
    "pdf", "docx", "pptx", "xlsx", "xls",
    "html", "htm", "txt", "md", "csv", "json", "xml",
    "epub", "zip",
    "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff",
    "wav", "mp3", "m4a",
    "msg",
)
