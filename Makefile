.PHONY: restore dev build test format docker-up docker-down

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

docker-up:
	docker compose up --build

docker-down:
	docker compose down
