.PHONY: restore dev build test format migrate client-install client-dev client-build db-up docker-up docker-down

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

migrate:
	cd server && dotnet ef database update --project src/RemoveBg.Api --startup-project src/RemoveBg.Api

client-install:
	cd client && npm install

client-dev:
	cd client && npm run dev

client-build:
	cd client && npm run build

db-up:
	docker compose -f compose.yaml -f compose.local.yaml up -d mysql

docker-up:
	docker compose -f compose.yaml -f compose.local.yaml up --build

docker-down:
	docker compose -f compose.yaml -f compose.local.yaml down
