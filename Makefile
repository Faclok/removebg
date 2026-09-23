.PHONY: restore dev build test format client-install client-dev client-build docker-up docker-down

restore:
	cd server && dotnet restore RemoveBg.slnx

dev:
	cd server && dotnet watch --project src/RemoveBg.Api

build:
	cd server && dotnet build RemoveBg.slnx --no-restore

test:
	cd server && dotnet test RemoveBg.slnx --no-restore

format:
	cd server && dotnet format RemoveBg.slnx

client-install:
	cd client && npm install

client-dev:
	cd client && npm run dev

client-build:
	cd client && npm run build

docker-up:
	docker compose -f compose.yaml -f compose.local.yaml up --build

docker-down:
	docker compose -f compose.yaml -f compose.local.yaml down
