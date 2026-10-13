# Neruna Mobile – Planung (Entwurf)

Stand: 13.10.2026. Technik entschieden: .NET + Avalonia (Prototyp gemessen, § 3); offene Entscheide unten (§ 8).

## 1. Ziel

Neruna auf Android und iPhone/iPad – mit denselben Leitplanken wie der Desktop-Client (architecture.md § 1):
Daten bleiben auf dem Mailserver, Neruna hostet keine Nutzdaten, Neruna Cloud ist optional und verwaltet nur
Konfiguration (Signaturen, Textvorlagen, Konten, Zertifikate, Chat).

## 2. Umfang in Phasen

| Phase | Inhalt |
|---|---|
| 1 – MVP | Konten: IMAP/SMTP (Autoconfig), Microsoft 365 · Mail lesen, schreiben, antworten, Ordner, Suche, Anhänge · HTML sicher anzeigen (externe Bilder erst auf Klick) · Benachrichtigungen · Kalender und Kontakte (CalDAV/CardDAV, Graph) lesen und bearbeiten · Offline-Cache · Neruna Cloud verbinden (Code + PIN), Signaturen und Textvorlagen |
| 2 | Konten und S/MIME-Zertifikate aus dem Portal (Zero Knowledge) · S/MIME signieren/verschlüsseln · Abwesenheitsnotiz · Aufgaben · Chat · KI |
| 3 | Tablet-Layouts (Spalten wie am Desktop) · Widgets · Teilen-Erweiterung („Per Neruna senden“) · Standard-Mail-App |

## 3. Grundsatzentscheid: Technik

Was wiederverwendbar ist: Kern, Speicher, Provider und Shared (`Neruna.Core`, `Neruna.Storage`,
`Neruna.Providers.*`, `Neruna.Contracts`, `Neruna.Vault`) – rund 22 000 Zeilen ohne UI-Bezug, mit Tests: IMAP-Sync,
SMTP, Graph, CalDAV/CardDAV, iCalendar/vCard, SQLite-Speicher, Tresor, S/MIME, Cloud-Anbindung und die
Zero-Knowledge-Formate (müssen bitgenau zum Portal passen), KI-Provider. Die Desktop-UI hat rund 18 000 Zeilen.

| Option | Logik | Oberfläche | Aufwand | Bewertung |
|---|---|---|---|---|
| A – nativ getrennt | Kotlin **und** Swift neu (IMAP, DAV, S/MIME, Zero Knowledge …) | Jetpack Compose / SwiftUI | sehr hoch – drei Implementierungen derselben Logik | beste Plattformnähe, aber jede Änderung dreifach |
| B – Kotlin Multiplatform | einmal neu in Kotlin (für Android und iOS) | Compose / SwiftUI | hoch – zweite Implementierung neben C#; kein reifer IMAP-Client für KMP | Logik zweimal (C# + Kotlin) |
| **C – .NET weiterverwenden** | **bestehender C#-Kern** (MailKit/MimeKit laufen auf Android und iOS) | Avalonia (wie Desktop, für Touch neu gestaltet) | mittel – nur UI und Plattformdienste neu | eine Logik für alle drei Clients; gleiche Tests |

**Empfehlung: C.** Der teure, heikle Teil ist nicht die Oberfläche, sondern Sync, Sicherheit und die
Zero-Knowledge-Formate – die gibt es schon, getestet. Mit A oder B müsste jede Korrektur (z. B. am IMAP-Sync oder am
Umschlagformat) zwei- oder dreimal gemacht werden. Avalonia läuft auf Android und iOS; die ViewModels lassen sich
teilweise übernehmen, die Ansichten werden für Touch und kleine Bildschirme neu gebaut.

Kotlin bzw. Swift braucht es bei C nur für kleine plattformeigene Teile, die .NET nicht gut abdeckt:
Notification-Service-Extension (iOS), Widgets, Teilen-Erweiterung. Nachteile von C: grössere App (≈ 30–50 MB),
Bedienelemente nicht „hundertprozentig nativ“, Avalonia-Mobile ist jünger als Compose/SwiftUI.

