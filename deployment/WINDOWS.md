# Vaultix auf Windows installieren und aktualisieren

## Einmalige Installation

Voraussetzungen: Git, .NET 10 SDK und ein lokaler Klon des Vaultix-Repositorys. PowerShell als Administrator oeffnen und aus dem Repository ausfuehren:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\deployment\Install-Vaultix.ps1
```

Das Skript veroeffentlicht App und Dienst nach `%ProgramFiles%\Vaultix`, registriert `Vaultix.Service` fuer den automatischen Systemstart und erzeugt einen Startmenue-Eintrag. Laufzeitdaten, Queue und Geraetezugangsdaten liegen getrennt unter `%ProgramData%\Vaultix` und bleiben bei Updates erhalten.

Danach Vaultix ueber das Startmenue oeffnen, den HTTPS-Server verbinden und mindestens einen Backup-Ordner hinzufuegen.

## Update aus Git

Vaultix-App schliessen. Dann PowerShell als Administrator im Repository oeffnen:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\deployment\Update-Vaultix.ps1
```

Das Skript verweigert einen unsauberen lokalen Git-Stand, fuehrt `git pull --ff-only origin main` aus, stoppt den Dienst kontrolliert, veroeffentlicht App und Dienst neu und startet den Dienst wieder. Es aendert keine Daten unter `%ProgramData%\Vaultix`.
