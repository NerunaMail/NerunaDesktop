# Neruna – Architektur

Stand: 2026-10-07 · lebendes Dokument, wird mit dem Code nachgeführt.

## 1. Produkte

| Produkt | Art | Lizenz / Modell | Zweck |
|---|---|---|---|
| **Neruna Desktop** | Desktop-App (Windows, macOS, Linux) | Open Source, gratis | Mail-, Kalender- und Kontakt-Client im vertrauten Büro-Stil für offene Standards |
| **Neruna Cloud** | SaaS, mandantenfähig | Abo pro User/Domain | Zentrale Konfiguration: Autoconfig, Signaturen, Kalender-Abos, Richtlinien, Config-Tresor |
| **Neruna Control** | Self-hosted (Docker) | Abo/Lizenz pro User/Domain | Gleiche Software wie Cloud, im Single-Tenant-Modus beim Kunden |

### Leitplanken

1. **Neruna hostet keine Nutzdaten.** Mails, Kalender und Kontakte bleiben auf den Servern des Kunden
   (SOGo, Nextcloud, Dovecot/Postfix, Mailcow …). Cloud/Control verwalten ausschliesslich *Konfiguration*.
2. **Der Client ist ohne Cloud vollwertig.** Die Cloud ist Mehrwert für Organisationen, nie Voraussetzung.
3. **Zero-Knowledge für Geheimnisse.** Passwörter und Tokens verlassen den Client nur clientseitig verschlüsselt.
4. **Offene Standards vor eigenen Protokollen** (Thunderbird-Autoconfig, RFC 6186/6764, iCalendar, vCard).
5. **Cloud und Control sind eine Codebasis** (`NERUNA_MODE=cloud|control`).
6. **Provider-Architektur:** Mail, Kalender und Kontakte haben je einen eigenen Controller; dahinter hängen
   austauschbare Provider (IMAP, CalDAV, ICS, EWS …). Neue Protokolle erfordern keine Änderung an UI oder Speicher.

Optional später: **Globale Kontakte** als Add-on (zentrales Adressbuch pro Mandant), bewusst als separates,
abschaltbares Modul – die einzige mögliche Ausnahme von Leitplanke 1.

## 2. Technologie

| Teil | Stack |
|---|---|
| Desktop | .NET 10, Avalonia 12 (Fluent), CommunityToolkit.Mvvm, MailKit/MimeKit, Ical.Net, EF Core + SQLite |
| Server | PHP 8.4, Laravel 13, MariaDB 11.8, FrankenPHP (Docker); Admin-UI voraussichtlich Filament |
| Vertrag | `server/contract/openapi.yaml` + Beispieldokumente in `server/contract/fixtures/`, gegen die **beide** Seiten testen (Desktop findet sie als `../server/contract`, sonst werden diese Tests übersprungen) |

## 3. Repository-Struktur

```
api/                      OpenAPI-Vertrag + Fixtures (von C#- und PHP-Tests geprüft)
src/
  Shared/                 → Open Source, nur .NET
    Neruna.Contracts      DTOs der Cloud-API, Autoconfig-Format lesen/schreiben
    Neruna.Vault          Kryptografie des Config-Tresors (auditierbar)
  Client/                 → Open Source
    Neruna.Core           Domänenmodell, Provider-Interfaces, Controller, Discovery, Kontoeinrichtung
    Neruna.Storage        SQLite-Offline-Cache (EF Core), MIME-Dateien, Credential-Store
    Providers/
      Neruna.Providers.Imap   IMAP + SMTP (MailKit)
      Neruna.Providers.Dav    CalDAV + CardDAV (gemeinsame WebDAV-Basis, RFC 4791/6352/6578/6764)
      Neruna.Providers.Ics    ICS/webcal-Abos (read-only)
      Neruna.Providers.Demo   Beispieldaten (--demo, Screenshots)
    Neruna.Desktop        Avalonia-App (Composition Root)
server/                   → proprietär: Laravel-App (Neruna Cloud/Control)
tests/                    .NET-Tests (Server-Tests liegen in server/tests)
tools/                    Neruna.Desktop.Snapshot (headless Screenshots, Live-E2E), testlab/ (GreenMail + Radicale)
scripts/                  publish.sh (eigenständige Builds, auch Windows-Cross-Build)
```

Client und Shared lassen sich später unverändert in ein öffentliches Repo auslagern; `server/` bleibt privat.

## 4. Neruna Desktop

### Controller und Provider

```
            UI (Avalonia, MVVM)
     ┌───────────┼───────────────┐
MailController  CalendarController  ContactController      ← kennen kein Protokoll
     │               │                   │
IMailProvider   ICalendarProvider   IContactProvider        ← Interfaces in Neruna.Core
 ├ imap ✔        ├ caldav ✔          ├ carddav ✔
 ├ ews (später)  ├ ics ✔             ├ ldap (später)
 └ jmap/graph …  └ ews (später)      └ neruna-global (Add-on)
     │               │                   │
IMailStore      ICalendarStore      IContactStore            ← SQLite (Neruna.Storage)
```

