# Builds and runs the unit tests (tests\*.cs against the pure logic in src\). Exit code 0 = all passed.
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$out = Join-Path $env:TEMP 'claude-session-deck-tests'
New-Item -ItemType Directory -Force $out | Out-Null
$sources = @(Get-ChildItem "$root\src\*.cs" | Where-Object { $_.Name -ne 'Program.cs' } | ForEach-Object { $_.FullName }) +
           @(Get-ChildItem "$root\tests\*.cs" | ForEach-Object { $_.FullName })
& "$fw\csc.exe" /nologo /codepage:65001 "/lib:$fw\WPF" /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll `
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /main:ClaudeSessionDeck.Tests.Runner "/out:$out\claude-session-deck-tests.exe" $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$out\claude-session-deck-tests.exe"
exit $LASTEXITCODE
