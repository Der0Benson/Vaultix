# Vaultix Sicherheit

## Vertrauensmodell

Vaultix ist für einen einzelnen privaten PC und einen Server im vertrauenswürdigen Heimnetz ausgelegt. Der Server wird standardmäßig nicht öffentlich gebunden. LAN-Verbindungen vom Windows-Dienst werden nur über HTTPS akzeptiert; HTTP ist auf Loopback begrenzt.

## Umgesetzte Schutzmaßnahmen

- 256-Bit Device-Secrets aus einem kryptografischen Zufallszahlengenerator
- Secrets serverseitig nur als SHA-256-Prüfwert und clientseitig per Windows DPAPI (`LocalMachine`) gespeichert
- keine Secrets, Dateiinhalte oder Tokens in Anwendungslogs
- Authentifizierung für Objects, Snapshots und Restore; Pairing ist der einzige initial offene Mutations-Endpunkt
- validierte 64-stellige SHA-256-IDs und relative Snapshot-Pfade ohne Traversal
- serverseitiges Uploadlimit und Hash-Verifikation
- temporäre Uploaddatei, Flush-to-disk und atomarer Move
- SQLite-Fremdschlüssel und Snapshot-Transaktionen
- kein Client-Endpunkt zum Löschen von Snapshots oder Leeren des Repositorys
- Reparse Points werden beim Scan nicht verfolgt
- Restore schreibt mit Dienstrechten nur in den kontrollierten Vaultix-Stagingbereich
- bekannte verwundbare SQLite-Native-Abhängigkeit wurde auf die reparierte Version 2.1.12 überschrieben

## Deployment-Pflichten

- Pairing-Endpunkt nur während der Einrichtung aus dem Heimnetz erreichbar machen.
- Kestrel mit einem vertrauenswürdigen Zertifikat konfigurieren; Zertifikatsprüfung niemals abschalten.
- Ubuntu-Firewall auf die PC-Adresse beziehungsweise das private Subnetz begrenzen.
- Repository und `/etc/vaultix/server.env` nur für den Dienstbenutzer lesbar machen.
- Die Windows-Service- und Named-Pipe-Berechtigungen im Installer auf den vorgesehenen lokalen Benutzer begrenzen. Das mitgelieferte Skript ist ein Entwicklungshelfer und noch kein gehärteter Installer.

## Bewusste 0.1-Grenzen

Client-seitige Verschlüsselung, VSS, Mass-Change-Pause, Retention/GC und signierte Updates folgen später. Vaultix 0.1 sollte nicht ins öffentliche Internet gestellt werden.
