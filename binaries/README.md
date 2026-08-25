# `binaries/` — archive git des binaires livrables

Ce dossier est **volontairement versionné**. Il est rempli automatiquement par
`src/Directory.Build.targets` à chaque `dotnet build -c Release`, et remplace le versionnement de
`bin/` et `obj/`, retiré le 24/08/2026 : on garde une trace git des sorties de compilation sans
suivre les 188 fichiers de DLL de dépendances et de caches NuGet qui allaient avec.

## Ce qu'il contient

Un sous-dossier par projet, avec **l'assembly de ce projet et ses fichiers frères uniquement** —
`.dll`, `.exe`, `.pdb`, `.deps.json`, `.runtimeconfig.json`. Jamais les DLL de dépendances :
NuGet les restaure, et elles représentaient l'essentiel du volume retiré.

| Sous-dossier | Cible | Rôle du binaire |
|---|---|---|
| `Atas.Indicators.TradeDeck/` | net10.0 | **Livrable réel** : c'est ce `TradeDeck.Indicators.dll` qu'ATAS X charge. |
| `Atas.Strategy.TradeDeck/` | net10.0 | **Livrable réel** : c'est ce `TradeDeck.Strategy.dll` qu'ATAS classique charge depuis `%APPDATA%\ATAS\Strategies\`. |
| `StreamDeckBridge/` | net8.0 | Référence. Le déploiement passe par `dotnet publish` (voir `docs/publier-une-version.md`), pas par ce dossier. |
| `NinjaTrader.AddOn.StreamDeck/` | net48 | Contrôle de compilation. L'add-on est déployé **en sources `.cs`** chez le trader — ce DLL n'est jamais installé. |
| `NinjaTrader.Scripts/` | net48 | Contrôle de compilation seul, par conception (voir l'en-tête du `.csproj`). |

## Règles

- **Ne jamais ajouter `binaries/` au `.gitignore`.** C'est le seul endroit du dépôt où un binaire
  produit reste suivi ; `bin/`, `obj/`, `publish/`, `dist/`, `build/` et `bridge/` sont tous
  ignorés.
- **Ne rien éditer ni déposer ici à la main.** Tout est écrasé au prochain build Release.
- **Release uniquement.** Un `dotnet build -c Debug` ne touche pas à ce dossier, pour qu'une
  itération de développement ne salisse pas `git status`.
- Les builds SDK étant déterministes, reconstruire sans modifier les sources produit des octets
  identiques : le fichier archivé n'est pas réécrit et n'apparaît pas dans `git status`.
