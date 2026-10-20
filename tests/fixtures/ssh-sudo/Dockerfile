# 受控 sudo 的真机验证靶子 —— AI 插件「以 root 执行」那条链路(VelaShell.Sudo.Kernel.Tests 的 SudoInteropTests)。
#
# 要验的是真实 sudo + PAM 的行为:requiretty、passwd_tries、runas_default、受限 sudoers 拒绝时回显 argv、
# use_pty(sudo 1.9.14 起默认开),以及登录 shell 不是 POSIX 家族(fish / tcsh)时命令行照样不被改写。
# 自己写的服务端替身与实现一样错时测不出来,所以用发行版原装的 sudo 与 PAM。
#
# 用 Ubuntu 24.04 LTS:sudo 经 PAM 认证、默认 use_pty,是这条功能最常对着的服务器形状;
# 钉住版本是因为 Ubuntu 25.10 起默认的 sudo 换成了 sudo-rs,那是另一个实现。
#
# 账号只有一个 sudoer。口令、登录 shell 与 /etc/sudoers.d/test-user 都由每条用例先 `docker exec` 进来按需重写
# (所以只能指向这台一次性容器);sudoer 不在 sudo 组,只有用例写进去的策略生效。
# OpenSSH 9.6 还没有 PerSourcePenalties,不用像 ssh-shells 那样关它;换更新的底包时记得补上。
FROM ubuntu:24.04

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends openssh-server sudo fish tcsh \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /run/sshd \
    && ssh-keygen -A \
    && useradd -m -s /bin/bash sudoer \
    && echo 'sudoer:velapass' | chpasswd

# Ubuntu 的 sshd_config 开头就 Include 了 sshd_config.d,先读到的值生效。
RUN printf '%s\n' \
    'Port 2222' \
    'PermitRootLogin no' \
    'PasswordAuthentication yes' \
    'KbdInteractiveAuthentication no' \
    'UsePAM yes' \
    'PrintMotd no' \
    'MaxStartups 100' \
    > /etc/ssh/sshd_config.d/10-velashell.conf

EXPOSE 2222
CMD ["/usr/sbin/sshd", "-D", "-e"]
