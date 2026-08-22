# Vaultix Server mit Docker Compose

Der Stack betreibt den Server standardmaessig nur auf `127.0.0.1:7443`. Fuer Zugriffe aus dem Heimnetz muss ein TLS-Reverse-Proxy (zum Beispiel Caddy, Traefik oder nginx) davorstehen. Die Windows-App verbindet sich dann mit dessen HTTPS-URL. Den Containerport nicht direkt ins LAN oder Internet veroeffentlichen.

## Start auf Ubuntu

```bash
git clone <dein-vaultix-repository> vaultix
cd vaultix/deployment
cp .env.example .env
docker compose up -d --build
docker compose ps
curl --fail http://127.0.0.1:7443/api/v1/health
```

Zum erstmaligen Koppeln eines Windows-PCs `VAULTIX_PAIRING_ENABLED=true` in `.env` setzen und `docker compose up -d` ausfuehren. Nach erfolgreichem Pairing den Wert wieder auf `false` setzen und erneut `docker compose up -d` ausfuehren. Bei deaktiviertem Pairing ist der offene Endpoint nicht erreichbar.

Die Daten liegen im Docker-Volume `vaultix_repository`; sie bleiben bei `docker compose down` erhalten. Nur `docker compose down --volumes` entfernt sie und damit saemtliche Backups.

Der Hilfscontainer `vaultix-permissions` setzt beim Start ausschliesslich die Eigentuemerschaft des Volume-Wurzelordners. Der eigentliche Server laeuft danach als unprivilegierter Benutzer (UID 1654), mit schreibgeschuetztem Root-Dateisystem und ohne Linux-Capabilities.

## Betrieb

```bash
docker compose logs -f vaultix-server
docker compose up -d --build
```

Vor Updates das Volume sichern. Eine Sicherung muss das gesamte Repository einschliesslich der SQLite-Dateien und des `objects`-Verzeichnisses enthalten. Pairing sollte nur waehrend der Einrichtung moeglich sein; der Reverse-Proxy beziehungsweise die Firewall muss den Zugriff auf bekannte Heimnetz-Clients beschraenken.
