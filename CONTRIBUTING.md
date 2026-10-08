# Mitwirken an Neruna Desktop

Danke für dein Interesse! Fehlerberichte, Ideen und Pull Requests sind willkommen.

## Vor dem Pull Request

- `dotnet build` ohne Warnungen (`TreatWarningsAsErrors` ist aktiv) und `dotnet test` grün.
- Neue Funktionen mit Tests; Integrationstests gegen das Testlabor (`tools/testlab`) wo sinnvoll.
- Code wie der umliegende: gleiche Benennung, Kommentare erklären das *Warum*.
- UI-Texte vorerst Deutsch (de-CH, «ss» statt «ß»).
- Architektur-Leitplanken in [`docs/architecture.md`](docs/architecture.md) beachten (z. B. Protokollcode nur in Providern,
  keine Passwörter in Datenbank oder Logs).

## Developer Certificate of Origin (DCO)

Mit jedem Beitrag bestätigst du das [Developer Certificate of Origin 1.1](https://developercertificate.org/):
Du hast das Recht, den Beitrag unter der Lizenz dieses Projekts (MPL 2.0) einzureichen. Dazu jeden Commit
mit `-s` signieren:

```sh
git commit -s -m "Kalender: Wochennummern anzeigen"
```

Das fügt `Signed-off-by: Dein Name <deine@adresse>` hinzu.

## Sicherheitslücken

Bitte nicht öffentlich melden – siehe [`SECURITY.md`](SECURITY.md).

## Lizenz und Marke

Beiträge stehen unter der [Mozilla Public License 2.0](LICENSE), wie der Rest des Projekts; das Copyright verbleibt
bei den jeweiligen Autorinnen und Autoren («Neruna contributors»). Name und Logo sind davon getrennt geregelt, siehe
[`TRADEMARKS.md`](TRADEMARKS.md).
