import asyncio
import threading
from io import BytesIO

from anyio import to_thread
from PIL import Image, UnidentifiedImageError

from app.errors import ApiError


class BackgroundRemovalService:
    def __init__(self, model_name: str, workers: int, max_image_pixels: int) -> None:
        self.model_name = model_name
        self.max_image_pixels = max_image_pixels
        self._semaphore = asyncio.Semaphore(workers)
        self._session = None
        self._session_lock = threading.Lock()

    def validate(self, payload: bytes) -> tuple[int, int]:
        try:
            with Image.open(BytesIO(payload)) as image:
                width, height = image.size
                if width <= 0 or height <= 0:
                    raise ApiError(422, "invalid_image", "Изображение имеет некорректный размер")
                if width * height > self.max_image_pixels:
                    raise ApiError(
                        413,
                        "image_too_large",
                        f"Допустимо не более {self.max_image_pixels} пикселей",
                    )
                image.verify()
        except ApiError:
            raise
        except (UnidentifiedImageError, OSError, ValueError) as exc:
            raise ApiError(
                422, "invalid_image", "Файл не является корректным изображением"
            ) from exc
        return width, height

    def _get_session(self):
        if self._session is not None:
            return self._session
        with self._session_lock:
            if self._session is None:
                from rembg import new_session

                self._session = new_session(
                    self.model_name,
                    providers=["CPUExecutionProvider"],
                )
        return self._session

    def _remove_sync(self, payload: bytes) -> bytes:
        from rembg import remove

        output = remove(payload, session=self._get_session(), force_return_bytes=True)
        if not isinstance(output, bytes):
            buffer = BytesIO()
            output.save(buffer, format="PNG")
            output = buffer.getvalue()
        return output

    async def remove(self, payload: bytes) -> bytes:
        async with self._semaphore:
            return await to_thread.run_sync(self._remove_sync, payload)