- Ein **Konto** (`Account`) hat beliebig viele **Verbindungen** (`ServiceConnection`: Art, Provider-ID, Settings).
  Ein SOGo-Konto hat typischerweise Mail + Kalender + Kontakte; ein «Internet-Kalender» nur eine ICS-Verbindung.
- Die `ProviderRegistry` löst Provider über ihre ID auf. Ein neuer Provider = neues Projekt + eine Zeile in
  `AppServices.cs`. Verbindungen, deren Provider fehlt, werden beim Sync übersprungen (nicht als Fehler gemeldet).
- **Kanonische Formate** zwischen Provider und Speicher: MIME (Mail), iCalendar (Kalender), vCard (Kontakte).
  IDs und Sync-States sind opake Strings des Providers (IMAP: `UIDVALIDITY:UIDNEXT`, ICS: Inhalts-Hash,
  DAV: `sync:<token>` bzw. `ctag:`/`etags:` als Fallback).
- **Schreiben** geht immer über den Controller: Provider schreibt mit ETag-Prüfung (`If-Match`/`If-None-Match`),
  danach aktualisiert der Controller den lokalen Speicher. Ein geänderter Server-Stand führt zu
  `RemoteConflictException` statt zu stillem Überschreiben.
- **Bearbeiten erhält Fremddaten:** `EventDraft` und `ContactDraft` ändern nur ihre Felder; Teilnehmer, Alarme,
  Serien-Ausnahmen, Fotos, Adressen und Hersteller-Properties bleiben erhalten. Serien werden als Ganzes bearbeitet.
- Bei der Kontoeinrichtung fragt `AccountSetupService` jeden registrierten Provider, welchen Teil der gefundenen
  Konfiguration er übernehmen kann (`SettingsFromDiscovery`). Sobald ein CalDAV-Provider registriert ist,
  bekommen neue Konten automatisch auch Kalender.

### Offline-Datenhaltung

- `neruna.db` (SQLite, WAL): Konten, Verbindungen, Ordner, Nachrichten-Metadaten, Kalender- und Kontaktobjekte.
- Rohe MIME-Nachrichten als `.eml` unter `messages/<verbindung>/<ordner-hash>/`.
- Passwörter (Konten, Zertifikate) nie in SQLite, sondern im Schlüsselbund des Systems (`Neruna.Storage/Credentials`):
  Windows-Anmeldeinformationsverwaltung (`Neruna/<id>`), macOS-Schlüsselbund (Dienst «Neruna»), Linux Secret Service
  über libsecret (Schema `org.neruna.Secret`). Ohne Schlüsselbund (Linux ohne Secret Service) bleibt die verschlüsselte
  Datei `credentials.json` (AES-GCM, Schlüssel in benutzerlokaler Datei); Einstellungen → Info zeigt, welcher Speicher
  verwendet wird. Beim Start wandern Passwörter aus `credentials.json` in den Schlüsselbund (gegengelesen, dann wird
  die Datei gelöscht). Tests verwenden immer die Datei; `NERUNA_TEST_KEYRING=1` prüft den echten Schlüsselbund.
- Schema per EF-Core-Migrations (siehe «Schema-Updates»).

### Nachrichtendarstellung

- `MessageContent` zerlegt eine Nachricht strukturell: Hauptinhalt (HTML vor Text, auch in `multipart/related`),
  eingebettete Bilder (`cid:`-Referenzen) und **alle** echten Anhänge – auch PDFs mit `Content-Disposition: inline`,
  Kalender-Einladungen, angehängte Mails und `winmail.dat` (TNEF, wird entpackt).
- HTML wird per Whitelist-Callback bereinigt (keine Skripte, Event-Handler, Formulare, Frames, `<meta>/<link>/<base>`)
  und mit **Avalonia.HtmlRenderer** dargestellt (rein managed, kein JavaScript). Externe Bilder und Stylesheets werden
  standardmässig nicht geladen («Bilder laden» pro Nachricht); blockierte Bilder werden durch ein transparentes Pixel ersetzt.
- Reiner Text wird als ein `<div>` pro Zeile dargestellt (Leerzeichenfolgen in `white-space: pre`-Spans, Links
  anklickbar), bewusst ohne `white-space: pre-wrap` – HtmlRenderer setzt das uneinheitlich um (unter Windows landeten
  ganze Mails auf einer Zeile). format=flowed wird vorher zusammengefügt, die Signaturtrennlinie `-- ` bleibt erhalten.
- Ebenso werden `<pre>`-Blöcke in HTML-Mails (Thunderbird: Text-Signatur `pre.moz-signature`, Zitate `pre.moz-quote-pre`)
  in explizite Zeilen umgewandelt.
- Grenzen: CSS-Unterstützung etwa CSS 2 (moderne Newsletter mit Flexbox werden einfacher dargestellt). Option für später:
  dieselbe native WebView wie beim Verfassen, mit dediziertem Sanitizer und CSP.

### Verfassen (HTML-Editor)

