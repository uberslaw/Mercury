# Builds dist\Mercury and zips it to dist\Mercury-portable.zip
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $here "publish.ps1") -Zip
exit $LASTEXITCODE
