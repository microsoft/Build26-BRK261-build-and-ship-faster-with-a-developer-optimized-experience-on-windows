def test_health_returns_ok(client):
    resp = client.get("/api/v1/health")
    assert resp.status_code == 200
    body = resp.json()
    assert body["status"] == "ok"
    assert isinstance(body["version"], str) and body["version"]


def test_health_sets_request_id_header(client):
    resp = client.get("/api/v1/health")
    assert "X-Request-ID" in resp.headers


def test_formats_lists_extensions(client):
    resp = client.get("/api/v1/formats")
    assert resp.status_code == 200
    exts = resp.json()["extensions"]
    assert "pdf" in exts and "docx" in exts and "xlsx" in exts and "txt" in exts


def test_openapi_exposes_convert_routes(client):
    resp = client.get("/openapi.json")
    assert resp.status_code == 200
    paths = resp.json()["paths"]
    assert "/api/v1/convert" in paths
    assert "/api/v1/convert/raw" in paths
    assert "post" in paths["/api/v1/convert"]


def test_index_html_served(client):
    resp = client.get("/")
    assert resp.status_code == 200
    assert resp.headers["content-type"].startswith("text/html")
    assert "MarkItDown" in resp.text
