# VelaShell.XServer 帧率 / 延迟测量的客户端一侧(perf.cs 用):
#   docker build -t velashell-xperf scripts/xserver/perf
# xprobe(同目录 xprobe.c,指针跟随 / 按键 / 整窗动画)、glxgears(llvmpipe 直接渲染,每帧整窗 PutImage)、x11perf、
# xterm、GTK 3 / GTK 4 的演示程序;另带 sshd(用户 dp / testpass,开 X11 转发)与 tc(iproute2,netem 模拟链路延迟),
# 同一个镜像也能量经 SSH X11 转发的整条链路:
#   docker run -d --cap-add NET_ADMIN -p 2240:22 velashell-xperf /usr/sbin/sshd -D -e
FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends x11-apps mesa-utils xterm gtk-3-examples gtk-4-examples \
    gcc libc6-dev libx11-dev openssh-server xauth iproute2 procps \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /run/sshd && useradd -m -s /bin/bash dp && echo 'dp:testpass' | chpasswd \
 && printf 'X11Forwarding yes\nX11UseLocalhost yes\nPasswordAuthentication yes\n' > /etc/ssh/sshd_config.d/x11.conf
COPY xprobe.c /usr/src/xprobe.c
RUN gcc -O2 -o /usr/local/bin/xprobe /usr/src/xprobe.c -lX11
