"""Shared pytest fixtures."""

from __future__ import annotations

import os
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

FIXTURES_DIR = Path(__file__).parent / "fixtures"


@pytest.fixture(scope="session")
def client() -> TestClient:
    # Ensure deterministic settings across tests.
    os.environ.setdefault("MARKITDOWN_MAX_UPLOAD_MB", "50")
    os.environ.setdefault("MARKITDOWN_CORS_ORIGINS", "*")
    os.environ.setdefault("MARKITDOWN_ALLOW_URL_FETCH", "false")

    from app.main import app  # imported after env is set

    with TestClient(app) as c:
        yield c


@pytest.fixture
def fixtures_dir() -> Path:
    return FIXTURES_DIR
