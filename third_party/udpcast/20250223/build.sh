#!/usr/bin/env bash
set -euo pipefail
export PATH="/usr/bin:$PATH"
cd "$(dirname "$0")"
export PATH="$PWD/llvm-mingw-20261006-ucrt-x86_64/bin:/usr/bin:$PATH"
for arch in aarch64 x86_64; do
    mkdir -p "build-$arch"
    (
        cd "build-$arch"
        if [ ! -f Makefile ]; then
          CC="$arch-w64-mingw32-clang" CFLAGS='-O2 -Wall -DBB_FEATURE_UDPCAST_FEC' LDFLAGS='-static' \
            bash ../source/configure --build=x86_64-pc-msys --host="$arch-w64-mingw32" > configure-output.log 2>&1
        fi
        make clean > build-output.log 2>&1
        make -j4 udp-sender.exe udp-receiver.exe >> build-output.log 2>&1
    )
done