- `Editor/editor.html`: `contenteditable`-Seite mit kleiner JS-API (`window.neruna`: `setContent`, `getHtml`, `exec`,
  `font`, `size` in pt, `color`, `highlight`, `link`, `state`), CSP mit Nonce, keine Netzwerkzugriffe. Läuft in
  `NativeWebView` (Avalonia.Controls.WebView, MIT): WebView2 unter Windows, WebKit unter macOS/Linux. Navigation und
  neue Fenster sind gesperrt. Ohne WebView-Laufzeit (oder nach 12 s ohne «ready») übernimmt ein reines Textfeld –
  die Formatierungsleiste wird ausgeblendet, gesendet wird Text.
- Die Avalonia-Werkzeugleiste (Schriftart mit Vorschau in der Schrift selbst, Grösse 8–72 pt, F/K/U/abc, Schrift- und
  Hervorhebungsfarbe, Listen, Einzug, Ausrichtung, Link, Formatierung löschen) steuert den Editor per Skript; der Editor
  meldet die Formatierung an der Schreibmarke zurück (`state`), damit die Knöpfe den Zustand spiegeln.
- `MessageComposer`: Antworten/Weiterleiten als HTML im gewohnten Geschäfts-Stil (Kopfblock Von/Gesendet/An/Cc/Betreff, darunter
  das Original mit Formatierung; eingebettete Bilder als `data:`-URI). Beim Senden werden `data:`-Bilder zu
  eingebetteten Teilen (`cid:`) und es entsteht automatisch eine Textalternative (`multipart/alternative`).
- Inline im Lesebereich oder als eigenes Fenster (Einstellung «E-Mail → Verfassen»); «Loslösen» übergibt den Entwurf
  als HTML an ein neues Fenster.
- Tests: `tests/editor` (Playwright, Chromium + WebKit) für die JS-API; die WebView selbst ist headless nicht prüfbar.

### Entwürfe

- `MailController.SaveDraftAsync`: legt die Nachricht per IMAP APPEND (`\Draft \Seen`) in «Entwürfe» ab – fehlt der
  Ordner, wird `Drafts` angelegt – und löscht erst danach die vorherige Fassung. Die Message-ID bleibt über alle
  Speicherungen gleich; daran wird die neue Kopie wiedergefunden. Entwürfe werden ohne S/MIME abgelegt.
- Gespeichert wird mit «Speichern»/Ctrl+S, automatisch alle 30 s bei Änderungen und beim Verlassen (andere Nachricht
  öffnen, Fenster schliessen): der Text wird sofort übernommen, das Speichern läuft im Hintergrund. Geändert heisst:
  vom Benutzer getippt (Editor-Ereignis `ContentChanged`), nicht nur geöffnet.
- «Verwerfen» fragt einmal nach und löscht auch den gespeicherten Entwurf; nach dem Senden wird er gelöscht.
- Beim Beenden mit ungespeicherten Entwürfen (Lesebereich oder eigene Fenster) fragt Neruna «Speichern / Nicht
  speichern / Abbrechen»; «Speichern» wartet, bis die Entwürfe auf dem Server liegen (`PrepareCloseAsync`).
- In «Entwürfe» öffnet «Bearbeiten» oder Doppelklick den Entwurf wieder (`MessageComposer.FromDraft`: Empfänger,
  Text mit Bildern, Anhänge). Derselbe Entwurf ist höchstens einmal offen.

### Nachricht im eigenen Fenster

Doppelklick in der Liste öffnet die Nachricht in einem eigenen Fenster (`MessageWindowViewModel`, gleiche
`ReadingPaneView` wie der Lesebereich) mit Antworten/Allen antworten/Weiterleiten/Löschen; Antworten daraus öffnen
ebenfalls ein eigenes Fenster. Das Fenster lädt die Nachricht selbst und bleibt offen, wenn man in der Liste weitergeht.

### Erinnerungen

- Aus den Alarmen (VALARM) der Termine selbst (`ReminderService`): funktioniert auch für Termine aus dem Webmail
  oder anderen Programmen, bei Serien pro Vorkommen; mehrere Alarme eines Vorkommens ergeben eine Erinnerung.
  E-Mail-Alarme des Servers werden ignoriert. Erinnerungen zu Terminen, die schon vorbei sind, erscheinen nicht.
- Geschlossen/geschlummert wird lokal gespeichert (Tabelle `ReminderStates`, Migration `AddReminderStates`);
  die Alarme auf dem Server bleiben unverändert. Abgelaufene Einträge werden täglich entfernt.
- `ReminderScheduler` prüft alle 30 s und nach jedem Abgleich; das Erinnerungsfenster erscheint bei jeder neu fälligen
  Erinnerung (Schliessen, Alle schliessen, Element öffnen, Erneut erinnern in 5 min … 1 Tag oder «bei Beginn»).
- Standard-Erinnerung (Einstellungen → Kalender, voreingestellt 15 min) für neue Termine und angenommene Einladungen.
  Im Editor wird ein vorhandener Alarm nur angefasst, wenn die Erinnerung geändert wird.

### Einladungen (iTIP/iMIP)

- Termin-Editor: Teilnehmer per E-Mail-Adresse; der eigene Kontoname wird Organisator. Beim Speichern gehen
  Einladungen bzw. Aktualisierungen an alle, Absagen an Ausgeladene; beim Löschen eine Absage (`InvitationService`).
  Löscht ein Teilnehmer eine Besprechung, erhält der Organisator «abgelehnt» (`SendDeclineAsync`); die Rückfrage
  sagt jeweils, dass Mail verschickt wird («Löschen und absagen?»).
