def test_convert_txt_returns_content_as_markdown(client, fixtures_dir):
    path = fixtures_dir / "hello.txt"
    with path.open("rb") as f:
        resp = client.post(
            "/api/v1/convert",
            files={"file": ("hello.txt", f, "text/plain")},
        )
    assert resp.status_code == 200, resp.text
    body = resp.json()
    assert "Hello, MarkItDown!" in body["markdown"]
    assert body["filename"] == "hello.txt"
    assert body["bytes"] > 0
    assert body["duration_ms"] >= 0


def test_convert_html_produces_markdown(client, fixtures_dir):
    path = fixtures_dir / "sample.html"
    with path.open("rb") as f:
        resp = client.post(
            "/api/v1/convert",
            files={"file": ("sample.html", f, "text/html")},
        )
    assert resp.status_code == 200, resp.text
    md = resp.json()["markdown"]
    # Heading and a list item from the fixture should appear in some form.
    assert "Heading" in md
    assert "Item" in md


def test_convert_csv_produces_table_or_text(client, fixtures_dir):
    path = fixtures_dir / "sample.csv"
    with path.open("rb") as f:
        resp = client.post(
            "/api/v1/convert",
            files={"file": ("sample.csv", f, "text/csv")},
        )
    assert resp.status_code == 200, resp.text
    md = resp.json()["markdown"]
    # markitdown emits CSV either as a markdown table or whitespace text;
    # either way the data values should be present.
    assert "Alice" in md and "Bob" in md
