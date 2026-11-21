# ApiTester

Application desktop C# (.NET 10 / Avalonia UI) pour le test unitaire d'API HTTP, l'exécution de lots paramétrés depuis une commande `curl`, l'enchaînement de requêtes avec extraction de variables et export de scripts CLI autonomes, ainsi que l'analyse de réponses JSONPath et Regex.

## Emplacement du projet

Les sources de l'application, la solution et la documentation technique détaillée se trouvent dans le dossier `PROJET_GUI_API/` :

- Solution : `PROJET_GUI_API/ApiTester.slnx`
- Fichier projet .NET : `PROJET_GUI_API/ApiTester.csproj`
- Documentation complète : `PROJET_GUI_API/README.md`

```text
.
├── README.md                          Présentation générale et localisation du projet
└── PROJET_GUI_API/                    Code source complet d'ApiTester
    ├── README.md                      Documentation technique détaillée
    ├── ApiTester.slnx
    ├── ApiTester.csproj
    ├── Program.cs
    ├── App.axaml(.cs)
    ├── Models/
    ├── Services/
    ├── ViewModels/
    ├── Views/
    ├── Converters/
    ├── Styles/
    └── Assets/
```

## Prérequis, compilation et lancement depuis la racine

Prérequis : SDK .NET 10 (`10.0` ou supérieur).

```bash
# Restaurer les dépendances
dotnet restore PROJET_GUI_API/ApiTester.csproj

# Compiler en Debug
dotnet build PROJET_GUI_API/ApiTester.csproj

# Lancer l'application
dotnet run --project PROJET_GUI_API/ApiTester.csproj

# Compiler en Release
dotnet build PROJET_GUI_API/ApiTester.csproj -c Release
```

Ou depuis le dossier du projet :

```bash
cd PROJET_GUI_API
dotnet run --project ApiTester.csproj
```

## Aperçu des fonctionnalités

L'interface est organisée autour de quatre onglets et d'un éditeur superposé plein cadre (touche `Échap` pour fermer) :

1. **Requête** : envoi HTTP unitaire (`GET`, `POST`, `PUT`, `PATCH`, `DELETE`), gestion des paramètres d'URL, en-têtes (avec éditeur de cookies en popup basculable Tableau / Texte), corps (`Aucun`, `JSON`, `Texte`, `Formulaire`), authentification (`Bearer`, `Basic`, `API Key`), affinage de la réponse sur la même ligne (`Auto`, `JSONPath`, `Regex`, `XPath`) avec bascule non destructive entre vue brute et vue affinée.
2. **Lot (curl)** : analyseur multi-shell de commandes `curl` / `curl.exe` (`POSIX`, `PowerShell`, `cmd.exe`), variables `{{...}}` (`Fixe`, `Plage` sur `BigInteger` en bases 2/8/10/16, `Liste`, `Aléatoire` avec graine optionnelle), modes `Combinaisons` et `Parallèle` avec ordre des axes configurable, prévisualisation des 3 premières requêtes, garde-fous (500 / 10 000 requêtes), détection de rate limit (`429`, `Retry-After`, backoff exponentiel, quotas préventifs) et export disque (fichiers unitaires, `.json`, `.jsonl`, `_resume.csv`).
3. **Chaîne** : configuration guidée par commande `curl` ou bascule directe en mode manuel, enchaînement d'étapes HTTP avec passage de valeurs simples `{{variable}}` et de listes `[[liste]]` extraites du corps ou des en-têtes (`JSONPath` dont `[*]`, `[]`, `[n++]`, ou `Regex`) affinables via le panneau `Conditions` (Regex, filtres texte/longueur/nombre, déduplication, modes Guidé et Manuel), éditeur de cookies en popup, onglet `Retour` pour retourner une variable, une liste ou un objet/tableau JSON personnalisé (y compris sur une étape de retour pur sans URL), test hors ligne avec inspection complète, panneau des variables accumulées et génération de scripts CLI autonomes sans dépendance externe en **Python**, **JavaScript (Node.js)** et **PHP**.
4. **Extracteur** : analyse de réponses `JSON`, `XML`, `HTML` ou `Texte`, navigation par arbre dépliable/repliable, génération et test en direct d'expressions `JSONPath` et `Regex`, sélection manuelle sur texte brut et transfert direct d'une règle vers une étape de l'onglet `Chaîne`.

Pour le détail complet de l'architecture MVVM, des services, de la syntaxe des gabarits, des expressions de retour et JSONPath, de la journalisation Serilog et du masquage des secrets, consulter `PROJET_GUI_API/README.md`.
