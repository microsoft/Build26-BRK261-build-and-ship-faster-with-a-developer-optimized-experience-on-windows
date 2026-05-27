from fastapi.testclient import TestClient


def test_missing_file_field_returns_422(client):
    # FastAPI's File(...) dependency produces a 422 when the form field is absent.
    resp = client.post("/api/v1/convert")
    assert resp.status_code in (400, 422)


def test_unsupported_extension_returns_415(client):
    # A made-up extension with random bytes should trip UnsupportedFormatException.
    resp = client.post(
        "/api/v1/convert",
        files={"file": ("mystery.xyzzy", b"\x00\x01\x02not a real format\x03", "application/octet-stream")},
    )
    assert resp.status_code == 415, resp.text
    assert "Unsupported" in resp.json()["detail"]


def test_oversize_upload_returns_413(monkeypatch):
    # Build a fresh app with a tiny max_upload_mb so a small payload trips the limit.
    monkeypatch.setenv("MARKITDOWN_MAX_UPLOAD_MB", "1")
    # The settings object was built at import time, so re-import after env override.
    import importlib

    import app.config as config_mod
    import app.main as main_mod

    importlib.reload(config_mod)
    importlib.reload(main_mod)

    with TestClient(main_mod.app) as c:
        payload = b"x" * (2 * 1024 * 1024)  # 2 MB > 1 MB limit
        resp = c.post(
            "/api/v1/convert",
            files={"file": ("big.txt", payload, "text/plain")},
        )
        assert resp.status_code == 413, resp.text
        assert "exceeds" in resp.json()["detail"].lower()

    # Restore so subsequent tests get the default 50 MB.
    monkeypatch.delenv("MARKITDOWN_MAX_UPLOAD_MB", raising=False)
    importlib.reload(config_mod)
    importlib.reload(main_mod)


def test_url_fetch_disabled_by_default_returns_403(client):
    resp = client.post(
        "/api/v1/convert/url",
        json={"url": "https://example.com/some.pdf"},
    )
    assert resp.status_code == 403