- Server mit eigener Terminplanung (DAV-Header `calendar-auto-schedule`, z. B. SOGo, Nextcloud) verschicken das selbst;
  Neruna ändert dann nur den Kalender, sonst (Radicale …) sendet Neruna die Mails über die Mail-Verbindung des Kontos.
- Mails mit `text/calendar` und METHOD zeigen über dem Text einen Balken: Einladung (Annehmen/Vorläufig/Ablehnen,
  Kalenderwahl, Überschneidungen, veraltete Fassung), Absage («Aus dem Kalender entfernen»), Antwort (wird sofort im
  eigenen Termin eingetragen). Angenommene Einladungen kommen in den eigenen Standardkalender des Kontos
  (zuletzt gewählter, sonst ein Kalender im eigenen Ordner, bevorzugt `…/personal/`).

### Benachrichtigungen

Eigene kleine Fenster unten rechts auf dem Bildschirm von Neruna (`NotificationService`, plattformunabhängig, ohne
Fokus zu nehmen): Konto, Absender, Betreff, Vorschau; Klick öffnet die Nachricht, nach 8 s (nicht solange die Maus
darauf ist) schliessen sie sich, höchstens drei; bei mehr als drei neuen Mails eine Sammelmeldung. Nur wenn Neruna
nicht gerade genau dieser Posteingang vorne offen ist. Einstellungen → E-Mail: «Neue E-Mails sofort abrufen»,
«Desktop-Benachrichtigung» und «Test-Benachrichtigung anzeigen».

### Mailansicht

«Neue E-Mail» steht über der Ordnerliste; die Aktionen zur Nachricht (Antworten … Gelesen) über dem Lesebereich als
`ToolButton` (Symbol, darunter Text; Einstellungen → Design: «Nur Symbol», dann Text als Tooltip).

### Fenster und Spalten

`UiLayout` merkt sich Position, Grösse und Maximiert-Zustand des Hauptfensters, die Grösse der Fenster «Verfassen» und
«Nachricht» sowie die Spaltenbreiten von Mail, Kontakten und Kalender (Einstellung `ui.layout`, gesammelt gespeichert).
Eine Position, die auf keinem angeschlossenen Bildschirm mehr liegt, wird ignoriert (zentriert).

### Signaturen

- `SignatureService` (Core): Signaturen als HTML (Bilder als `data:`-URI) in SQLite (`Signatures`), Zuordnung pro
  Konto für «Neue Nachrichten» und «Antworten/Weiterleitungen» im Settings-Store. `Signature.Source` trennt eigene
  Signaturen von späteren zentralen aus Neruna Cloud (Vorlagen mit Platzhaltern).
- `SignatureBlock.Apply`: Platz zum Schreiben, darunter `<div id="neruna-signature">`, darunter das Zitat.
  Der Container wird immer eingefügt, damit «Signatur» beim Schreiben wechseln/entfernen kann (`neruna.setSignature`);
  in zitierten Neruna-Mails wird die Markierung entfernt.
- Einstellungen → Signaturen: Liste, Vorschau (wie beim Empfänger), Editor in eigenem Fenster mit derselben
  Formatierungsleiste wie beim Verfassen (inkl. Bild/Logo).

### Datumsnavigator

Links über der Kalenderliste ein oder zwei Monate (Einstellungen → Kalender) mit ISO-Kalenderwochen; heute ist
hervorgehoben, die angezeigte Woche hinterlegt, Tage mit Terminen fett. Klick auf Tag oder Kalenderwoche zeigt diese
Woche; der Navigator folgt der Woche, solange man nicht selbst Monate blättert.

### Dunkles Design und Mailinhalt

`MailPaper` entscheidet, wie Mailinhalt im dunklen Design erscheint: Text- und einfach formatierte Mails dunkel (schwarze
und graue Schriftfarben fallen weg, dunkle Farben werden aufgehellt), gestaltete Mails mit eigenen Hintergründen
(Newsletter) bleiben auf hellem Papier. Der Editor zeigt seine Seite im dunklen Design invertiert (Bilder
zurückinvertiert); die versendete Mail bleibt unverändert dunkle Schrift auf Weiss.

### Neruna Cloud verbinden

Einstellungen → Cloud: Server, Verbindungscode und PIN aus dem Portal. `CloudController` (Neruna.Core/Cloud) erzeugt dabei
ein Schlüsselpaar (ECDSA P-256); der private Schlüssel liegt im Schlüsselbund des Betriebssystems (`ICredentialStore`,
Schlüssel `CloudController.KeyId`), der Server kennt nur den öffentlichen Teil. Mitgeschickt werden Gerätename,
Betriebssystem und -version, angemeldeter Benutzer und Neruna-Version (im Portal pro Gerät sichtbar). Danach meldet sich
die App mit einer signierten JWT-Assertion (ES256, wie OAuth private_key_jwt) an und erhält ein 15-Minuten-Token.
Verbindung (Server, Geräte-ID, Organisation) steht in den Einstellungen `cloud.connection`; ist sie gesetzt, fragt auch
die Kontoeinrichtung zuerst den Server der Organisation (`AccountDiscovery.OrganizationServer`). Nur HTTPS, ausser
`localhost` zum Entwickeln. Vertrag: `server/contract/openapi.yaml` (enrollment, token, me).

