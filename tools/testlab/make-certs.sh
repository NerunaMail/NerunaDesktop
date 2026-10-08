#!/usr/bin/env bash
# Erzeugt S/MIME-Testzertifikate für das Testlabor (Passwort der .p12-Dateien: "geheim").
#   tools/testlab/certs/testlab-ca.crt          → als Zertifizierungsstelle importieren
#   tools/testlab/certs/<name>.p12              → eigenes Zertifikat (privater Schlüssel)
#   tools/testlab/certs/<name>.crt              → öffentliches Zertifikat
# Zusätzlich ein abgelaufenes Zertifikat (expired.p12) zum Testen der Statusanzeige.
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)/certs"
mkdir -p "$DIR" && cd "$DIR"

if [[ ! -f testlab-ca.key ]]; then
  openssl req -x509 -newkey rsa:2048 -nodes -keyout testlab-ca.key -out testlab-ca.crt -days 3650 \
    -subj "/CN=Neruna Testlabor CA/O=Neruna Testlabor" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
fi

issue() { # name email display-name days [start-offset]
  local name=$1 email=$2 cn=$3 days=$4
  openssl req -newkey rsa:2048 -nodes -keyout "$name.key" -out "$name.csr" -subj "/CN=$cn/emailAddress=$email" 2>/dev/null
  cat > "$name.ext" <<EXT
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,nonRepudiation,keyEncipherment
extendedKeyUsage=emailProtection
subjectAltName=email:$email
EXT
  openssl x509 -req -in "$name.csr" -CA testlab-ca.crt -CAkey testlab-ca.key -CAcreateserial -out "$name.crt" -days "$days" -extfile "$name.ext" 2>/dev/null
  openssl pkcs12 -export -inkey "$name.key" -in "$name.crt" -certfile testlab-ca.crt -name "$cn" -out "$name.p12" -passout pass:geheim
  rm -f "$name.csr" "$name.ext"
  echo "  $name.p12  ($email, $days Tage)"
}

echo "Zertifikate in $DIR:"
issue anna anna@example.com "Anna Muster" 730
issue lea lea@example.com "Lea Keller" 20
issue marco marco@example.com "Marco Bernasconi" 365
# Expired: valid for one day, backdated via a CA-signed cert with past dates.
openssl req -newkey rsa:2048 -nodes -keyout expired.key -out expired.csr -subj "/CN=Alt Zertifikat/emailAddress=anna@example.com" 2>/dev/null
printf 'basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=emailProtection\nsubjectAltName=email:anna@example.com\n' > expired.ext
# "openssl ca" can set explicit start/end dates (openssl x509 -not_before needs OpenSSL 3.4+).
: > ca-index.txt
[[ -f ca-serial ]] || echo 1000 > ca-serial
cat > ca.cnf <<CNF
[ ca ]
default_ca = testlab
[ testlab ]
database = ca-index.txt
new_certs_dir = .
serial = ca-serial
default_md = sha256
policy = any
copy_extensions = none
unique_subject = no
[ any ]
commonName = supplied
emailAddress = optional
CNF
openssl ca -batch -config ca.cnf -cert testlab-ca.crt -keyfile testlab-ca.key -in expired.csr -out expired.crt \
  -startdate 20240101000000Z -enddate 20250101000000Z -extfile expired.ext -notext 2>/dev/null
rm -f ca-index.txt* ca-serial.old ca.cnf [0-9A-F]*.pem
openssl pkcs12 -export -inkey expired.key -in expired.crt -name "Alt Zertifikat" -out expired.p12 -passout pass:geheim
rm -f expired.csr expired.ext
echo "  expired.p12 (anna@example.com, abgelaufen)"
