.PHONY: restore dev build test format web-install web-dev web-build docker-up docker-down

restore:
	dotnet restore RemoveBg.slnx

dev:
	dotnet watch --project src/RemoveBg.Api

build:
	dotnet build RemoveBg.slnx --no-restore

test:
	dotnet test RemoveBg.slnx --no-restore

format:
	dotnet format RemoveBg.slnx

web-install:
	cd web && npm install

web-dev:
	cd web && npm run dev

web-build:
	cd web && npm run build

docker-up:
	docker compose up --build

docker-down:
	docker compose down
