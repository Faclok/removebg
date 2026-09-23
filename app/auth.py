import hashlib
import secrets
from dataclasses import dataclass
from datetime import UTC, datetime

from fastapi import Depends, Request, Security
from fastapi.security import APIKeyHeader, HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.database import get_db
from app.errors import ApiError
from app.models import Account, ApiKey


@dataclass(slots=True)
class Principal:
    account: Account
    api_key: ApiKey


api_key_header = APIKeyHeader(
    name="X-API-Key",
    scheme_name="ApiKeyAuth",
    description="API-ключ вида rb_live_...",
    auto_error=False,
)
bearer_header = HTTPBearer(
    scheme_name="BearerAuth",
    description="Тот же API-ключ как Bearer token",
    auto_error=False,
)


def hash_api_key(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def issue_api_key() -> tuple[str, str, str]:
    value = f"rb_live_{secrets.token_urlsafe(32)}"
    return value, value[:16], hash_api_key(value)


def get_principal(
    request: Request,
    db: Session = Depends(get_db),
    header_key: str | None = Security(api_key_header),
    bearer: HTTPAuthorizationCredentials | None = Security(bearer_header),
) -> Principal:
    value = header_key or (bearer.credentials if bearer else None)

    if not value:
        raise ApiError(401, "missing_api_key", "Передайте ключ в X-API-Key или Bearer token")

    api_key = db.scalar(
        select(ApiKey).where(
            ApiKey.secret_hash == hash_api_key(value),
            ApiKey.revoked_at.is_(None),
        )
    )
    if api_key is None:
        raise ApiError(401, "invalid_api_key", "API-ключ недействителен или отозван")

    account = db.get(Account, api_key.account_id)
    if account is None:
        raise ApiError(401, "invalid_api_key", "Аккаунт для API-ключа не найден")

    api_key.last_used_at = datetime.now(UTC)
    db.commit()
    return Principal(account=account, api_key=api_key)
