#!/bin/bash
# Publish the current build to GitHub so installed copies auto-update.
# Usage: ./publish.sh   (version is read from Core.cs)
set -e
SRC=/home/claude/memo
REPO=/home/claude/memopad
cd $SRC
VER=$(grep -oP 'AssemblyFileVersion\("\K[0-9.]+' Core.cs)
TAG="v$(echo $VER | cut -d. -f1-3)"
./build.sh
SHA=$(sha256sum MemoPad.exe | cut -d' ' -f1)

mkdir -p $REPO/src
cp Core.cs Theme.cs App.cs Views.cs Calc.cs Updater.cs build.sh publish.sh app.ico $REPO/src/
cp MemoPad.exe $REPO/MemoPad.exe
printf "%s\n%s\n" "$VER" "$SHA" > $REPO/version.txt
cat > $REPO/README.md <<EOF
# MemoPad 便签本

Windows 桌面备忘录 / 笔记小程序：待在桌面底层、开机自启、备忘录可链接笔记、随手算。

- 下载：[MemoPad.exe](https://github.com/as1018518103-hue/memopad/raw/main/MemoPad.exe)（当前版本 $VER）
- 已安装的程序会自动从这里更新（version.txt 记录最新版本号和 SHA256）。
- 笔记数据只保存在本机 \`%AppData%\\MemoPad\`，不会上传。
EOF

cd $REPO
git add -A
git -c user.name="Claude" -c user.email="noreply@anthropic.com" commit -q -m "Release $TAG

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_014MvufMuvu9ptddvwSPmQSK" || echo "nothing to commit"
git branch -M main
git tag -f $TAG
git push -u origin main
git push -f origin $TAG
echo "published $VER ($TAG) sha=$SHA"