Ablage (entschieden): eigene Repositories `android/` (NerunaAndroid) und `applemobile/` (NerunaApple). Den
gemeinsamen Kern beziehen sie vorerst als Projekt-Referenz auf `desktop/` (im CI daneben ausgecheckt, Version über
`DESKTOP_REF`); NuGet-Pakete später, wenn sich die Schnittstellen beruhigt haben.

### Leistung von .NET auf Android

- Laufzeit: Neruna ist netz- und datenbanklastig (IMAP, HTTP, SQLite) – dort ist .NET nicht messbar langsamer als
  Kotlin; MailKit/MimeKit sind auf Mobilgeräten verbreitet.
- Kaltstart: spürbar länger als bei Kotlin (Runtime laden); mit AOT-Vorkompilierung und schlankem Start deutlich
  weniger. Muss früh auf einem günstigen Gerät gemessen werden (Ziel: unter 1,5 s bis zur Mailliste).
- Grösse: grösser als eine Kotlin-App (Runtime und Bibliotheken).
- Oberfläche: Avalonia zeichnet selbst (Skia) – flüssig, auf schwachen Geräten beim Scrollen langer Listen aber
  weniger gutmütig als Compose. Ebenfalls früh prüfen (Prototyp: Mailliste mit 5000 Einträgen).

### Prototyp (13.10.2026)

Repository NerunaAndroid, gebaut auf GitHub (signiertes APK). Demo-Konto über den echten Kern (SQLite, MailController).

| Messung | Ergebnis | Ziel |
|---|---|---|
| Prozessstart bis gefüllte Mailliste (Release, AOT) | 1,3 s | unter 1,5 s |
| Liste mit 5000 Einträgen: erzeugen | 36 ms | – |
| Liste mit 5000 Einträgen: scrollen | flüssig | flüssig |
| APK-Grösse (Debug) | 29 MB | – |

Gerät: POCO F8 Ultra (Oberklasse). Offen: dieselben Messungen auf einem günstigen Gerät.

## 3b. Kalender und Kontakte des Systems

Ziel: Termine und Kontakte aus Neruna-Konten erscheinen auch in den Kalender- und Kontakte-Apps des Geräts (und
umgekehrt), z. B. für Anrufer-Erkennung und Erinnerungen des Systems.

- **Android:** vollständig möglich. Neruna legt ein eigenes Konto im System an (`AccountManager`) und stellt Kalender
  und Adressbücher über einen Sync-Adapter in `CalendarContract`/`ContactsContract` bereit (wie DAVx5). Die Daten
  kommen aus Neruna-Konten (CalDAV/CardDAV, Graph); Änderungen in der System-App gehen zurück.
  Sprache: In Kotlin der übliche Weg mit vielen Beispielen; .NET für Android bindet dieselben APIs vollständig
  (`Android.Provider.CalendarContract`, `ContactsContract`, `AbstractThreadedSyncAdapter`) – machbar, aber wenig
  Vorlagen.
- **iOS:** begrenzt, unabhängig von der Sprache. Apps können keine eigene Kalender- oder Kontaktquelle als Konto im
  System anlegen (es gibt keinen Sync-Adapter wie unter Android). Möglich ist nur:
  - Termine und Kontakte über EventKit bzw. Contacts in einen *lokalen* Kalender bzw. in die Kontakte des Geräts
    schreiben (Einbahn, Konflikte selbst lösen) – oder
  - das Konto in den iOS-Einstellungen als CalDAV/CardDAV-Konto einrichten lassen (ein
    Konfigurationsprofil `.mobileconfig` aus Neruna Cloud kann das vorbereiten), dann synchronisiert iOS selbst.
  Für Microsoft 365 hat iOS ein eigenes Exchange-Konto.

## 4. Benachrichtigungen (der heikelste Punkt)

