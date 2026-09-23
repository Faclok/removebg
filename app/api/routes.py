import secrets
import time
from datetime import UTC, datetime
from decimal import ROUND_HALF_UP, Decimal

from fastapi import APIRouter, Depends, File, Header, Query, Request, Response, UploadFile
from sqlalchemy import func, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.auth import Principal, get_principal, issue_api_key
from app.config import Settings
from app.database import get_db
from app.errors import ApiError
from app.models import Account, ApiKey, LedgerEntry, UsageRecord
from app.schemas import (
    ApiKeyCreated,
    ApiKeyCreateRequest,
    ApiKeyView,
    BalanceResponse,
    ErrorResponse,
    RegisterRequest,
    RegisterResponse,
    TopUpRequest,
    TopUpResponse,
    UsageItem,
    UsageResponse,
    rubles,
)
from app.services.billing import (
    charge_success,
    pricing_table,
    record_failure,
    successful_count,
    unit_price_kopecks,
)

router = APIRouter(prefix="/v1")
ERRORS = {
    401: {"model": ErrorResponse},
    402: {"model": ErrorResponse},
    422: {"model": ErrorResponse},
}


def settings_from(request: Request) -> Settings:
    return request.app.state.settings


@router.post(
    "/accounts/register",
    response_model=RegisterResponse,
    status_code=201,
    summary="Создать аккаунт и получить первый API-ключ",
)
def register(payload: RegisterRequest, request: Request, db: Session = Depends(get_db)):
    settings = settings_from(request)
    raw_key, prefix, key_hash = issue_api_key()
    account = Account(
        name=payload.name.strip(),
        email=str(payload.email).lower(),
        balance_kopecks=settings.starting_balance_kopecks,
    )
    db.add(account)
    try:
        db.flush()
        api_key = ApiKey(
            account_id=account.id,
            name="Первый ключ",
            prefix=prefix,
            secret_hash=key_hash,
        )
        db.add(api_key)
        db.add(
            LedgerEntry(
                account_id=account.id,
                amount_kopecks=settings.starting_balance_kopecks,
                balance_after_kopecks=settings.starting_balance_kopecks,
                kind="welcome_bonus",
                description="Стартовый баланс",
            )
        )
        db.commit()
    except IntegrityError as exc:
        db.rollback()
        raise ApiError(409, "email_exists", "Аккаунт с таким email уже существует") from exc

    return RegisterResponse(
        account_id=account.id,
        api_key=raw_key,
        balance_rub=rubles(account.balance_kopecks),
        message="Сохраните API-ключ: повторно он показан не будет",
    )


@router.get("/balance", response_model=BalanceResponse, responses=ERRORS)
def get_balance(
    request: Request,
    principal: Principal = Depends(get_principal),
    db: Session = Depends(get_db),
):
    count = successful_count(db, principal.account.id)
    db.refresh(principal.account)
    settings = settings_from(request)
    return BalanceResponse(
        account_id=principal.account.id,
        balance_rub=rubles(principal.account.balance_kopecks),
        successful_images_this_month=count,
        next_image_price_rub=rubles(unit_price_kopecks(settings, count)),
        pricing=pricing_table(settings),
    )


@router.post(
    "/remove-background",
    responses={
        200: {"content": {"image/png": {}}, "description": "PNG с прозрачным фоном"},
        401: {"model": ErrorResponse},
        402: {"model": ErrorResponse},
        413: {"model": ErrorResponse},
        415: {"model": ErrorResponse},
        422: {"model": ErrorResponse},
        503: {"model": ErrorResponse},
    },
    summary="Удалить фон изображения",
)
async def remove_background(
    request: Request,
    image: UploadFile = File(description="JPEG, PNG или WebP"),
    principal: Principal = Depends(get_principal),
    db: Session = Depends(get_db),
):
    started = time.perf_counter()
    settings = settings_from(request)
    content_type = (image.content_type or "").lower()
    if content_type not in {"image/jpeg", "image/png", "image/webp"}:
        raise ApiError(415, "unsupported_media_type", "Поддерживаются JPEG, PNG и WebP")

    payload = await image.read(settings.max_upload_bytes + 1)
    if not payload:
        raise ApiError(422, "empty_file", "Получен пустой файл")
    if len(payload) > settings.max_upload_bytes:
        raise ApiError(413, "file_too_large", "Файл превышает допустимый размер")

    count = successful_count(db, principal.account.id)
    price = unit_price_kopecks(settings, count)
    db.refresh(principal.account)
    if principal.account.balance_kopecks < price:
        raise ApiError(402, "insufficient_funds", "Недостаточно средств на балансе")

    try:
        width, height = request.app.state.background_service.validate(payload)
        output = await request.app.state.background_service.remove(payload)
    except ApiError as exc:
        record_failure(
            db,
            request_id=request.state.request_id,
            account_id=principal.account.id,
            api_key_id=principal.api_key.id,
            filename=image.filename,
            input_bytes=len(payload),
            duration_ms=int((time.perf_counter() - started) * 1000),
            error_code=exc.code,
        )
        raise
    except Exception as exc:
        record_failure(
            db,
            request_id=request.state.request_id,
            account_id=principal.account.id,
            api_key_id=principal.api_key.id,
            filename=image.filename,
            input_bytes=len(payload),
            duration_ms=int((time.perf_counter() - started) * 1000),
            error_code="processing_failed",
        )
        raise ApiError(503, "processing_failed", "Не удалось обработать изображение") from exc

    duration_ms = int((time.perf_counter() - started) * 1000)
    balance_after = charge_success(
        db,
        account_id=principal.account.id,
        api_key_id=principal.api_key.id,
        request_id=request.state.request_id,
        filename=image.filename,
        input_bytes=len(payload),
        output_bytes=len(output),
        duration_ms=duration_ms,
        price_kopecks=price,
    )
    return Response(
        output,
        media_type="image/png",
        headers={
            "Content-Disposition": 'attachment; filename="background-removed.png"',
            "X-Request-ID": request.state.request_id,
            "X-Image-Width": str(width),
            "X-Image-Height": str(height),
            "X-Charged-Rub": str(rubles(price)),
            "X-Balance-Rub": str(rubles(balance_after)),
        },
    )


