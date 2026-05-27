def test_convert_raw_returns_text_markdown(client, fixtures_dir):
    path = fixtures_dir / "hello.txt"
    with path.open("rb") as f:
        resp = client.post(
            "/api/v1/convert/raw",
            files={"file": ("hello.txt", f, "text/plain")},
        )
    assert resp.status_code == 200, resp.text
    assert resp.headers["content-type"].startswith("text/markdown")
    assert "Hello, MarkItDown!" in resp.text
