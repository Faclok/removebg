import os
import uuid
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware

from app.api.routes import router
from app.config import Settings, get_settings
from app.database import Database
from app.errors import ApiError, api_error_handler
from app.services.background import BackgroundRemovalService


def create_app(
    settings: Settings | None = None,
    background_service: BackgroundRemovalService | None = None,
) -> FastAPI:
    config = settings or get_settings()
    os.environ.setdefault("OMP_NUM_THREADS", str(config.omp_num_threads))
    database = Database(config.database_url)
    processor = background_service or BackgroundRemovalService(
        model_name=config.background_model,
        workers=config.cpu_workers,
        max_image_pixels=config.max_image_pixels,
    )

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        database.create_schema()
        yield
        database.dispose()

    app = FastAPI(
        title=config.app_name,
        version="0.1.0",
        description=(
            "CPU-only API удаления фона. Списание происходит только за успешную обработку; "
            "новый аккаунт получает стартовый баланс 1000 ₽."
        ),
        docs_url="/docs",
        redoc_url="/redoc",
        openapi_url="/openapi.json",
        lifespan=lifespan,
    )
    app.state.settings = config
    app.state.database = database
    app.state.background_service = processor
    app.add_exception_handler(ApiError, api_error_handler)
    app.add_middleware(
        CORSMiddleware,
        allow_origins=[],
        allow_credentials=False,
        allow_methods=["GET", "POST", "DELETE"],
        allow_headers=["Authorization", "Content-Type", "X-API-Key", "X-Admin-Token"],
    )

    @app.middleware("http")
    async def request_id_middleware(request: Request, call_next):
        request.state.request_id = str(uuid.uuid4())
        response = await call_next(request)
        response.headers["X-Request-ID"] = request.state.request_id
        return response

    @app.get("/health/live", tags=["health"])
    def liveness():
        return {"status": "ok"}

    @app.get("/health/ready", tags=["health"])
    def readiness(request: Request):
        with request.app.state.database.engine.connect() as connection:
            connection.exec_driver_sql("SELECT 1")
        return {"status": "ready", "model": config.background_model, "device": "cpu"}

    app.include_router(router)
    return app


app = create_app()
