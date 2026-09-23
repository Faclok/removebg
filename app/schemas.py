from datetime import datetime
from decimal import Decimal

from pydantic import BaseModel, ConfigDict, EmailStr, Field


def rubles(kopecks: int) -> Decimal:
    return (Decimal(kopecks) / Decimal(100)).quantize(Decimal("0.00"))


class RegisterRequest(BaseModel):
    name: str = Field(min_length=1, max_length=200)
    email: EmailStr


class RegisterResponse(BaseModel):
    account_id: str
    api_key: str
    balance_rub: Decimal
    message: str


class ApiKeyCreateRequest(BaseModel):
    name: str = Field(min_length=1, max_length=100)


class ApiKeyCreated(BaseModel):
    id: str
    name: str
    api_key: str
    prefix: str
    created_at: datetime


class ApiKeyView(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: str
    name: str
    prefix: str
    created_at: datetime
    last_used_at: datetime | None
    revoked_at: datetime | None


class PricingTier(BaseModel):
    from_images: int
    to_images: int | None
    price_rub: Decimal


class BalanceResponse(BaseModel):
    account_id: str
    balance_rub: Decimal
    successful_images_this_month: int
    next_image_price_rub: Decimal
    pricing: list[PricingTier]


class UsageItem(BaseModel):
    id: str
    request_id: str
    status: str
    filename: str | None
    input_bytes: int
    output_bytes: int
    price_rub: Decimal
    duration_ms: int
    error_code: str | None
    created_at: datetime


class UsageResponse(BaseModel):
    total: int
    successful: int
    charged_rub: Decimal
    items: list[UsageItem]


class TopUpRequest(BaseModel):
    amount_rub: Decimal = Field(gt=0, decimal_places=2, max_digits=12)
    description: str = Field(default="Административное пополнение", max_length=300)


class TopUpResponse(BaseModel):
    account_id: str
    added_rub: Decimal
    balance_rub: Decimal


class ErrorBody(BaseModel):
    code: str
    message: str
    request_id: str | None = None


class ErrorResponse(BaseModel):
    error: ErrorBody