### Kalendernamen und -farben

Farbe und Anzeigename eines Kalenders lassen sich über das Farbfeld in der Kalenderliste ändern. Beides wird nur in
Neruna gespeichert (Einstellungen `calendar.color.*`, `calendar.name.*`); der Server behält seine Angaben.
`CalendarController.GetCalendarsAsync` liefert den eigenen Namen (`CalendarInfo.ServerName` = Name auf dem Server),
damit er überall gleich erscheint (Liste, Termin-Editor, «Eintragen in» bei Einladungen).

### Wochenansicht

Einstellung «Kalender → Darstellung»: Liste (Termine pro Tag untereinander) oder Zeitraster
(`TimedEventItem.Layout`: Termine auf den Tag zugeschnitten, überlappende in Spalten nebeneinander, `TimeGridPanel`
platziert sie; ganztägige Termine in eigener Zeile, rote Jetzt-Linie, Start um 07:00, Doppelklick legt einen Termin an).

### Kalender auswählen

`CalendarController.DiscoverAsync` fragt den Server erneut nach allen Kalendern (eigene, geteilte, im Webmail
abonnierte), `SetSelectionAsync` speichert pro Verbindung die Auswahl und die angebotenen Kalender. Ohne Auswahl werden
wie bisher alle Kalender synchronisiert; mit Auswahl erscheinen neue Server-Kalender nicht von selbst, sondern im Dialog
«Kalender verwalten» als «neu». Abgewählte Kalender werden lokal entfernt (nicht auf dem Server).
SOGo listet im Webmail abonnierte Kalender nur Clients, deren User-Agent «Thunderbird» enthält (an einem echten
SOGo-Server nachgewiesen); Neruna fragt Kalender-Ordner deshalb als `Neruna/0.1 (compatible; Thunderbird/128.0)` ab,
alle anderen Anfragen als `Neruna/0.1`. Listet ein Server einen Kalender trotzdem nicht, kann er im Dialog per
CalDAV-Adresse hinzugefügt werden (`AddByUrlAsync`, gespeichert pro
Verbindung, bei jeder Synchronisation mit abgefragt). Der Dialog zeigt, welcher Kalender-Ordner mit welchem Benutzer
abgefragt wurde.
Ist im Konto die URL eines einzelnen Kalenders hinterlegt (SOGo für Thunderbird: `…/Calendar/personal/`), sucht die
Erkennung trotzdem über `current-user-principal` → `calendar-home-set` alle Kalender des Benutzers.

### S/MIME beim Verfassen

Signieren wird automatisch eingeschaltet, wenn für den Absender ein eigenes Zertifikat vorhanden ist; Verschlüsseln,
solange für alle Empfänger (und den Absender, wegen der lesbaren Kopie in «Gesendet») Zertifikate bekannt sind – die
Prüfung läuft beim Tippen der Empfänger mit. Manuelles Umschalten hat Vorrang; beides lässt sich unter
Einstellungen → Zertifikate abschalten. Antworten auf verschlüsselte Mails bleiben verschlüsselt.

### Start

Splashscreen (Name, Version, Spinner, Status) bis Datenbank, Konten und Hauptfenster geladen sind; die Datenbank wird
im Hintergrund geöffnet, die Synchronisation startet erst, wenn das Hauptfenster sichtbar ist.

### Kontakte, Gruppen, Bilder

- Links die Adressbücher (ein-/ausblenden, Farbe lokal wählbar), in der Liste Farbstreifen und Avatar in der Farbe
  des Adressbuchs; rechts Details. «E-Mail schreiben» an einen Kontakt oder an alle Mitglieder einer Gruppe.
- «Adressbücher verwalten» fragt jeden CardDAV-Server neu ab und wählt, welche Adressbücher Neruna überhaupt
  synchronisiert (`ContactController.DiscoverAsync`/`SetSelectionAsync`, Schlüssel `addressbooks.selected|known.{connId:N}`).
  Ohne Auswahl wird alles übernommen; nach einer Auswahl erscheinen neue Adressbücher nur noch als «neu» im Dialog.
  Abgewählte werden lokal entfernt, nicht auf dem Server. Typischer Fall: zwei Konten derselben Firma bieten beide das
  Domain-Adressbuch an. Das Ein-/Ausblenden per Checkbox in der Liste ist davon getrennt (nur Anzeige, bleibt synchronisiert).
- Bei mehreren Konten steht unter jedem Adressbuch das Konto, zu dem es gehört.
- Gruppen-Editor zweispaltig: links die Mitglieder (bei mehreren Adressen Auswahl, welche die Gruppe nutzt), rechts
  die durchsuchbaren Kontakte mit E-Mail-Adresse, die noch nicht Mitglied sind («Hinzufügen»). Schreibgeschützte
  Gruppen zeigen nur die Mitglieder.
