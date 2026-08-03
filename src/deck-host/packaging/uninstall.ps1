<#
.SYNOPSIS
  Désinstalle le TradeDeck et remet l'application Stream Deck en marche.

.DESCRIPTION
  Annule exactement ce qu'a fait install.ps1 : tâche planifiée, raccourci, démarrage
  automatique d'Elgato. Les données (layout.json, journaux) sont CONSERVÉES par défaut,
  dans %APPDATA%\StreamDeckTrader : elles contiennent votre configuration de touches.
#>
[CmdletBinding()]
param(
  [string]$InstallDir = "$env:LOCALAPPDATA\TradeDeck",
  [switch]$RemoveData
)

$ErrorActionPreference = 'Continue'
$TaskName = 'TradeDeck'
function Info($m) { Write-Host "  $m" }
function Step($m) { Write-Host "`n$m" -ForegroundColor Cyan }

Step "1/5  Arrêt de l'hôte"
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
  Where-Object { $_.CommandLine -like '*TradeDeck*' -or $_.CommandLine -like '*deck-host*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
# Le bridge est laissé en vie s'il tourne : il porte la macro de sécurité et son verrou.
Info "Hôte arrêté (le bridge n'est pas touché)"

Step "2/5  Tâche planifiée"
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Info "Supprimée"

Step "3/5  Démarrage automatique de Stream Deck"
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$backup = Join-Path $InstallDir 'elgato-autostart.bak'
if (Test-Path $backup) {
  Set-ItemProperty -Path $run -Name 'Elgato Stream Deck' -Value (Get-Content $backup -Raw).Trim()
  Info "Restauré depuis la sauvegarde"
} else {
  Info "Aucune sauvegarde — rien à restaurer (réactivez-le depuis Stream Deck si besoin)"
}

Step "4/5  Raccourci"
Remove-Item "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\TradeDeck.lnk" -ErrorAction SilentlyContinue
Remove-Item "$env:USERPROFILE\Desktop\TradeDeck.lnk" -ErrorAction SilentlyContinue
Info "Supprimé"

Step "5/5  Fichiers"
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue }
Info "Programme supprimé"
if ($RemoveData) {
  Remove-Item "$env:APPDATA\StreamDeckTrader" -Recurse -Force -ErrorAction SilentlyContinue
  Info "Données supprimées (layout et journaux)"
} else {
  Info "Données conservées dans %APPDATA%\StreamDeckTrader (layout.json, journaux)"
}

Write-Host "`nDésinstallation terminée." -ForegroundColor Green
Write-Host "  Relancez l'application Stream Deck pour retrouver l'ancien fonctionnement."
