from io import BytesIO

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from app.config import Settings
from app.errors import ApiError
from app.main import create_app


class FakeBackgroundService:
    def __init__(self, fail: bool = False) -> None:
        self.fail = fail

    def validate(self, payload: bytes) -> tuple[int, int]:
        with Image.open(BytesIO(payload)) as image:
            return image.size

    async def remove(self, payload: bytes) -> bytes:
        if self.fail:
            raise ApiError(503, "processing_failed", "Тестовая ошибка")
        with Image.open(BytesIO(payload)) as image:
            result = image.convert("RGBA")
            output = BytesIO()
            result.save(output, "PNG")
            return output.getvalue()


def png_bytes() -> bytes:
    output = BytesIO()
    Image.new("RGB", (8, 6), "white").save(output, "PNG")
    return output.getvalue()


@pytest.fixture
def client(tmp_path):
    settings = Settings(
        database_url=f"sqlite:///{tmp_path / 'test.db'}",
        starting_balance_kopecks=100_000,
        admin_token="test-admin-token",
    )
    with TestClient(create_app(settings, FakeBackgroundService())) as test_client:
        yield test_client


def register(client: TestClient, email: str = "test@example.ru") -> dict:
    response = client.post("/v1/accounts/register", json={"name": "ООО Тест", "email": email})
    assert response.status_code == 201
    return response.json()


def test_register_gets_1000_rubles_and_key(client: TestClient):
    data = register(client)
    assert data["balance_rub"] == "1000.00"
    assert data["api_key"].startswith("rb_live_")

    balance = client.get("/v1/balance", headers={"X-API-Key": data["api_key"]})
    assert balance.status_code == 200
    assert balance.json()["next_image_price_rub"] == "0.60"


def test_successful_removal_charges_only_success(client: TestClient):
    account = register(client)
    headers = {"X-API-Key": account["api_key"]}
    response = client.post(
        "/v1/remove-background",
        headers=headers,
        files={"image": ("product.png", png_bytes(), "image/png")},
    )
    assert response.status_code == 200
    assert response.headers["content-type"] == "image/png"
    assert response.headers["x-charged-rub"] == "0.60"

    balance = client.get("/v1/balance", headers=headers).json()
    assert balance["balance_rub"] == "999.40"
    usage = client.get("/v1/usage", headers=headers).json()
    assert usage["successful"] == 1
    assert usage["charged_rub"] == "0.60"


def test_processing_failure_is_not_charged(tmp_path):
    settings = Settings(database_url=f"sqlite:///{tmp_path / 'failed.db'}")
    with TestClient(create_app(settings, FakeBackgroundService(fail=True))) as client:
        account = register(client, "fail@example.ru")
        headers = {"X-API-Key": account["api_key"]}
        response = client.post(
            "/v1/remove-background",
            headers=headers,
            files={"image": ("product.png", png_bytes(), "image/png")},
        )
        assert response.status_code == 503
        assert client.get("/v1/balance", headers=headers).json()["balance_rub"] == "1000.00"
        usage = client.get("/v1/usage", headers=headers).json()
        assert usage["successful"] == 0
        assert usage["items"][0]["price_rub"] == "0.00"


def test_duplicate_email_and_invalid_key(client: TestClient):
    register(client)
    duplicate = client.post(
        "/v1/accounts/register", json={"name": "Дубль", "email": "test@example.ru"}
    )
    assert duplicate.status_code == 409
    assert duplicate.json()["error"]["code"] == "email_exists"

    unauthorized = client.get("/v1/balance", headers={"X-API-Key": "wrong"})
    assert unauthorized.status_code == 401


def test_api_key_lifecycle_and_admin_top_up(client: TestClient):
    account = register(client)
    headers = {"X-API-Key": account["api_key"]}
    created = client.post("/v1/api-keys", headers=headers, json={"name": "mobile-app"})
    assert created.status_code == 201
    second_key = created.json()["api_key"]

    listed = client.get("/v1/api-keys", headers={"Authorization": f"Bearer {second_key}"})
    assert listed.status_code == 200
    assert len(listed.json()) == 2

    topped_up = client.post(
        f"/v1/admin/accounts/{account['account_id']}/top-up",
        headers={"X-Admin-Token": "test-admin-token"},
        json={"amount_rub": "2000.00", "description": "Тест"},
    )
    assert topped_up.status_code == 200
    assert topped_up.json()["balance_rub"] == "3000.00"


def test_openapi_exposes_auth_and_binary_response(client: TestClient):
    schema = client.get("/openapi.json").json()
    schemes = schema["components"]["securitySchemes"]
    assert schemes["ApiKeyAuth"]["name"] == "X-API-Key"
    assert schemes["BearerAuth"]["scheme"] == "bearer"
    operation = schema["paths"]["/v1/remove-background"]["post"]
    assert {"ApiKeyAuth": []} in operation["security"]
    assert "image/png" in operation["responses"]["200"]["content"]
