#!/usr/bin/env bash
# Legt im Testlabor für anna@example.com zwei Kalender, ein Adressbuch und ein paar Mails an.
# Aufruf: tools/testlab/seed.sh [host]   (Standard: 127.0.0.1)
set -euo pipefail
HOST="${1:-127.0.0.1}"
USER="anna@example.com"
DAV="http://$HOST:5232/anna@example.com"
AUTH=(-u "$USER:geheim" -s -o /dev/null -w "%{http_code} %{url_effective}\n")

mkcol() { # url type name color
  curl "${AUTH[@]}" -X MKCOL "$1" -H "Content-Type: application/xml" --data \
    "<?xml version=\"1.0\"?><D:mkcol xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" xmlns:A=\"urn:ietf:params:xml:ns:carddav\" xmlns:I=\"http://apple.com/ns/ical/\"><D:set><D:prop><D:resourcetype><D:collection/>$2</D:resourcetype><D:displayname>$3</D:displayname><I:calendar-color>$4</I:calendar-color></D:prop></D:set></D:mkcol>"
}

mkcol "$DAV/kalender/" "<C:calendar/>" "Kalender" "#0F6CBDFF"
mkcol "$DAV/team/" "<C:calendar/>" "Team" "#C239B3FF"
mkcol "$DAV/kontakte/" "<A:addressbook/>" "Kontakte" "#000000FF"

MONDAY=$(date -d "monday this week" +%Y%m%d 2>/dev/null || date -v-monday +%Y%m%d)
curl "${AUTH[@]}" -X PUT "$DAV/kalender/standup.ics" -H "Content-Type: text/calendar" --data-binary $'BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Testlab//EN\r\nBEGIN:VEVENT\r\nUID:standup@testlab\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:'"$MONDAY"$'T070000Z\r\nDTEND:'"$MONDAY"$'T071500Z\r\nRRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR\r\nSUMMARY:Stand-up\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n'
curl "${AUTH[@]}" -X PUT "$DAV/kontakte/marco.vcf" -H "Content-Type: text/vcard" --data-binary $'BEGIN:VCARD\r\nVERSION:3.0\r\nUID:marco@testlab\r\nFN:Marco Bernasconi\r\nN:Bernasconi;Marco;;;\r\nORG:Bernasconi Netzwerke AG\r\nEMAIL;TYPE=INTERNET,WORK:marco@example.com\r\nTEL;TYPE=CELL:+41 79 555 12 34\r\nADR;TYPE=WORK:;;Via Cantonale 1;Lugano;;6900;Schweiz\r\nEND:VCARD\r\n'
curl "${AUTH[@]}" -X PUT "$DAV/kontakte/lea.vcf" -H "Content-Type: text/vcard" --data-binary $'BEGIN:VCARD\r\nVERSION:3.0\r\nUID:lea@testlab\r\nFN:Lea Keller\r\nN:Keller;Lea;;;\r\nEMAIL;TYPE=INTERNET,WORK:lea@example.com\r\nEND:VCARD\r\n'

send() { # from subject body
  curl -s --url "smtp://$HOST:3025" --mail-from "$1" --mail-rcpt "$USER" --user "$1:geheim" -T - <<MAIL
From: $1
To: Anna Muster <$USER>
Subject: $2
Date: $(date -R)
Content-Type: text/plain; charset=utf-8

$3
MAIL
  echo "Mail gesendet: $2"
}
send "marco@example.com" "Offerte Netzwerk-Erneuerung Q4" "Hallo Anna, anbei wie besprochen unsere Offerte. Gruss Marco"
send "lea@example.com" "Teamausflug auf die Rigi" "Ich bin dabei! Soll ich die Tickets reservieren?"
