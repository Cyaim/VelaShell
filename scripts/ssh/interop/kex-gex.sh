#!/bin/sh
# SPDX-License-Identifier: MIT
# Copyright 2026 VelaShell Labs
#
# 在**容器里**跑：让 sshd 在默认清单之外再开 diffie-hellman-group-exchange-sha256（RFC 4419）
# 与 diffie-hellman-group18-sha512（RFC 8268），供群交换与 group18 的互操作用例验证。
# OpenSSH 10 起服务端的默认清单里已经没有 DH 那几种了；group18 在本库的默认清单与 FIPS 清单里都有，
# 不打开它的话，「每一种密钥交换」在这台靶机上只能把它记成对端不支持。
# 用法（从宿主机）：
#   docker cp scripts/ssh/interop/kex-gex.sh <容器>:/tmp/kex-gex.sh
#   docker exec <容器> sh /tmp/kex-gex.sh
#
# 写的是「+」形式：只追加，默认清单原样留着 —— 别的用例谈出来的算法不变。

set -e

# 配置文件的位置从 sshd 的命令行里问出来（见 trust-ca.sh 的说明）。
pid=''
conf='/etc/ssh/sshd_config'

for p in /proc/[0-9]*; do
    [ -r "$p/cmdline" ] || continue
    cmd=$(tr '\000' ' ' < "$p/cmdline" 2>/dev/null) || continue
    case "$cmd" in
        *sshd*listener*|*sshd*-D*)
            pid="${p#/proc/}"
            f=$(printf '%s' "$cmd" | sed -n 's/.*-f \([^ ][^ ]*\).*/\1/p')
            [ -n "$f" ] && conf="$f"
            break
            ;;
    esac
done

[ -n "$pid" ] || { echo '在容器里找不到 sshd 进程' >&2; exit 1; }
[ -f "$conf" ] || { echo "sshd 说的配置文件不存在：$conf" >&2; exit 1; }
echo "sshd pid=$pid 配置=$conf"

line='KexAlgorithms +diffie-hellman-group-exchange-sha256,diffie-hellman-group18-sha512'

# ⚠️ **改写旧的那一行，不要再追加一行。** sshd_config 里同一个关键字只认第一次出现的值（sshd_config(5)），
# 早先的脚本写过只有群交换的一行；在那后面再追加，group18 这一行读到了也不起作用。
if grep -q '^KexAlgorithms +diffie-hellman-group-exchange-sha256' "$conf"; then
    sed -i "s#^KexAlgorithms +diffie-hellman-group-exchange-sha256.*#$line#" "$conf"
else
    echo "$line" >> "$conf"
fi

grep -q "^$line\$" "$conf" || { echo "KexAlgorithms 没写进去：$line" >&2; exit 1; }

kill -HUP "$pid"

echo "群交换与 group18 已打开：$line"