- **Android:** periodischer Abruf (WorkManager, frühestens alle 15 min) oder IMAP IDLE in einem Foreground-Service
  (dauerhafte Benachrichtigung „Neruna empfängt Mails“) – wählbar.
- **iOS:** Apps dürfen im Hintergrund keine Verbindung halten; Hintergrundabruf ist selten und unzuverlässig. Echte
  Benachrichtigungen gehen nur über APNs – also über einen Server, der von neuen Mails erfährt.

Vorschlag: **inhaltsloses Push-Relay in Neruna Cloud** – ohne Zugangsdaten und ohne Inhalte, passend zu „keine
Nutzdaten“:
- Eigene Mailserver (Dovecot): Plugin `push_notification` meldet „neue Mail in Postfach X“ per Webhook an die Cloud;
  die Cloud weckt die Geräte per APNs/FCM, die App holt die Mail selbst.
- Microsoft 365: die App legt selbst eine Graph-Change-Notification an (mit ihrem Token, Ziel = Cloud);
  Graph meldet nur Ressourcen-IDs, die Cloud leitet weiter.
- Ohne Cloud: Abruf beim Öffnen und so oft iOS es zulässt.

## 5. Plattformdienste

| Thema | Android | iOS |
|---|---|---|
| Geheimnisse | Android Keystore | Keychain |
| OAuth (Microsoft) | Custom Tabs, Redirect `msauth://…` | ASWebAuthenticationSession, Redirect `msauth.<bundle-id>://auth` |
| HTML-Mail | WebView, JavaScript aus | WKWebView, JavaScript aus |
| Editor | WebView mit `editor.html` (wie Desktop) | WKWebView mit `editor.html` |
| Hintergrund | WorkManager / Foreground-Service | BGAppRefreshTask, APNs |

- Microsoft: die App-Registrierung „Neruna Desktop“ braucht zusätzliche Redirect-URIs für Mobile (oder eine eigene
  Registrierung „Neruna Mobile“).
- KI-Anmeldung mit ChatGPT: der offizielle Ablauf verlangt eine Rückleitung auf `http://127.0.0.1:{port}` – auf iOS
  mit ASWebAuthenticationSession nicht vorgesehen; zu klären. API-Schlüssel funktionieren überall.

## 6. Verteilung

- **Android:** Google Play (einmalig 25 USD), zusätzlich APK auf GitHub; F-Droid möglich (MPL 2.0 ist frei, alle
  Abhängigkeiten müssen es auch sein).
- **iOS:** App Store und TestFlight (Apple Developer Program, 99 USD/Jahr), Signierung und Notarisierung im CI
  (macOS-Runner wie für den Desktop-Build). MPL 2.0 ist mit dem App Store vereinbar.
- Datenschutzangaben in beiden Stores: keine Datenerhebung durch Neruna; Crash-Berichte nur auf Zustimmung.

## 7. Gemeinsame Verträge

- API-Vertrag `server/contract/openapi.yaml` und die Fixtures gelten auch für Mobile.
- Zero-Knowledge-Formate: Fixtures aus dem Browser-Modul (`server/tests/js/fixtures`) – jeder Client muss sie öffnen.
- Texte: dieselben Kataloge (`Localization/*.json`), Deutsch als Schlüssel.

## 8. Offene Entscheide

| # | Thema | Optionen |
|---|---|---|
| M1 | Technik | ✔ C .NET + Avalonia (Prototyp 13.10.2026) |
| M2 | Push auf iOS | inhaltsloses Relay in Neruna Cloud · nur Abruf – *später* |
| M3 | Ablage | ✔ eigene Repos (NerunaAndroid, NerunaApple) |
| M6 | System-Kalender/-Kontakte | Android: Sync-Adapter · iOS: lokaler Kalender über EventKit oder Konfigurationsprofil |
| M4 | Microsoft-Registrierung | bestehende App um Mobile-Redirects erweitern · eigene „Neruna Mobile“ |
| M5 | Mindestversionen | z. B. Android 10 (API 29) · iOS 17 |
