"""Runtime configuration loaded from environment variables."""

from __future__ import annotations

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_prefix="MARKITDOWN_",
        env_file=None,
        case_sensitive=False,
    )

    host: str = "0.0.0.0"
    port: int = 8000

    max_upload_mb: int = 100

    cors_origins: str = "*"

    allow_url_fetch: bool = False
    url_fetch_timeout_seconds: int = 15
    url_fetch_max_mb: int = 25

    request_id_header: str = "X-Request-ID"

    @property
    def max_upload_bytes(self) -> int:
        return self.max_upload_mb * 1024 * 1024

    @property
    def cors_origin_list(self) -> list[str]:
        return [o.strip() for o in self.cors_origins.split(",") if o.strip()]


settings = Settings()
