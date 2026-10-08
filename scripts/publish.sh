#!/usr/bin/env bash
# Baut Neruna Desktop als eigenständige Anwendung (ohne .NET-Installation lauffähig).
# Aufruf: scripts/publish.sh [win-x64|win-arm64|linux-x64|osx-arm64|osx-x64]   (Standard: win-x64)
# Ergebnis: artifacts/neruna-<rid>/ und artifacts/neruna-<rid>.zip
set -euo pipefail
RID="${1:-win-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/neruna-$RID"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1

rm -rf "$OUT" "$OUT.zip"
dotnet publish "$ROOT/src/Client/Neruna.Desktop" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -p:PublishReadyToRun=true -o "$OUT"
rm -f "$OUT"/*.pdb
# Licence texts must ship with the app (MPL 2.0 and the third-party notices required by MIT/BSD/Apache).
cp "$ROOT/LICENSE" "$OUT/LICENSE.txt"
cp "$ROOT/THIRD-PARTY-NOTICES.md" "$OUT/THIRD-PARTY-NOTICES.md"

cat > "$OUT/LIESMICH.txt" <<'TXT'
Neruna Desktop – Testversion
============================

Start:       neruna (Windows: neruna.exe) – keine .NET-Installation nötig
Demo:        neruna --demo          (Beispieldaten, eigener Datenordner)
Fehlersuche: neruna --protocol-log  (IMAP/SMTP/DAV-Mitschnitt, Passwörter geschwärzt)

Daten und Logs:  Windows %LOCALAPPDATA%\Neruna, Linux ~/.local/share/Neruna, macOS ~/Library/Application Support/Neruna
                 (Logs im Unterordner "logs"; Einstellungen → Konten → Diagnose → Öffnen)

S/MIME:      Einstellungen → Zertifikate (S/MIME) → Zertifikat importieren (.p12/.pfx mit Passwort, .cer/.crt/.pem)

Hinweise zur Testversion:
- Nicht signiert: Windows SmartScreen meldet "Unbekannter Herausgeber" → "Weitere Informationen" → "Trotzdem ausführen".
- Passwörter liegen vorläufig verschlüsselt im Datenordner, noch nicht im Schlüsselbund des Betriebssystems.
- HTML-Mails: einfache Darstellung (CSS 2), externe Bilder erst nach Klick auf "Bilder laden".
- Formatiert schreiben braucht WebView2 (Windows 11 vorinstalliert; Windows 10: "WebView2 Runtime" von Microsoft)
  bzw. WebKitGTK unter Linux. Ohne wird als reiner Text geschrieben.
- Einstellungen → E-Mail: Verfassen im Lesebereich oder eigenem Fenster, Standardschrift, wann als gelesen markiert wird.
TXT
(cd "$ROOT/artifacts" && python3 -c "import shutil; shutil.make_archive('neruna-$RID', 'zip', '.', 'neruna-$RID')")
echo "Fertig: $OUT.zip"