- Gruppen (`GroupDraft`): neu als vCard 3.0 mit `X-ADDRESSBOOKSERVER-KIND:group` und
  `X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:<UID>` (Apple/SOGo); vCard-4-Gruppen behalten `KIND:group`/`MEMBER`.
  Die pro Mitglied gewählte Adresse steht im Parameter `X-NERUNA-EMAIL="…"` – andere Programme ignorieren ihn und
  nehmen die Standardadresse. Adressen ohne Kontakt sind `mailto:`-Mitglieder. `ContactController.ResolveMembersAsync`
  löst Mitglieder über alle Adressbücher per UID auf.
- Bilder: `PHOTO` (vCard 3 `ENCODING=b`, vCard 4 `data:`-URI) wird gelesen und angezeigt; ein neues Bild wird
  zentriert quadratisch zugeschnitten und als 256×256-JPEG gespeichert (`AvatarImage`, SkiaSharp). Ohne Änderung
  bleibt ein vorhandenes PHOTO unverändert (auch verlinkte Bilder).

### Design

Einstellungen → Design: Hell/Dunkel/wie System und Farbschema Blau/Rot/Grün (`Appearance`): setzt die Fluent-Akzentfarbe
(Knöpfe, Auswahl) und Nerunas Akzent-Pinsel (Kopfleiste, Ungelesen-Markierung, «heute») für beide Varianten, sofort
und beim Start. Kalenderfarben sind pro Kalender lokal wählbar (Klick auf das Farbfeld), ohne den Server zu ändern;
das Zeitraster lässt sich in 60/30/15 Minuten unterteilen.

### Drag & Drop und Mehrfachauswahl

- Mehrfachauswahl in der Nachrichtenliste (Ctrl/Shift-Klick, Ctrl+A); Löschen, Archivieren, Kennzeichnen,
  Gelesen/Ungelesen und Verschieben wirken auf alle ausgewählten Nachrichten (`MailViewModel.ActionTargets`).
- Nachrichten auf einen Ordner im Baum ziehen verschiebt sie. Der Zug startet erst nach einigen Pixeln Bewegung;
  Drücken auf eine bereits ausgewählte Nachricht behält die Mehrfachauswahl (wie im Datei-Explorer).
- Im selben Konto: `MailController.MoveAsync` (IMAP MOVE, ein Befehl für alle).
- In ein anderes Konto: `MailController.MoveToAccountAsync` – pro Nachricht laden, im Ziel ablegen
  (`IMailProvider.AppendAsync`, IMAP APPEND mit Flags und Empfangsdatum) und erst danach in der Quelle endgültig
  löschen. Ein Abbruch hinterlässt höchstens ein Duplikat, nie einen Verlust; die Liste wird dann neu geladen.

### Konten

Reihenfolge frei wählbar (Einstellungen → Konten ↑/↓ oder Kontextmenü im Ordnerbaum), gespeichert als `SortOrder`;
neue Konten kommen ans Ende. Ordner zeigen Symbole (eigene für Systemordner).

### Gelesen-Status

Einstellung «E-Mail → Lesebereich»: beim Anklicken (Standard), nach 10 s im Lesebereich (Wechsel zu einer anderen
Nachricht bricht ab), erst beim Antworten/Weiterleiten oder nie automatisch. Antworten/Weiterleiten markiert in allen
Modi ausser «nie» als gelesen. Die Ordnerliste zeigt ungelesene Nachrichten als «Posteingang (3)».

### S/MIME

- `CertificateManager`: Import von `.p12/.pfx` (privater Schlüssel), `.cer/.crt/.der/.pem/.p7b`; Übersicht mit E-Mail,
  Status (gültig / läuft in < 30 Tagen ab / abgelaufen / noch nicht gültig), Ablaufdatum, Verwendung, Aussteller,
  SHA-1-Fingerabdruck. PKCS#12-Container bleiben mit ihrem Passwort verschlüsselt in SQLite, das Passwort liegt im
  Credential-Store.
- `SecureMimeService` (MimeKit + BouncyCastle): klar signieren (für alle Clients lesbar), verschlüsseln (AES-256,
  immer auch an den Absender, damit «Gesendet» lesbar bleibt), entschlüsseln, Signaturen prüfen in drei Stufen
  (gültig / gültig aber Aussteller nicht vertrauenswürdig / ungültig), Abgleich Signierer ↔ Absender.
- Vertrauensanker: Stammzertifikate des Betriebssystems plus selbst importierte Zertifizierungsstellen (Firmen-CAs).
- Zertifikate aus gültig signierten Mails werden automatisch übernommen.
- Noch nicht: Online-Sperrprüfung (CRL/OCSP), Auswahl bei mehreren eigenen Zertifikaten pro Adresse (heute: das am
  längsten gültige), LDAP-Zertifikatsuche, Speichern privater Schlüssel im OS-Schlüsselbund.

### Schema-Updates

EF-Core-Migrations in `Neruna.Storage/Migrations`; beim Start bringt `InitializeNerunaStorageAsync` die Datenbank auf
den neuesten Stand.

