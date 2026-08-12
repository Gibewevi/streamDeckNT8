; Installateur Bitlearn TradeDeck — Inno Setup 6.
;
; Produit un unique .exe téléchargeable depuis Bitlearn. L'utilisateur double-clique, obtient un
; raccourci sur son bureau, et TradeDeck démarre à chaque ouverture de session. Plus aucune
; commande à taper.
;
; Construction : ne pas appeler ISCC directement — `build-installer.ps1` prépare d'abord une
; charge utile sans les dépendances de développement (26 Mo de TypeScript s'y trouvaient sinon).

#define AppName        "Bitlearn TradeDeck"
#define AppPublisher   "Bitlearn"
#define AppUrl         "https://bitlearn.fr/tradedeck"
#define ExeBase        "BitlearnTradeDeck-Setup"

#ifndef AppVersion
  #define AppVersion "0.11.0"
#endif
#ifndef Payload
  #define Payload "..\..\..\build\payload"
#endif
#ifndef OutDir
  #define OutDir "..\..\..\build"
#endif

[Setup]
AppId={{8E4C1B77-3D2A-4C6E-9F31-TRADEDECK0001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={localappdata}\TradeDeck
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\assets\TradeDeck.ico
OutputDir={#OutDir}
OutputBaseFilename={#ExeBase}-{#AppVersion}
SetupIconFile=assets\TradeDeck.ico
WizardStyle=modern

; Installation par utilisateur, sans élévation. C'est ce qu'exige la tâche planifiée : enregistrée
; en administrateur, elle ne se déclencherait pas à la session de l'utilisateur. Cela évite aussi
; l'invite UAC, qui fait abandonner une partie des installations.
PrivilegesRequired=lowest

; LZMA2 au maximum : la charge utile est dominée par node.exe (91 Mo), très compressible.
Compression=lzma2/max
SolidCompression=yes

; Refuse d'installer par-dessus une version plus récente, plutôt que de la rétrograder en silence.
VersionInfoVersion={#AppVersion}
AppMutex=BitlearnTradeDeckSetup

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "register-task.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "assets\TradeDeck.ico"; DestDir: "{app}\assets"; Flags: ignoreversion
; Deuxième copie, temporaire : l'arrêt des processus doit avoir lieu AVANT la copie, donc avant
; que la version installée du script n'existe. Sur une mise à jour, celle du dossier serait de
; surcroît l'ancienne.
Source: "register-task.ps1"; Flags: dontcopy

[Icons]
; Le raccourci ouvre la configuration sur Bitlearn — l'interface locale n'est plus la référence.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\TradeDeck.vbs"; IconFilename: "{app}\assets\TradeDeck.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\TradeDeck.vbs"; IconFilename: "{app}\assets\TradeDeck.ico"; Tasks: desktopicon

[Run]
; -WindowStyle Hidden : l'enregistrement de la tâche ne doit pas faire clignoter une console.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\register-task.ps1"" -Action install -InstallDir ""{app}"""; \
  StatusMsg: "Enregistrement du démarrage automatique…"; Flags: runhidden waituntilterminated

; Lancement immédiat, sinon l'utilisateur devrait fermer sa session pour que TradeDeck démarre.
Filename: "{app}\TradeDeck.vbs"; Description: "Ouvrir TradeDeck"; Flags: postinstall nowait shellexec skipifsilent

[UninstallRun]
; Avant la suppression des fichiers : la tâche doit être retirée et l'hôte arrêté, sinon node.exe
; garde ses fichiers verrouillés et la désinstallation laisse un dossier à moitié vide.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\register-task.ps1"" -Action uninstall -InstallDir ""{app}"""; \
  RunOnceId: "RetirerTacheTradeDeck"; Flags: runhidden waituntilterminated

[Code]
{
  Arrête l'hôte ET le bridge avant que le moindre fichier ne soit remplacé.

  `StreamDeckBridge.exe` est un processus séparé, lancé par le superviseur de l'hôte. Sans cet
  arrêt il garde ses propres fichiers ouverts, et l'installateur échoue à les écraser —
  « impossible de fermer StreamDeckBridge ». Fermer l'hôte seul ne suffit pas : il ne tue
  délibérément pas le bridge en s'arrêtant, celui-ci portant le verrou de la macro de sécurité.

  Rien n'est perdu au passage : l'état de la macro est écrit dans
  %APPDATA%\StreamDeckTrader\safety-macro.json à chaque changement et relu au redémarrage.
}
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  ExtractTemporaryFile('register-task.ps1');
  if not Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + ExpandConstant('{tmp}\register-task.ps1') + '"'
        + ' -Action stop -InstallDir "' + ExpandConstant('{app}') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) then
    { L'échec du lancement de PowerShell n'est pas bloquant : si rien ne tournait, la copie
      passera de toute façon. Inno signalera lui-même un fichier verrouillé le cas échéant. }
    Log('Arret prealable impossible a lancer');
end;

[UninstallDelete]
; Le layout et les journaux vivent hors du dossier d'installation (%APPDATA%\StreamDeckTrader) et
; sont volontairement conservés : réinstaller ne doit pas effacer la configuration ni l'historique.
Type: filesandordirs; Name: "{app}\dist"
Type: filesandordirs; Name: "{app}\node_modules"
Type: filesandordirs; Name: "{app}\bridge"
Type: filesandordirs; Name: "{app}\ui"
