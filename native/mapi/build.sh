#!/usr/bin/env bash
# Builds Neruna's Simple MAPI DLL for 64-bit and 32-bit Windows programs (mingw-w64: apt-get install gcc-mingw-w64).
# Usage: native/mapi/build.sh <output folder>   → <output>/NerunaMapi64.dll and <output>/NerunaMapi32.dll
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:?output folder}"
mkdir -p "$OUT"
FLAGS=(-shared -O2 -Wall -Wextra -Werror -municode -DUNICODE -D_UNICODE -static-libgcc -s)
# --kill-at: the 32-bit stdcall exports without "@20" (mapi32.dll looks them up by their plain names).
x86_64-w64-mingw32-gcc "${FLAGS[@]}" -o "$OUT/NerunaMapi64.dll" "$HERE/neruna_mapi.c"
i686-w64-mingw32-gcc "${FLAGS[@]}" -Wl,--kill-at -o "$OUT/NerunaMapi32.dll" "$HERE/neruna_mapi.c"
