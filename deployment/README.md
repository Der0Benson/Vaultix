# Vaultix Server mit Docker Compose

Der Stack betreibt den Vaultix-Server standardmaessig nur auf `127.0.0.1:7443` und startet Caddy als TLS-Reverse-Proxy auf Port 443. Die Windows-App verbindet sich nur mit der HTTPS-Adresse von Caddy. Den internen Containerport nicht direkt ins LAN oder Internet veroeffentlichen.

## Start auf Ubuntu

```bash
git clone <dein-vaultix-repository> vaultix
cd vaultix/deployment
cp .env.example .env
sudo nano .env # VAULTIX_TLS_HOST auf die LAN-IP oder den DNS-Namen dieses Servers setzen
mkdir -p tls
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 3650 \
  -keyout tls/vaultix.key -out tls/vaultix.crt \
  -subj "/CN=<VAULTIX_TLS_HOST>" \
  -addext "subjectAltName = IP:<VAULTIX_TLS_HOST>" \
  -addext "keyUsage = critical,digitalSignature,keyEncipherment" \
  -addext "extendedKeyUsage = serverAuth"
chmod 600 tls/vaultix.key
docker compose up -d --build
docker compose ps
```

Caddy verwendet ein lokales RSA-Zertifikat. Fuer den Windows-PC die Zertifikatsdatei `tls/vaultix.crt` uebertragen und als vertrauenswuerdiges Stammzertifikat importieren:

In einer PowerShell **als Administrator**:

```powershell
certutil -addstore -f Root .\vaultix.crt
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
