# Builds claude-session-deck.exe (console: hook / open / rename / starts the window) and claude-session-deck-gui.exe (floating window) with the
# C# compiler that ships with Windows (no SDK needed).
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$sources = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
$common = @(
  '/nologo', '/codepage:65001', '/optimize+', "/win32manifest:$root\app.manifest", "/lib:$fw\WPF",
  "/win32icon:$root\icon\app.ico", "/resource:$root\icon\app.ico,ClaudeSessionDeck.app.ico",
  '/r:UIAutomationClient.dll', '/r:UIAutomationTypes.dll', '/r:WindowsBase.dll',
  '/r:System.Web.Extensions.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll'
)

& "$fw\csc.exe" @common /target:exe "/out:$root\claude-session-deck.exe" $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$fw\csc.exe" @common /target:winexe /define:GUI "/out:$root\claude-session-deck-gui.exe" $sources
exit $LASTEXITCODE
