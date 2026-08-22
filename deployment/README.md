# Vaultix Server mit Docker Compose

Der Stack betreibt den Vaultix-Server standardmaessig nur auf `127.0.0.1:7443` und startet Caddy als TLS-Reverse-Proxy auf Port 443. Die Windows-App verbindet sich nur mit der HTTPS-Adresse von Caddy. Den internen Containerport nicht direkt ins LAN oder Internet veroeffentlichen.

## Start auf Ubuntu

```bash
git clone <dein-vaultix-repository> vaultix
cd vaultix/deployment
cp .env.example .env
sudo nano .env # VAULTIX_TLS_HOST auf die LAN-IP oder den DNS-Namen dieses Servers setzen
docker compose up -d --build
docker compose ps
```

Caddy erstellt eine lokale Zertifizierungsstelle. Fuer den Windows-PC einmalig das Stammzertifikat exportieren und als vertrauenswuerdiges Stammzertifikat importieren:

```bash
docker compose cp vaultix-proxy:/data/caddy/pki/authorities/local/root.crt ./vaultix-local-ca.crt
curl --cacert ./vaultix-local-ca.crt https://<VAULTIX_TLS_HOST>/api/v1/health
```

Die Datei auf den Windows-PC uebertragen und dort in einer PowerShell **als Administrator** importieren:

```powershell
certutil -addstore -f Root .\vaultix-local-ca.crt
```

Danach wird in Vaultix als Server-Adresse `https://<VAULTIX_TLS_HOST>` verwendet.

Zum erstmaligen Koppeln eines Windows-PCs `VAULTIX_PAIRING_ENABLED=true` in `.env` setzen und `docker compose up -d` ausfuehren. Nach erfolgreichem Pairing den Wert wieder auf `false` setzen und erneut `docker compose up -d` ausfuehren. Bei deaktiviertem Pairing ist der offene Endpoint nicht erreichbar.

Die Daten liegen im Docker-Volume `vaultix_repository`; sie bleiben bei `docker compose down` erhalten. Nur `docker compose down --volumes` entfernt sie und damit saemtliche Backups.

Der Hilfscontainer `vaultix-permissions` setzt beim Start ausschliesslich die Eigentuemerschaft des Volume-Wurzelordners. Der eigentliche Server laeuft danach als unprivilegierter Benutzer (UID 1654), mit schreibgeschuetztem Root-Dateisystem und ohne Linux-Capabilities.

## Betrieb

```bash
docker compose logs -f vaultix-server
docker compose up -d --build
```

Vor Updates das Volume sichern. Eine Sicherung muss das gesamte Repository einschliesslich der SQLite-Dateien und des `objects`-Verzeichnisses enthalten. Pairing sollte nur waehrend der Einrichtung moeglich sein; der Reverse-Proxy beziehungsweise die Firewall muss den Zugriff auf bekannte Heimnetz-Clients beschraenken.
