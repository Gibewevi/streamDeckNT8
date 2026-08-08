# Enregistre (ou retire) la tâche planifiée qui lance TradeDeck à l'ouverture de session.
#
# Séparé de l'installateur parce que Inno Setup ne sait pas créer de tâche planifiée, et parce
# que l'opération doit tourner dans le contexte de l'utilisateur : une tâche enregistrée en
# administrateur ne se déclencherait pas à SA session.
#
# Appelé par l'installateur après la copie, et par le désinstalleur avant la suppression.

param(
  [Parameter(Mandatory = $true)][ValidateSet('install', 'uninstall', 'stop')][string]$Action,
  [string]$InstallDir = "$env:LOCALAPPDATA\TradeDeck",
  [string]$TaskName = 'TradeDeck'
)

$ErrorActionPreference = 'Stop'

<#
Arrête tout ce qui tourne depuis le dossier d'installation.

Deux processus, pas un : l'hôte (`node.exe`) **et le bridge** (`StreamDeckBridge.exe`), que le
superviseur lance à part. Ne fermer que l'hôte laissait le bridge tenir ses propres fichiers, et
l'installateur échouait à les remplacer — c'est exactement ce qui se produisait.

Arrêter le bridge ne lève aucune garantie de la macro de sécurité : son état, y compris
`lockedUntilUtc` et `tradeCount`, est écrit dans `%APPDATA%\StreamDeckTrader\safety-macro.json`
à chaque changement, et relu au redémarrage. Un verrou de six heures reste un verrou de six
heures après une réinstallation.
#>
function Stop-TradeDeckProcesses {
  param([string]$Dir)

  $cibles = @()

  $cibles += Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*$Dir*" }

  # L'enveloppe `wscript.exe` qui porte l'hôte. Tuer Node suffirait — l'enveloppe rend la main dès
  # qu'il sort — mais l'ordre inverse est possible selon ce qui se termine en premier, et un
  # lanceur resté seul relancerait un hôte que l'on vient d'arrêter.
  $cibles += Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*$Dir*" }

  # Filtré sur le chemin de l'exécutable : un bridge lancé depuis un autre dossier — une copie de
  # développement, par exemple — ne doit pas être abattu par l'installateur.
  $cibles += Get-CimInstance Win32_Process -Filter "Name='StreamDeckBridge.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Dir, [StringComparison]::OrdinalIgnoreCase) }

  foreach ($p in $cibles) {
    try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch { }
  }

  if ($cibles.Count -eq 0) { return }

  # Windows libère les verrous de fichiers de façon asynchrone : rendre la main trop tôt
  # ferait échouer la copie sur un fichier encore verrouillé par un processus déjà mort.
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $restants = @($cibles | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    if ($restants.Count -eq 0) { break }
  }
}

function Remove-TradeDeckTask {
  $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if (-not $existing) { return }
  try {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
  } catch {
    Write-Warning "Tache '$TaskName' non supprimee : $($_.Exception.Message)"
  }
}

# Appelé par l'installateur AVANT la copie des fichiers : à ce moment la tâche existe peut-être
# déjà et relancerait l'hôte entre l'arrêt et la copie.
if ($Action -eq 'stop') {
  $existante = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($existante) { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue }
  Stop-TradeDeckProcesses -Dir $InstallDir
  return
}

if ($Action -eq 'uninstall') {
  Remove-TradeDeckTask
  # Sans cet arrêt, hôte et bridge gardent leurs fichiers verrouillés et la désinstallation
  # laisse un dossier à moitié vide.
  Stop-TradeDeckProcesses -Dir $InstallDir
  return
}

# Réenregistrement systématique : une mise à jour peut changer le chemin de l'exécutable ou les
# arguments, et une tâche existante conserverait silencieusement les anciens.
Remove-TradeDeckTask
# Ceinture et bretelles : la copie vient d'avoir lieu, mais un hôte relancé entre-temps
# tiendrait encore l'ancien binaire en mémoire.
Stop-TradeDeckProcesses -Dir $InstallDir

$node = Join-Path $InstallDir 'node.exe'
if (-not (Test-Path $node)) { throw "node.exe introuvable dans $InstallDir" }
$lanceur = Join-Path $InstallDir 'run-host.vbs'
if (-not (Test-Path $lanceur)) { throw "run-host.vbs introuvable dans $InstallDir" }

# Par `wscript.exe` et non `node.exe` directement : Node est une application console, et une tâche
# planifiée en session interactive lui alloue une fenêtre noire qui reste ouverte tant que
# TradeDeck tourne. `wscript` est un hôte fenêtré, il lance Node masqué.
# `//B` supprime les boîtes de dialogue d'erreur du moteur de script : personne n'est là pour les
# fermer, et une fenêtre modale bloquerait le démarrage jusqu'à la prochaine session.
$action = New-ScheduledTaskAction -Execute 'wscript.exe' `
  -Argument "//B //Nologo `"$lanceur`"" -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# Relance sur sortie non nulle : l'hôte s'arrête volontairement quand une autre instance occupe
# déjà le port, et il ne faut pas qu'une tâche morte laisse le deck éteint après un incident.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
  -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
  -Settings $settings -Principal $principal -Force | Out-Null

# L'application Elgato occupe l'unique place plugin du bridge et se dispute le boîtier : son
# démarrage automatique doit céder la place. L'hôte la ferme aussi à chaque lancement.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($nom in 'Elgato Stream Deck', 'StreamDeck') {
  if (Get-ItemProperty -Path $run -Name $nom -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $run -Name $nom -ErrorAction SilentlyContinue
  }
}

Start-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
