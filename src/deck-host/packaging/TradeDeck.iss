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
#define ExeBase        "BitlearnTradeDeck-Setup"

; Serveur Bitlearn que ce paquet vise. L'installateur l'écrit dans
; %APPDATA%\StreamDeckTrader\bitlearn.json — la seule source lue à la fois par l'hôte et par le
; raccourci du bureau. Sans elle, les deux retombent sur la production, quelle que soit la
; provenance du téléchargement.
;
; Ne pas surcharger à la main : `build-installer.ps1 -BitlearnUrl https://dev.bitlearn.fr` aligne
; aussi le repli du lanceur et le nom du fichier produit.
#ifndef BitlearnUrl
  #define BitlearnUrl "https://bitlearn.fr"
#endif

; Suffixe de nom pour un paquet qui ne vise pas la production. Deux .exe de même version pointant
; deux serveurs différents sont autrement impossibles à distinguer dans un dossier de
; téléchargements — et se tromper des deux mène à un 404 muet.
#ifndef FileSuffix
  #define FileSuffix ""
#endif

#define AppUrl BitlearnUrl + "/tradedeck"

#ifndef AppVersion
  #define AppVersion "0.18.0"
#endif

#ifndef Payload
  #define Payload "..\..\..\build\payload"
#endif

; Sources NinjaScript à déposer dans NinjaTrader. Hors de `Payload` à dessein : ce dernier est
; copié en bloc vers `{app}`, et ces fichiers n'ont rien à y faire — leur seule destination est
; `Documents\NinjaTrader 8\bin\Custom`.
#ifndef NtPayload
  #define NtPayload "..\..\..\build\ninjatrader"
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
OutputBaseFilename={#ExeBase}-{#AppVersion}{#FileSuffix}
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

; L'intégration NinjaTrader. Sans elle le voyant « NinjaTrader » reste rouge quoi qu'il arrive :
; c'est l'add-on chargé DANS NinjaTrader qui ouvre la connexion vers le bridge, et rien d'autre
; ne le dépose. Les deux premiers clients payés ont buté là.
;
; `TdSwingEngine.cs` va dans `Indicators\` et NON avec l'add-on : `TrendEngine` le référence, les
; deux dossiers compilent dans le même `NinjaTrader.Custom.dll`, mais un second exemplaire sous
; `AddOns\StreamDeck\` lèverait un CS0101. Une compilation NinjaScript est tout ou rien : cet
; échec emporterait les indicateurs et stratégies personnels du trader.
Source: "{#NtPayload}\AddOns\StreamDeck\*.cs"; DestDir: "{code:DossierNinjaScript|AddOns\StreamDeck}"; \
  Flags: ignoreversion; Check: NinjaTraderPresent
Source: "{#NtPayload}\Indicators\TdSwingEngine.cs"; DestDir: "{code:DossierNinjaScript|Indicators}"; \
  Flags: ignoreversion uninsneveruninstall; Check: NinjaTraderPresent

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

{
  Fixe le serveur Bitlearn du poste, avant que quoi que ce soit ne démarre.

  L'hôte et le raccourci lisent tous deux %APPDATA%\StreamDeckTrader\bitlearn.json, et
  retombent sur la production quand il manque. Rien ne l'écrivait : un client servi par
  dev.bitlearn.fr installait donc un poste qui visait bitlearn.fr, où les pages TradeDeck
  n'existent pas. Il y arrivait sur un 404, sans le moindre indice que l'adresse était en
  cause. Constaté le 19/08/2026.

  Écrit en ssInstall et non en ssPostInstall : `register-task.ps1` lance la tâche à la
  fin de son enregistrement, et l'hôte lit sa cible une seule fois, au démarrage. Un fichier
  écrit après lui ne serait pris en compte qu'au redémarrage suivant.

  Le jeton d'appareil est effacé quand la cible change : il ne vaut que pour le serveur qui l'a
  émis, et l'hôte se considère appairé tant qu'il existe. Le garder produirait un
  poste silencieusement désynchronisé — 401 à chaque appel, aucune nouvelle demande
  d'appairage, et le deck qui continue de trader comme si de rien n'était. Le perdre coûte
  un clic au prochain démarrage.

  Le journal en attente n'est pas touché : ces lignes sont le travail du trader, pas un cache.
  Scellées avec la clé de l'ancien serveur, elles tomberont au palier non vérifiable sur
  le nouveau — dégradé, jamais perdu.
}

{ La valeur de "url" dans le fichier de configuration. Écrit par cet installateur, mais aussi
  à la main sur un poste de développement : on cherche la clé, pas une mise en forme. }
function UrlLue(const contenu: String): String;
var
  reste: String;
  p: Integer;
begin
  Result := '';
  p := Pos('"url"', contenu);
  if p = 0 then Exit;
  reste := Copy(contenu, p + 5, Length(contenu));
  p := Pos(':', reste);
  if p = 0 then Exit;
  reste := Copy(reste, p + 1, Length(reste));
  p := Pos('"', reste);
  if p = 0 then Exit;
  reste := Copy(reste, p + 1, Length(reste));
  p := Pos('"', reste);
  if p = 0 then Exit;
  Result := Copy(reste, 1, p - 1);
end;

{ La cible actuelle du poste. Fichier absent = production : c'est le repli codé en dur dans
  l'hôte comme dans le lanceur, donc bien ce que le poste visait jusqu'ici. Sans cette
  équivalence, la première mise à jour vers un installateur qui écrit le fichier
  désapparierait tous les postes déjà installés. }
function CibleActuelle(const chemin: String): String;
var
  brut: AnsiString;
  contenu, url: String;
begin
  Result := 'https://bitlearn.fr';
  if not FileExists(chemin) then Exit;
  if not LoadStringFromFile(chemin, brut) then Exit;
  contenu := brut;
  url := Trim(UrlLue(contenu));
  if url <> '' then Result := url;
end;

{
  Dossier NinjaScript du poste. La constante Inno userdocs, et non %USERPROFILE% suivi de
  Documents en dur : ce dossier se redirige — OneDrive le fait par défaut sur beaucoup de
  machines neuves — et seule la constante suit la redirection.
}
function DossierNinjaScript(Param: String): String;
begin
  Result := AddBackslash(ExpandConstant('{userdocs}\NinjaTrader 8\bin\Custom')) + Param;
end;

var
  NtVerifie: Boolean;
  NtDetecte: Boolean;

{
  NinjaTrader 8 est-il installé ?

  On ne crée jamais l'arborescence : la fabriquer sur un poste sans NinjaTrader y laisserait un
  faux dossier de plateforme, et NinjaTrader installé ensuite ne le lirait pas forcément.
  Absent, on saute le dépôt et on le DIT à la fin — un voyant rouge sans explication est
  exactement ce qu'on corrige ici.
}
function NinjaTraderPresent: Boolean;
begin
  if not NtVerifie then
  begin
    NtDetecte := DirExists(ExpandConstant('{userdocs}\NinjaTrader 8\bin\Custom'));
    NtVerifie := True;
  end;
  Result := NtDetecte;
end;

{
  Efface les sources de la version précédente avant d'écrire les nôtres.

  Sans cela, un fichier retiré ou renommé entre deux versions resterait à compiler pour
  toujours. Et comme une compilation NinjaScript est tout ou rien, une source orpheline qui ne
  compile plus emporterait avec elle les indicateurs et stratégies du trader.

  Seuls les `.cs` de NOTRE dossier sont touchés. C'est aussi ce qui rattrape une copie
  égarée de `TdSwingEngine.cs` déposée ici à la main : elle produirait un CS0101 avec
  celle d'`Indicators\`.
}
procedure PurgerSourcesAddOn;
var
  dossier: String;
  rec: TFindRec;
begin
  dossier := DossierNinjaScript('AddOns\StreamDeck');
  if not DirExists(dossier) then Exit;
  if not FindFirst(AddBackslash(dossier) + '*.cs', rec) then Exit;
  try
    repeat
      DeleteFile(AddBackslash(dossier) + rec.Name);
    until not FindNext(rec);
  finally
    FindClose(rec);
  end;
end;

{ Ce que le trader doit savoir en partant, et rien de plus. }
procedure MessageNinjaTrader;
begin
  if WizardSilent then Exit;

  if NinjaTraderPresent then
    MsgBox('L''intégration NinjaTrader a été installée.' + #13#10#13#10 +
           'Si NinjaTrader 8 est ouvert, fermez-le et rouvrez-le : il compile l''add-on à son ' +
           'démarrage. Le voyant « NinjaTrader » passe alors au vert.', mbInformation, MB_OK)
  else
    MsgBox('NinjaTrader 8 n''a pas été trouvé sur ce poste.' + #13#10#13#10 +
           'TradeDeck est installé et fonctionnel, mais son intégration NinjaTrader ne l''est ' +
           'pas : le voyant « NinjaTrader » restera rouge. Installez NinjaTrader 8, puis ' +
           'relancez cet installateur.', mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  dossier, config, precedente: String;
begin
  if CurStep = ssPostInstall then
  begin
    MessageNinjaTrader;
    Exit;
  end;
  if CurStep <> ssInstall then Exit;

  { Avant la copie : les fichiers de cette version arrivent ensuite, dans un dossier propre. }
  if NinjaTraderPresent then PurgerSourcesAddOn;

  dossier := ExpandConstant('{userappdata}\StreamDeckTrader');
  config := AddBackslash(dossier) + 'bitlearn.json';
  precedente := CibleActuelle(config);

  if not ForceDirectories(dossier) then
  begin
    { Non bloquant : un poste sans fichier retombe sur la production, soit exactement l'état
      d'avant. Tracé dans le journal d'installation, rien n'est montré au trader. }
    Log('bitlearn.json : dossier d etat impossible a creer');
    Exit;
  end;

  if not SaveStringToFile(config, '{"url": "{#BitlearnUrl}"}' + #13#10, False) then
  begin
    Log('bitlearn.json : ecriture impossible');
    Exit;
  end;

  if CompareText(precedente, '{#BitlearnUrl}') <> 0 then
  begin
    DeleteFile(AddBackslash(dossier) + 'device.json');
    Log('Serveur Bitlearn : ' + precedente + ' -> {#BitlearnUrl}, le poste sera reapparie');
  end;
end;

[UninstallDelete]
; Le layout et les journaux vivent hors du dossier d'installation (%APPDATA%\StreamDeckTrader) et
; sont volontairement conservés : réinstaller ne doit pas effacer la configuration ni l'historique.
Type: filesandordirs; Name: "{app}\dist"
Type: filesandordirs; Name: "{app}\node_modules"
Type: filesandordirs; Name: "{app}\bridge"
Type: filesandordirs; Name: "{app}\ui"

; L'add-on est retiré de NinjaTrader : le laisser ferait compiler à chaque démarrage un add-on
; qui cherche un bridge désinstallé.
;
; `Indicators\TdSwingEngine.cs` reste, délibérément. Ce fichier seul ne coûte rien et
; compile sans rien exiger, alors que le supprimer casserait la compilation — tout ou rien,
; indicateurs du trader compris — s'il l'utilise dans un indicateur à lui.
Type: filesandordirs; Name: "{code:DossierNinjaScript|AddOns\StreamDeck}"
