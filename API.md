# Vaultix Server API v1

Basis: `/api/v1`. JSON nutzt camelCase. Binärdaten werden gestreamt.

| Methode | Pfad | Auth | Zweck |
|---|---|---:|---|
| GET | `/health` | nein | Version, Storage und freien Platz prüfen |
| POST | `/devices/pair` | nein | privates Gerät koppeln |
| POST | `/objects/check` | ja | fehlende SHA-256-Objekte bestimmen |
| PUT | `/objects/{sha256}` | ja | Objekt streamen und verifizieren |
| GET | `/snapshots` | ja | Snapshots des Geräts auflisten |
| GET | `/snapshots/{id}` | ja | Snapshot samt Dateieinträgen lesen |
| POST | `/snapshots` | ja | Snapshot transaktional erstellen |
| GET | `/snapshots/{id}/files?path=...` | ja | Datei streamend wiederherstellen |

Authentifizierte Anfragen senden `X-Vaultix-Device` und `X-Vaultix-Secret`. Fehler enthalten keine internen Stacktraces. Uploads sind standardmäßig auf 512 GiB pro Objekt begrenzt und unterstützen Range-Downloads.
