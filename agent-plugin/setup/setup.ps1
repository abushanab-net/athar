# Athar agent setup for Windows (T132): irm {{BASE_URL}}/agent/setup.ps1 | iex
# Asks for your access token without echoing it, so it never reaches your shell history.
$ErrorActionPreference = 'Stop'
$base = '{{BASE_URL}}'; $server = '{{SERVER_URL}}'; $mcp = '{{MCP_URL}}'

if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Athar setup needs Node 22.15 or later: https://nodejs.org' }
$v = [version]((node --version).TrimStart('v'))
if ($v -lt [version]'22.15') { throw "Athar setup needs Node 22.15 or later (you have $v)." }

Write-Host "Create an access token on the Agents page ($base/agents), then paste it here."
$secure = Read-Host 'Athar access token' -AsSecureString
$env:ATHAR_TOKEN = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("athar-" + [guid]::NewGuid())
try {
    New-Item -ItemType Directory $tmp | Out-Null
    Invoke-WebRequest "$base/agent/bundle.zip" -OutFile "$tmp/bundle.zip" -UseBasicParsing
    Expand-Archive "$tmp/bundle.zip" "$tmp/bundle"
    node --use-system-ca "$tmp/bundle/setup.mjs" "$tmp/bundle" $server $mcp
    if ($LASTEXITCODE -ne 0) { throw 'Athar setup stopped (see above).' }
}
finally {
    Remove-Item Env:ATHAR_TOKEN -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
