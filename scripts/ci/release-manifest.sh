#!/usr/bin/env bash
# 生成应用内自更新清单 latest.json,写到标准输出(.github/workflows/release.yml 的两个任务调用)。
#
# RID 列表、产物命名与清单字段只在这里写一份,两种取数方式产出同一份结构:
#   release-manifest.sh files <产物目录> <release 元数据>
#       构建后附加任务:对刚构建好的产物现算 SHA-256 与字节数。
#   release-manifest.sh sums <SHA256SUMS.txt> <release 附件列表> <release 元数据>
#       仅同步日志任务的补救:`gh release upload --clobber` 是先删旧附件、再传新附件,同步若在
#       两步之间被取消或失败,Release 上就只剩 SHA256SUMS.txt、没有 latest.json,而稳定通道的
#       客户端从此一直报「已是最新」。按构建时留下的校验表取哈希、按 Release 上现存附件取字节数,
#       重建出与原清单一致的 assets —— 不必重新构建,重新构建会换掉全部哈希。
# <release 元数据> 是 `gh release view --json body,publishedAt,url,...` 的输出(调用方已校验);
# <release 附件列表> 是 `gh release view --json assets` 的输出。
# 版本号取环境变量 TAG_NAME 去掉前缀 v;调用方须已按 VERSION_TAG_PATTERN 校验过标签。
# 任何一个平台的产物取不到都直接失败,不写出缺平台的清单。
set -euo pipefail

usage() {
  echo "用法: $0 files <产物目录> <release 元数据>" >&2
  echo "      $0 sums <SHA256SUMS.txt> <release 附件列表> <release 元数据>" >&2
  exit 2
}

: "${TAG_NAME:?TAG_NAME 未设置}"
VERSION="${TAG_NAME#v}"
mode="${1:-}"
case "$mode" in
  files) [[ $# -eq 3 ]] || usage; dir="$2"; metadata="$3" ;;
  sums) [[ $# -eq 4 ]] || usage; sums="$2"; release_assets="$3"; metadata="$4" ;;
  *) usage ;;
esac

assets='{}'
# 直接在当前 shell 里改 assets,不包进 $(...):命令替换里的子 shell 不继承 set -e,
# 取数失败会被吞掉,写出 sha256 为空的条目。
add_asset() {
  local rid="$1" file="$2"
  local sha size
  if [[ "$mode" == files ]]; then
    sha=$(sha256sum "$dir/$file" | cut -d' ' -f1)
    size=$(stat -c%s "$dir/$file")
  else
    # 校验表每行是 sha256sum 的输出:哈希、空白、文件名(二进制模式文件名前带 *);
    # 构建时已去掉 ./ 前缀,手工改过的表也容许它。同名的有效记录必须恰好一条。
    sha=$(awk -v name="$file" '
      { sub(/\r$/, "") }
      NF == 2 && length($1) == 64 && $1 !~ /[^0-9A-Fa-f]/ {
        f = $2; sub(/^[*]/, "", f); sub(/^\.\//, "", f)
        if (f == name) { count++; hash = tolower($1) }
      }
      END { if (count != 1) exit 1; print hash }' "$sums") || {
      echo "::error::SHA256SUMS.txt 里没有 $file 的唯一有效记录,无法重建 latest.json" >&2
      exit 1
    }
    size=$(jq -er --arg name "$file" '
      [.assets[] | select(.name == $name and (.state // "uploaded") == "uploaded")]
      | if length == 1 and (.[0].size | type == "number") and .[0].size > 0 then .[0].size else empty end' \
      "$release_assets") || {
      echo "::error::Release 上没有上传完成的 $file,无法重建 latest.json" >&2
      exit 1
    }
  fi
  assets=$(jq --arg rid "$rid" --arg name "$file" --arg sha "$sha" --argjson size "$size" \
    '.[$rid] = {name: $name, sha256: $sha, size: $size}' <<< "$assets")
}

for rid in win-x64 win-arm64; do
  add_asset "$rid" "VelaShell-$VERSION-$rid.zip"
done
# macOS 固定登记 tar.gz —— 更新器只会原地解包 zip/tar.gz,dmg 是给人手动安装的,
# 写进清单会让全体 mac 用户的应用内更新直接报"不支持的包格式"(见 release.yml 文件头)。
for rid in osx-x64 osx-arm64 linux-x64 linux-arm64; do
  add_asset "$rid" "VelaShell-$VERSION-$rid.tar.gz"
done
# 正文始终作为 JSON 数据读取,不经 shell 展开或拼接为脚本。
jq -n --arg version "$VERSION" --arg tag "$TAG_NAME" --argjson assets "$assets" \
  --slurpfile release "$metadata" '
  {version: $version, tag: $tag, assets: $assets,
   releaseNotes: ($release[0] | {body: (.body // ""), publishedAt, htmlUrl: .url})}'