- **Modell geändert** → in `desktop/`: `dotnet tool restore` (einmalig), dann
  `dotnet ef migrations add <Name> --project src/Client/Neruna.Storage`. Der Test
  `SchemaUpgradeTests.Every_model_change_has_a_migration` schlägt fehl, wenn die Migration vergessen wurde.
- Vor jeder Änderung an einer bestehenden Datenbank legt Neruna eine Kopie an (`neruna.db.bak-<Datum>`, per
  `VACUUM INTO`, die drei neuesten bleiben).
- **Beta-Datenbanken von vor den Migrations** (mit `EnsureCreated` angelegt, ohne `__EFMigrationsHistory`) ergänzt
  `LegacyDatabase` auf den Stand von `InitialCreate` (nur fehlende Tabellen, Spalten, Indizes – gemäss den Schritten
  der Migration selbst, nicht dem aktuellen Modell) und markiert sie als migriert; danach laufen alle weiteren
  Migrations normal. Kann entfernt werden, sobald keine solchen Installationen mehr existieren.
- Einmalige Datenkorrekturen ohne Schemaänderung: `UpgradeDataAsync` (Version in den Einstellungen); künftig besser
  als SQL in einer Migration.

### Diagnose

Logdateien pro Tag unter `<Datenordner>/logs`. `--protocol-log` schreibt zusätzlich MailKit-Protokolle
(`logs/protocol/`, Passwörter geschwärzt) und DAV-Antworten auf Trace-Level.

### IMAP-Synchronisation (Stand)

- Ordner inkl. Special-Use (RFC 6154); Erstsync lädt die neuesten 2000 Nachrichten.
- Inkrementell: neue UIDs ab `UIDNEXT`, Flags aller bekannten UIDs, Löschungen per Abgleich; `UIDVALIDITY`-Wechsel
  ersetzt den Ordnerinhalt.
- Gesendete Nachrichten werden (falls SPECIAL-USE) in «Gesendet» abgelegt.
- Verschieben (MOVE bzw. COPY+EXPUNGE), Löschen (Papierkorb bzw. `UID EXPUNGE`), Flags, Senden + Ablage in «Gesendet».
- Als `\Deleted` markierte, noch nicht endgültig entfernte Nachrichten gelten als gelöscht (manche Programme löschen so
  und bereinigen den Ordner erst später); wird die Markierung aufgehoben, erscheinen sie wieder.
- Ordnerrollen per SPECIAL-USE, sonst anhand üblicher Namen (Drafts/Entwürfe, Sent/Gesendet, Trash/Papierkorb …).
- Push (`MailPushService`): pro Konto eine eigene Verbindung im IMAP IDLE auf dem Posteingang
  (`IMailProvider.WaitForChangesAsync`, IDLE wird alle 25 min erneuert; ohne IDLE alle 2 min abfragen). Meldet der
  Server etwas, wird der Posteingang synchronisiert; neue ungelesene Mails (nicht eigene, nicht die beim Start
  nachgeholten) lösen `NewMail` aus. Verbindungsabbrüche: neuer Versuch nach 30 s, verdoppelt bis 5 min. Die übrigen
  Ordner synchronisiert weiterhin der 5-Minuten-Abgleich. `MailController` synchronisiert einen Ordner nie doppelt
  gleichzeitig (Sperre pro Ordner).
- Nach einem Abgleich aktualisiert die Mail-Seite Zähler und Liste an Ort und Stelle (`RefreshAfterSyncAsync`):
  Auswahl, Lesebereich und ein angefangener Entwurf bleiben; der Baum wird nur neu aufgebaut, wenn sich Konten oder
  Ordner geändert haben.
- Erstsync: die neuesten 2000 Nachrichten pro Ordner. Ältere lädt `MailController.LoadOlderAsync` in Portionen
  (`IMailProvider.FetchOlderAsync`; der Anbieter entscheidet anhand der bekannten IDs, was «älter» ist), ohne den
  Sync-Stand zu ändern. Die Liste zeigt 500 auf einmal und lädt am Ende selbst nach («x von y Nachrichten»).
- Suche: Die Schnellsuche filtert sofort, was geladen ist; Enter bzw. «Im ganzen Ordner auf dem Server suchen» sucht
  per IMAP SEARCH (TEXT: Kopf und Text). Die erweiterte Suche (Von, Betreff, Text, Ordner oder alle Ordner eines Kontos,
  mit/ohne Unterordner) läuft auf dem Server (`MailController.SearchAsync`, je Ordner die neuesten 300 Treffer);
  Anbieter ohne Server-Suche durchsuchen die gespeicherten Kopfdaten. Treffer kennen ihren Ordner – Aktionen darauf
  wirken im jeweiligen Ordner. Hinweis: GreenMail (Testlabor) vergleicht FROM nur mit ganzen Adressen.
- Ausbau: CONDSTORE/QRESYNC (RFC 7162), Verbindungs-Pooling (heute eine Verbindung pro Aktion).

### CalDAV / CardDAV

- Discovery ab beliebiger URL: Server-Root, `/.well-known/caldav|carddav`, Principal, Home-Set oder direkt eine Sammlung
  (`current-user-principal` → `calendar-home-set`/`addressbook-home-set` → Sammlungen mit Name, Farbe, Schreibrecht).
