from datetime import UTC, datetime

from sqlalchemy import Select, func, select, update
from sqlalchemy.orm import Session

from app.config import Settings
from app.errors import ApiError
from app.models import Account, LedgerEntry, UsageRecord
from app.schemas import PricingTier, rubles


def month_bounds(now: datetime | None = None) -> tuple[datetime, datetime]:
    current = now or datetime.now(UTC)
    start = current.replace(day=1, hour=0, minute=0, second=0, microsecond=0)
    if start.month == 12:
        end = start.replace(year=start.year + 1, month=1)
    else:
        end = start.replace(month=start.month + 1)
    return start, end


def successful_usage_query(account_id: str) -> Select[tuple[int]]:
    start, end = month_bounds()
    return select(func.count(UsageRecord.id)).where(
        UsageRecord.account_id == account_id,
        UsageRecord.status == "success",
        UsageRecord.created_at >= start,
        UsageRecord.created_at < end,
    )


def successful_count(db: Session, account_id: str) -> int:
    return int(db.scalar(successful_usage_query(account_id)) or 0)


def unit_price_kopecks(settings: Settings, count: int) -> int:
    if count < settings.price_tier_1_limit:
        return settings.price_tier_1_kopecks
    if count < settings.price_tier_2_limit:
        return settings.price_tier_2_kopecks
    if count < settings.price_tier_3_limit:
        return settings.price_tier_3_kopecks
    return settings.price_tier_4_kopecks


def pricing_table(settings: Settings) -> list[PricingTier]:
    return [
        PricingTier(
            from_images=0,
            to_images=settings.price_tier_1_limit - 1,
            price_rub=rubles(settings.price_tier_1_kopecks),
        ),
        PricingTier(
            from_images=settings.price_tier_1_limit,
            to_images=settings.price_tier_2_limit - 1,
            price_rub=rubles(settings.price_tier_2_kopecks),
        ),
        PricingTier(
            from_images=settings.price_tier_2_limit,
            to_images=settings.price_tier_3_limit - 1,
            price_rub=rubles(settings.price_tier_3_kopecks),
        ),
        PricingTier(
            from_images=settings.price_tier_3_limit,
            to_images=None,
            price_rub=rubles(settings.price_tier_4_kopecks),
        ),
    ]


def record_failure(
    db: Session,
    *,
    request_id: str,
    account_id: str,
    api_key_id: str,
    filename: str | None,
    input_bytes: int,
    duration_ms: int,
    error_code: str,
) -> None:
    db.add(
        UsageRecord(
            request_id=request_id,
            account_id=account_id,
            api_key_id=api_key_id,
            status="failed",
            filename=filename,
            input_bytes=input_bytes,
            duration_ms=duration_ms,
            error_code=error_code,
        )
    )
    db.commit()


def charge_success(
    db: Session,
    *,
    account_id: str,
    api_key_id: str,
    request_id: str,
    filename: str | None,
    input_bytes: int,
    output_bytes: int,
    duration_ms: int,
    price_kopecks: int,
) -> int:
    result = db.execute(
        update(Account)
        .where(Account.id == account_id, Account.balance_kopecks >= price_kopecks)
        .values(balance_kopecks=Account.balance_kopecks - price_kopecks)
    )
    if result.rowcount != 1:
        db.rollback()
        raise ApiError(402, "insufficient_funds", "Недостаточно средств на балансе")

    balance_after = db.scalar(select(Account.balance_kopecks).where(Account.id == account_id))
    usage = UsageRecord(
        request_id=request_id,
        account_id=account_id,
        api_key_id=api_key_id,
        status="success",
        filename=filename,
        input_bytes=input_bytes,
        output_bytes=output_bytes,
        price_kopecks=price_kopecks,
        duration_ms=duration_ms,
    )
    db.add(usage)
    db.flush()
    db.add(
        LedgerEntry(
            account_id=account_id,
            amount_kopecks=-price_kopecks,
            balance_after_kopecks=int(balance_after or 0),
            kind="usage",
            description="Успешное удаление фона",
            usage_id=usage.id,
        )
    )
    db.commit()
    return int(balance_after or 0)
