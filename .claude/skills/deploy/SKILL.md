---
name: deploy
description: Déploie le cockpit Stream Deck × NinjaTrader vers l'installation active (plugin, bridge, sources add-on NT8). À utiliser dès qu'un changement doit être testé ou livré sur la machine de trading — « déployer », « installer », « mettre en production », « tester dans le vrai Stream Deck ».
---

# Déploiement vers l'installation active

L'installation réelle **ne correspond pas** aux instructions du README. Déployer partiellement
laisse une installation à moitié à jour qui a l'air correcte mais exécute de l'ancien code.

## Cibles

| Source (dépôt) | Destination |
|----------------|-------------|
| `src/streamdeck-ninjatrader/dist/*` | `%APPDATA%\Elgato\StreamDeck\Plugins\com.trader.ninjatrader.sdPlugin\dist\` |
| `src/streamdeck-ninjatrader/com.trader.ninjatrader.sdPlugin/manifest.json` | racine du dossier `.sdPlugin` *(si modifié)* |
| `src/streamdeck-ninjatrader/com.trader.ninjatrader.sdPlugin/ui/*` | `…\.sdPlugin\ui\` *(si modifié)* |
| `src/StreamDeckBridge/publish/*` | `…\.sdPlugin\bridge\` |
| `src/NinjaTrader.AddOn.StreamDeck/**/*.cs` | `Documents\NinjaTrader 8\bin\Custom\AddOns\StreamDeck\` **à plat** |

L'add-on se déploie **en sources `.cs`** : NinjaScript les compile au démarrage de NinjaTrader.
Copier le DLL construit n'aurait aucun effet.

## Procédure

### 1. Construire

```powershell
dotnet build "src\NinjaTrader.AddOn.StreamDeck\NinjaTrader.AddOn.StreamDeck.csproj" -c Release
dotnet publish "src\StreamDeckBridge\StreamDeckBridge.csproj" -c Release -o "src\StreamDeckBridge\publish"
cd src\streamdeck-ninjatrader; npm run build
```

Les ~180 avertissements CS0436 de l'add-on sont normaux. Ne pas déployer si une **erreur** subsiste.

### 2. Sauvegarder l'existant

Copier `dist\`, `bridge\`, `manifest.json` et le dossier `AddOns\StreamDeck\` vers le scratchpad
avant d'écraser quoi que ce soit. C'est le seul moyen de revenir en arrière rapidement.

### 3. Arrêter le bridge en cours

**Obligatoire** : le bridge est lancé automatiquement par le plugin et **verrouille ses DLL**.
Sans cet arrêt, la copie échoue à mi-chemin et laisse un dossier `bridge\` incohérent.

```powershell
Get-Process | Where-Object { $_.Path -like "*sdPlugin\bridge\*" } | Stop-Process -Force
```

`Get-Process StreamDeckBridge` seul est trompeur : le processus peut être relancé par le plugin
juste après la copie de `dist\`. Toujours revérifier après avoir copié le plugin.

### 4. Copier

```powershell
$p    = "$env:APPDATA\Elgato\StreamDeck\Plugins\com.trader.ninjatrader.sdPlugin"
$nt   = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom\AddOns\StreamDeck"
$repo = "<racine du dépôt>"

Copy-Item "$repo\src\streamdeck-ninjatrader\dist\*" "$p\dist\" -Recurse -Force
Copy-Item "$repo\src\StreamDeckBridge\publish\*"    "$p\bridge\" -Recurse -Force

Get-ChildItem "$repo\src\NinjaTrader.AddOn.StreamDeck" -Recurse -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
  ForEach-Object { Copy-Item $_.FullName "$nt\$($_.Name)" -Force }
```

### 4 bis. Purger les copies périmées — sinon rien ne compile

La copie ci-dessus écrit **à plat**. Toute copie d'un déploiement antérieur restée dans un
sous-dossier (`Models\`, `Services\`, `Utilities\`) déclare la **même classe dans le même
namespace** : NinjaScript compile récursivement, on obtient des `CS0101` et **toute** la
compilation NinjaScript échoue — y compris les indicateurs et stratégies du trader.

Ce cas s'est produit : la compilation a échoué silencieusement, NinjaTrader a rechargé le DLL de
la veille, et le code fraîchement déployé n'a jamais tourné.

```powershell
# Vérifier d'abord qu'aucune classe n'existe UNIQUEMENT en sous-dossier
$flat = Get-ChildItem "$nt\*.cs" | Select-Object -ExpandProperty Name
Get-ChildItem "$nt\*\*.cs" | Where-Object { $flat -notcontains $_.Name } | Select-Object FullName
# (aucun résultat = suppression sans perte)

Remove-Item -Recurse -Force "$nt\Models","$nt\Services","$nt\Utilities" -ErrorAction SilentlyContinue
Get-ChildItem "$nt\*.bak-*" | Remove-Item -Force
```

Contrôle : `(Get-ChildItem $nt -Recurse -Filter *.cs).Count` doit égaler le nombre de `.cs` du
projet, sans doublon.

### 5. Vérifier

Vérifier **sur les fichiers déployés**, jamais sur ceux du dépôt :

```powershell
Get-ChildItem "$p\bridge\StreamDeckBridge.exe", "$p\dist\plugin.js" | Select-Object Name, LastWriteTime
Get-ChildItem "$nt" -Filter *.cs | Select-Object Name, LastWriteTime
```

Puis lancer le bridge déployé et confirmer qu'il écrit bien son log du jour :

```powershell
Start-Process "$p\bridge\StreamDeckBridge.exe" -WindowStyle Hidden
Get-Content "$env:APPDATA\StreamDeckTrader\logs\bridge-$(Get-Date -f yyyy-MM-dd).log" -Encoding UTF8 | Select-Object -First 5
```

> Une taille de fichier à `0` dans `Get-ChildItem` est trompeuse tant que le processus garde le
> fichier ouvert : lire le contenu, pas la taille.

### 6. Redémarrages — à faire par l'utilisateur

Ces deux étapes **ne peuvent pas être sautées** et arrêtent des applications de trading : les
annoncer, ne pas les exécuter d'autorité.

- **Stream Deck** : le processus node en cours garde l'ancien code. Tant qu'il n'a pas redémarré,
  `plugin-AAAA-MM-JJ.log` n'apparaît pas — c'est le signe le plus fiable que le nouveau plugin
  n'est pas actif. Le redémarrage relance aussi le bridge automatiquement.
- **NinjaTrader** : redémarrer **ne suffit pas**. NinjaTrader recharge `NinjaTrader.Custom.dll`
  tel quel ; il faut **ouvrir l'éditeur NinjaScript et compiler (F5)** pour que les sources
  déployées soient prises en compte. Vérification décisive :

  ```powershell
  Get-Item "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom\NinjaTrader.Custom.dll" |
    Select-Object LastWriteTime
  ```

  Si la date du DLL est antérieure à celle des `.cs` déployés, l'ancien code tourne toujours.
  L'apparition de `addon-AAAA-MM-JJ.log` confirme que la nouvelle version est active.

## Contrôle final

Les trois fichiers du jour doivent exister dans `%APPDATA%\StreamDeckTrader\logs\` après
redémarrage complet. Un fichier manquant = un composant qui n'a pas été rechargé.
