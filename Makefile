.PHONY: install dev test lint docker-up docker-down

install:
	python -m pip install -e ".[dev]"

dev:
	uvicorn app.main:app --reload --host 0.0.0.0 --port 8000

test:
	pytest

lint:
	ruff check .

docker-up:
	docker compose up --build

docker-down:
	docker compose down

