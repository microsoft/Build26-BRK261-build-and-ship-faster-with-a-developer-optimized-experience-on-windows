"""End-to-end sample-file tests.

These exercise the real `/api/v1/convert` endpoint with every file in
``tests/samples/``. Each entry encodes the expected HTTP status and (optionally)
a substring the converted Markdown must contain.

Files that need optional system dependencies (FLAC for audio transcription) or
features deliberately not enabled in this build (OCR / LLM image description) are
listed in :data:`OPTIONAL_EXPECTATIONS` and accept either a successful conversion
or a documented 4xx error.
"""

from __future__ import annotations

import re
import shutil
from pathlib import Path

import pytest

SAMPLES_DIR = Path(__file__).parent / "samples"

# (filename, expected_status, substring that must appear in markdown)
HARD_EXPECTATIONS: list[tuple[str, int, str]] = [
    ("test.docx", 200, "AutoGen"),
    ("test.pdf", 200, "Large language models"),
    ("test.xlsx", 200, "Sheet1"),
    ("test.xls", 200, "Sheet1"),
    ("test.pptx", 200, "AutoGen"),
    ("test_blog.html", 200, "AutoGen"),
    ("test_rss.xml", 200, "Microsoft"),
    ("test.epub", 200, "Test EPUB"),
    ("test.json", 200, "key1"),
    ("test_mskanji.csv", 200, "|"),  # any markdown-table cell
    ("test.jpg", 200, "ImageSize"),
    ("test_llm.jpg", 200, "ImageSize"),
    ("test_outlook_msg.msg", 200, "From:"),
    ("test_files.zip", 200, "Content from the zip file"),
    ("REPAIR-2022-INV-001_multipage.pdf", 200, "ZAVA"),
    ("RECEIPT-2024-TXN-98765_retail_purchase.pdf", 200, "TECHMART"),
    ("SPARSE-2024-INV-1234_borderless_table.pdf", 200, "INVENTORY"),
]


# Files whose pass/fail depends on optional features:
# - Audio needs the `flac` CLI + network access for Google Web Speech transcription.
# - The scanned medical report needs OCR/LLM; without it markitdown returns 200
#   with empty markdown.
OPTIONAL_EXPECTATIONS: list[tuple[str, str]] = [
    ("test.mp3", "audio_transcription"),
    ("test.m4a", "audio_transcription"),
    ("test.wav", "audio_transcription"),
    ("MEDRPT-2024-PAT-3847_medical_report_scan.pdf", "ocr"),
]


# --------------------------------------------------------------------------- #
# Hard expectations
# --------------------------------------------------------------------------- #


@pytest.mark.parametrize(
    "filename,expected_status,must_contain",
    HARD_EXPECTATIONS,
    ids=[e[0] for e in HARD_EXPECTATIONS],
)
def test_sample_converts_successfully(
    client, filename: str, expected_status: int, must_contain: str
) -> None:
    path = SAMPLES_DIR / filename
    assert path.exists(), f"Missing sample file: {path}"

    with path.open("rb") as f:
        resp = client.post("/api/v1/convert", files={"file": (filename, f)})

    assert resp.status_code == expected_status, (
        f"{filename}: expected {expected_status}, got {resp.status_code} - {resp.text[:300]}"
    )
    body = resp.json()
    assert body["filename"] == filename
    assert body["bytes"] > 0
    assert isinstance(body["markdown"], str)
    assert len(body["markdown"]) > 0, f"{filename}: empty markdown"
    assert must_contain in body["markdown"], (
        f"{filename}: expected substring {must_contain!r} not found. "
        f"Got: {body['markdown'][:300]!r}"
    )


def test_raw_endpoint_returns_markdown_for_docx(client) -> None:
    path = SAMPLES_DIR / "test.docx"
    with path.open("rb") as f:
        resp = client.post("/api/v1/convert/raw", files={"file": (path.name, f)})
    assert resp.status_code == 200
    assert resp.headers["content-type"].startswith("text/markdown")
    assert "AutoGen" in resp.text


# --------------------------------------------------------------------------- #
# Optional / environment-dependent expectations
# --------------------------------------------------------------------------- #


def _flac_available() -> bool:
    return shutil.which("flac") is not None


@pytest.mark.parametrize(
    "filename",
    [name for name, _ in OPTIONAL_EXPECTATIONS if name.endswith((".mp3", ".m4a", ".wav"))],
)
def test_audio_either_works_or_fails_cleanly(client, filename: str) -> None:
    """Audio transcription needs the FLAC CLI + network access for Google Web
    Speech. We accept either: 200 with EXIF/transcript markdown, or 422 with a
    clearly attributable error (no opaque 500s)."""
    path = SAMPLES_DIR / filename
    with path.open("rb") as f:
        resp = client.post("/api/v1/convert", files={"file": (filename, f)})

    if resp.status_code == 200:
        # Conversion worked end to end.
        body = resp.json()
        assert isinstance(body["markdown"], str)
        # When FLAC + network are available, markitdown emits at least an
        # AudioTranscript section or some EXIF.
        assert len(body["markdown"]) > 0
        return

    # Otherwise expect 422 with the converter+exception named in the detail.
    assert resp.status_code == 422, f"{filename}: unexpected {resp.status_code}: {resp.text[:300]}"
    detail = resp.json()["detail"]
    assert "AudioConverter" in detail, detail
    if not _flac_available():
        assert re.search(r"FLAC|flac", detail), (
            f"FLAC is missing on this host; expected the detail to mention it: {detail}"
        )


def test_scanned_pdf_returns_empty_without_ocr(client) -> None:
    """Without LLM/OCR the scanned PDF cannot be extracted. markitdown returns
    200 with empty markdown. Verifies the no-OCR behaviour is at least clean."""
    path = SAMPLES_DIR / "MEDRPT-2024-PAT-3847_medical_report_scan.pdf"
    with path.open("rb") as f:
        resp = client.post("/api/v1/convert", files={"file": (path.name, f)})
    assert resp.status_code == 200
    body = resp.json()
    # Empty *is* the correct behaviour for the no-OCR build; if a future build
    # adds OCR this assertion will flip and we update it then.
    assert body["markdown"] == "", (
        f"Scanned PDF unexpectedly produced markdown: {body['markdown'][:200]!r}"
    )


# --------------------------------------------------------------------------- #
# Inventory sanity check
# --------------------------------------------------------------------------- #


def test_every_sample_file_has_a_test_case() -> None:
    """If someone adds a new sample to tests/samples/ they must add an entry to
    HARD_EXPECTATIONS or OPTIONAL_EXPECTATIONS so it gets coverage."""
    on_disk = {p.name for p in SAMPLES_DIR.iterdir() if p.is_file()}
    covered = {name for name, _, _ in HARD_EXPECTATIONS} | {
        name for name, _ in OPTIONAL_EXPECTATIONS
    }
    missing = on_disk - covered
    assert not missing, (
        f"Add these samples to HARD_EXPECTATIONS or OPTIONAL_EXPECTATIONS in "
        f"test_samples.py so they're covered: {sorted(missing)}"
    )
