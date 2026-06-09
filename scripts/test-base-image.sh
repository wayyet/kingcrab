#!/bin/bash
set -e

pass() { echo "[PASS] $1"; }
fail() { echo "[FAIL] $1"; }

echo "=== 基础工具 ==="
curl --version | head -1 && pass "curl" || fail "curl"
wget --version 2>&1 | head -1 && pass "wget" || fail "wget"
ping -c 1 8.8.8.8 > /dev/null 2>&1 && pass "ping" || fail "ping"
git --version && pass "git" || fail "git"
jq --version && pass "jq" || fail "jq"

echo ""
echo "=== 网络工具 ==="
ip -V 2>&1 && pass "ip (iproute2)" || fail "ip"
ss --version 2>&1 | head -1 && pass "ss" || fail "ss"
nc -h 2>&1 | head -1 && pass "nc (netcat)" || fail "nc"
lsof -v 2>&1 | head -1 && pass "lsof" || fail "lsof"
ifconfig --version 2>&1 | head -1 && pass "ifconfig (net-tools)" || fail "ifconfig"
nslookup google.com > /dev/null 2>&1 && pass "nslookup (dnsutils)" || fail "nslookup"

echo ""
echo "=== Python ==="
python --version && pass "python -> python3 alias" || fail "python alias"
python3 --version && pass "python3" || fail "python3"
pip --version && pass "pip -> pip3 alias" || fail "pip alias"
pip3 --version && pass "pip3" || fail "pip3"

echo ""
echo "=== Python 包 ==="
python3 -c "import websockets; print('websockets', websockets.__version__)" && pass "websockets" || fail "websockets"
python3 -c "import websocket; print('websocket-client ok')" && pass "websocket-client" || fail "websocket-client"

echo ""
echo "=== gcrew-cli ==="
gcrew-cli --version && pass "gcrew-cli" || fail "gcrew-cli"

echo ""
echo "=== 解压工具 ==="
bzip2 --version 2>&1 | head -1 && pass "bzip2" || fail "bzip2"
xz --version | head -1 && pass "xz-utils" || fail "xz-utils"
zip --version 2>&1 | head -1 && pass "zip" || fail "zip"
unzip -v 2>&1 | head -1 && pass "unzip" || fail "unzip"

echo ""
echo "=== 构建工具 ==="
gcc --version | head -1 && pass "gcc (build-essential)" || fail "gcc"
make --version | head -1 && pass "make" || fail "make"

echo ""
echo "=== Node / uv ==="
node --version && pass "node" || fail "node"
npm --version && pass "npm" || fail "npm"
uv --version && pass "uv" || fail "uv"

echo ""
echo "=== Playwright ==="
npx playwright --version && pass "playwright" || fail "playwright"

echo ""
echo "Done."
