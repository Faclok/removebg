from functools import lru_cache

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    app_name: str = "Удалифон API"
    environment: str = "development"
    database_url: str = "sqlite:///./data/removebg.db"
    starting_balance_kopecks: int = Field(default=100_000, ge=0)

    background_model: str = "birefnet-general-lite"
    max_upload_bytes: int = Field(default=15 * 1024 * 1024, ge=1024)
    max_image_pixels: int = Field(default=40_000_000, ge=1_000_000)
    cpu_workers: int = Field(default=2, ge=1, le=32)
    omp_num_threads: int = Field(default=4, ge=1, le=128)

    admin_token: str | None = None

    # Цена успешной обработки в копейках. Границы считаются по календарному
    # месяцу UTC и применяются к следующему изображению.
    price_tier_1_limit: int = 10_000
    price_tier_1_kopecks: int = 60
    price_tier_2_limit: int = 100_000
    price_tier_2_kopecks: int = 45
    price_tier_3_limit: int = 500_000
    price_tier_3_kopecks: int = 30
    price_tier_4_kopecks: int = 20


@lru_cache
def get_settings() -> Settings:
    return Settings()