@router.get("/usage", response_model=UsageResponse, responses=ERRORS)
def get_usage(
    principal: Principal = Depends(get_principal),
    db: Session = Depends(get_db),
    limit: int = Query(default=50, ge=1, le=200),
    offset: int = Query(default=0, ge=0),
):
    condition = UsageRecord.account_id == principal.account.id
    total = int(db.scalar(select(func.count(UsageRecord.id)).where(condition)) or 0)
    successful = int(
        db.scalar(
            select(func.count(UsageRecord.id)).where(condition, UsageRecord.status == "success")
        )
        or 0
    )
    charged = int(
        db.scalar(select(func.coalesce(func.sum(UsageRecord.price_kopecks), 0)).where(condition))
        or 0
    )
    records = db.scalars(
        select(UsageRecord)
        .where(condition)
        .order_by(UsageRecord.created_at.desc())
        .offset(offset)
        .limit(limit)
    ).all()
    return UsageResponse(
        total=total,
        successful=successful,
        charged_rub=rubles(charged),
        items=[
            UsageItem(
                id=item.id,
                request_id=item.request_id,
                status=item.status,
                filename=item.filename,
                input_bytes=item.input_bytes,
                output_bytes=item.output_bytes,
                price_rub=rubles(item.price_kopecks),
                duration_ms=item.duration_ms,
                error_code=item.error_code,
                created_at=item.created_at,
            )
            for item in records
        ],
    )


@router.get("/api-keys", response_model=list[ApiKeyView], responses=ERRORS)
def list_api_keys(principal: Principal = Depends(get_principal), db: Session = Depends(get_db)):
    return db.scalars(
        select(ApiKey)
        .where(ApiKey.account_id == principal.account.id)
        .order_by(ApiKey.created_at.desc())
    ).all()


@router.post("/api-keys", response_model=ApiKeyCreated, status_code=201, responses=ERRORS)
def create_api_key(
    payload: ApiKeyCreateRequest,
    principal: Principal = Depends(get_principal),
    db: Session = Depends(get_db),
):
    raw_key, prefix, key_hash = issue_api_key()
    api_key = ApiKey(
        account_id=principal.account.id,
        name=payload.name.strip(),
        prefix=prefix,
        secret_hash=key_hash,
    )
    db.add(api_key)
    db.commit()
    db.refresh(api_key)
    return ApiKeyCreated(
        id=api_key.id,
        name=api_key.name,
        api_key=raw_key,
        prefix=api_key.prefix,
        created_at=api_key.created_at,
    )


@router.delete("/api-keys/{key_id}", status_code=204, responses=ERRORS)
def revoke_api_key(
    key_id: str,
    principal: Principal = Depends(get_principal),
    db: Session = Depends(get_db),
):
    key = db.scalar(
        select(ApiKey).where(ApiKey.id == key_id, ApiKey.account_id == principal.account.id)
    )
    if key is None:
        raise ApiError(404, "api_key_not_found", "API-ключ не найден")
    if key.revoked_at is None:
        key.revoked_at = datetime.now(UTC)
        db.commit()
    return Response(status_code=204)


@router.post(
    "/admin/accounts/{account_id}/top-up",
    response_model=TopUpResponse,
    summary="Пополнить баланс вручную",
)
def admin_top_up(
    account_id: str,
    payload: TopUpRequest,
    request: Request,
    x_admin_token: str | None = Header(default=None),
    db: Session = Depends(get_db),
):
    configured_token = settings_from(request).admin_token
    if not configured_token:
        raise ApiError(503, "admin_disabled", "Административный API отключён")
    if not x_admin_token or not secrets.compare_digest(x_admin_token, configured_token):
        raise ApiError(401, "invalid_admin_token", "Неверный административный токен")

    account = db.get(Account, account_id)
    if account is None:
        raise ApiError(404, "account_not_found", "Аккаунт не найден")

    amount = int((payload.amount_rub * Decimal(100)).quantize(Decimal("1"), ROUND_HALF_UP))
    account.balance_kopecks += amount
    db.add(
        LedgerEntry(
            account_id=account.id,
            amount_kopecks=amount,
            balance_after_kopecks=account.balance_kopecks,
            kind="top_up",
            description=payload.description,
        )
    )
    db.commit()
    return TopUpResponse(
        account_id=account.id,
        added_rub=rubles(amount),
        balance_rub=rubles(account.balance_kopecks),
    )
