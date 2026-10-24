#!/bin/sh
# SPDX-License-Identifier: MIT
# Copyright 2026 VelaShell Labs
#
# 本机没有 docker 时，在 WSL（或任何有 OpenSSH 的 Linux）里起一台**非 root** 的 sshd 给互操作用例用。
#
# 非 root 的 sshd 只能让它自己那个用户登录，而且验不了口令 —— 所以只开公钥认证，
# 用例那边设 VELASHELL_SSH_INTEROP_KEY_ONLY=1 改用私钥（见 src/VelaShell.Ssh/README.md）。
# 另外打开有限域 DH（OpenSSH 10 的 sshd 默认已经不开）与 RekeyLimit 1M（服务端发起的重协商），
# 让密钥交换矩阵与「开着压缩时服务端发起的重协商」真的跑到。
#
# 监听 2229，与互操作用例的默认端口（VELASHELL_SSH_INTEROP_PORT）一致；2222–2228 是 docker-compose.test.yml 的靶机，
# WSL2 把端口转到 Windows 的 localhost 上，撞了会连错机器。
#
# 用法：sh scripts/ssh/interop/wsl-sshd.sh <放客户端私钥的目录>      前台运行，Ctrl+C 停。
set -e

out="${1:?要给一个放客户端私钥的目录}"
D="$HOME/vela-interop"
mkdir -p "$D"
cd "$D"
[ -f host_ed25519 ] || ssh-keygen -q -t ed25519 -f host_ed25519 -N ''
[ -f host_ecdsa ] || ssh-keygen -q -t ecdsa -b 256 -f host_ecdsa -N ''
[ -f host_rsa ] || ssh-keygen -q -t rsa -b 3072 -f host_rsa -N ''
[ -f client ] || ssh-keygen -q -t ed25519 -f client -N ''
cp client.pub authorized_keys
cat > sshd_config <<EOF
Port 2229
ListenAddress 0.0.0.0
HostKey $D/host_ed25519
HostKey $D/host_ecdsa
HostKey $D/host_rsa
PidFile $D/sshd.pid
AuthorizedKeysFile $D/authorized_keys
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
StrictModes no
PubkeyAuthentication yes
AllowTcpForwarding yes
AllowStreamLocalForwarding yes
AllowAgentForwarding yes
X11Forwarding no
MaxStartups 200
MaxSessions 50
Subsystem sftp /usr/lib/openssh/sftp-server
KexAlgorithms +diffie-hellman-group14-sha256,diffie-hellman-group16-sha512
RekeyLimit 1M
EOF
cp client "$out/interop_client"
echo "客户端私钥：$out/interop_client；登录用户：$(id -un)"
exec /usr/sbin/sshd -D -e -f "$D/sshd_config"
