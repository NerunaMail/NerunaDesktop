# Security Policy / Sicherheit

## Reporting a vulnerability

Please **do not** report security issues in public issues or pull requests.

- Preferred: GitHub «Report a vulnerability» (private vulnerability reporting) in this repository, or
- email **info@neruna.org** with the subject «Security».

Please include the affected version, steps to reproduce and, if possible, a proof of concept. We will confirm
receipt, keep you informed about the fix and credit you in the release notes unless you prefer otherwise.
Please give us reasonable time to release a fix before disclosing the issue publicly.

## Supported versions

Neruna Desktop is in beta. Security fixes are made for the latest release only.

## Scope

Especially relevant: handling of passwords and tokens (credential store), S/MIME (signing, encryption,
certificate validation), HTML mail rendering (sanitizing, blocking remote content), the configuration vault
(`Neruna.Vault`) and connections to mail, CalDAV and CardDAV servers (TLS, redirects, authentication).

---

## Deutsch

Sicherheitslücken bitte **nicht** öffentlich melden, sondern über «Report a vulnerability» auf GitHub oder per
E-Mail an **info@neruna.org** (Betreff «Security»), mit betroffener Version und Schritten zum Nachvollziehen. Wir
bestätigen den Eingang, halten dich über die Behebung auf dem Laufenden und nennen dich – wenn du möchtest – in den
Release Notes. Bitte gib uns angemessen Zeit für einen Fix, bevor du die Lücke veröffentlichst. Sicherheitsupdates
gibt es während der Beta nur für die jeweils neueste Version.