- Sync per `sync-collection` (RFC 6578) mit Multiget in 50er-Blöcken; Fallback CTag + ETag-Vergleich für ältere Server.
- Redirects folgt der Client selbst (Anmeldedaten nur an denselben Host), lesende Anfragen werden bei abgebrochenen
  Verbindungen bis zu 3× wiederholt (PUT bewusst nicht).
- Getestet gegen Radicale (Integrationstests). SOGo und Nextcloud: manuell zu testen.

### Konto-Erkennung

1. Neruna-Server der Organisation (`/api/v1/discovery`), sofern der Client angemeldet ist. *(DNS-TXT `_neruna.<domain>` folgt)*
2. Thunderbird-Autoconfig beim Provider (`autoconfig.<domain>`, `<domain>/.well-known/…`)
3. Mozilla ISPDB (erfährt nur die Domain)
4. CalDAV/CardDAV zusätzlich über `/.well-known` auf Mail-Domain und IMAP-Host
5. DNS-SRV (RFC 6186/6764) *(folgt)*
6. Raten + Verbindungstest (heute: Vorschlag `mail.<domain>`, Benutzer kann alles anpassen)

### UI

Klassisches Groupware-Muster: Navigationsleiste (Mail/Kalender/Kontakte), Ordnerbaum, Nachrichtenliste nach Datum gruppiert
(Heute/Gestern/Diese Woche …), Lesebereich mit Inline-Verfassen, Wochenansicht über alle Kalender,
Kontaktliste mit Detailansicht. Screenshots: `docs/screenshots/` (erzeugt mit `tools/Neruna.Desktop.Snapshot`).

## 5. Neruna Cloud / Control (Laravel)

| Bereich | Stand |
|---|---|
| Mandanten, Domains, Server-Profile | ✔ Datenmodell (ULIDs), Domain-Verifikation per TXT-Token vorbereitet |
| Discovery-API `/api/v1/discovery` | ✔ inkl. Vertragstest |
| Thunderbird-Autoconfig (beide Pfade) | ✔ inkl. Vertragstest |
| Admin und Kundenportal | ✔ Filament 5: `/admin` (Mandanten, Lizenz, Limits, Addons, Logins), `/portal` (Firmendaten, Benutzer, Verbindungscodes, Geräte) |
| Signaturvorlagen, Kalender-Abos, Richtlinien | offen |
| Config-Tresor-Ablage | offen (Server speichert nur `VaultEnvelope`-JSON) |
| Geräte verbinden | ✔ Einmal-Code + separate PIN, Geräteschlüssel (ECDSA P-256), signierte Anmeldung, Tokens; Geräte im Portal sperrbar |
| Betrieb | ✔ Shared Hosting (PHP 8.4+, MySQL/MariaDB, ohne Worker/Redis/Node), Paket per `scripts/package-shared-hosting.sh` |

Server-Profile beschreiben die Endpunkte beim Kunden (IMAP/SMTP/CalDAV/CardDAV). Für EWS & Co. wird das Profil
später um weitere Diensttypen ergänzt – analog zu den Client-Providern.

## 6. Config-Tresor (Zero-Knowledge)

```
Tresor-Passwort ──Argon2id(salt, 64 MiB, t=3, p=1)──► KEK
Recovery-Code (256 bit, Crockford-Base32) ──HKDF──► KEK₂
DEK (256 bit, zufällig)
  ├─ AES-256-GCM(KEK)  → wrappedKey[password]
  ├─ AES-256-GCM(KEK₂) → wrappedKey[recovery]
  └─ AES-256-GCM(DEK, Payload, AAD = Version|Tresor-ID)
```

- Server speichert nur den Envelope; Passwortwechsel = neues Wrapping, Payload unverändert.
- KDF-Parameter und Schlüsselart sind als AAD gebunden; zu schwache Parameter werden beim Entsperren abgelehnt.
- Optionaler Admin-Escrow-Schlüssel der Organisation (X25519) als weiterer Wrap: Entscheid offen.
- Implementiert und getestet in `Neruna.Vault`; Anbindung an Client-UI und Server folgt.

## 7. Offene Entscheide

| # | Thema | Optionen |
|---|---|---|
| 1 | Lizenz Client/Shared | ✔ MPL 2.0 (Beiträge mit CLA) |
| 2 | Endbenutzer-Auth gegenüber Cloud | ✔ Geräte-Code aus dem Portal + PIN, danach Geräteschlüssel |
| 3 | Admin-Escrow für Tresor | ja/nein, optional pro Mandant |
| 4 | Preismodell | pro User · pro Domain · Staffeln |
| 5 | UI-Sprachen | heute nur Deutsch; `.resx` de/en vor Release |
| 6 | HTML-Darstellung von Mails | ✔ Lesen: Avalonia.HtmlRenderer; Verfassen: native WebView (WebView2/WebKit). Avalonias RichTextEditor ist kommerziell → nicht verwendet |
| 7 | Admin-UI Server | ✔ Filament 5 |
| 8 | Produkt-Domain | ✔ neruna.org |
