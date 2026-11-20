# ApiTester

Application desktop Windows/Linux/macOS de test et d'automatisation d'API HTTP construite en C# (.NET 10) avec Avalonia UI.

ApiTester regroupe dans une même fenêtre quatre outils complémentaires : l'envoi de requêtes HTTP unitaires avec affinage de réponse en direct, l'exécution de lots paramétrés à partir d'une commande `curl`, l'enchaînement d'étapes avec extraction de variables et génération de scripts CLI autonomes (Python, JavaScript, PHP), ainsi qu'un laboratoire d'extraction JSONPath et Regex.

---

## Sommaire

1. [Pile technique et dépendances](#pile-technique-et-dépendances)
2. [Prérequis, compilation et exécution](#prérequis-compilation-et-exécution)
3. [Architecture du projet](#architecture-du-projet)
4. [Fonctionnement détaillé par onglet](#fonctionnement-détaillé-par-onglet)
   - [Onglet Requête](#onglet-requête)
   - [Onglet Lot (curl)](#onglet-lot-curl)
   - [Onglet Chaîne](#onglet-chaîne)
   - [Onglet Extracteur](#onglet-extracteur)
   - [Éditeur superposé (Overlay)](#éditeur-superposé-overlay)
5. [Syntaxe des variables, listes et JSONPath](#syntaxe-des-variables-listes-et-jsonpath)
6. [Journalisation, sécurité et limites](#journalisation-sécurité-et-limites)
7. [Structure complète des fichiers](#structure-complète-des-fichiers)

---

## Pile technique et dépendances

Le projet cible `.NET 10` (`net10.0`, `OutputType` : `WinExe`, `Nullable` activé) dans `ApiTester.csproj`.

Paquets NuGet référencés :

| Paquet | Version | Rôle dans le projet |
| :--- | :--- | :--- |
| `Avalonia` | 12.1.6 | Noyau de l'interface graphique multiplateforme |
| `Avalonia.Desktop` | 12.1.6 | Backend bureau (Win32 / X11 / Wayland / macOS) |
| `Avalonia.Themes.Fluent` | 12.1.6 | Thème Fluent de base (surchargé dans `Styles/`) |
| `Avalonia.Fonts.Inter` | 12.1.6 | Police embarquée Inter pour l'interface |
| `Avalonia.Controls.DataGrid` | 12.1.2 | Tableaux de résultats des lots et des chaînes |
| `Avalonia.AvaloniaEdit` | 12.0.0 | Référencé pour l'édition avancée de texte |
| `AvaloniaEdit.TextMate` | 12.0.0 | Coloration syntaxique TextMate (référencé dans le csproj) |
| `AvaloniaUI.DiagnosticsSupport` | 2.2.3 | Outils de diagnostic Avalonia (inclus uniquement en Debug) |
| `CommunityToolkit.Mvvm` | 8.4.2 | Générateurs `[ObservableProperty]` et `[RelayCommand]` |
| `Microsoft.Extensions.DependencyInjection` | 11.0.0 | Conteneur IoC pour services et ViewModels |
| `Serilog` | 4.4.0 | Journalisation structurée |
| `Serilog.Sinks.File` | 7.0.0 | Écriture des journaux sur disque avec rotation |
| `Microsoft.Data.Sqlite` | 11.0.0 | Dépendance provisionnée dans le csproj (aucun stockage SQLite actif à ce stade) |

---

## Prérequis, compilation et exécution

Prérequis :

- SDK .NET 10 (`10.0` ou supérieur)

Commandes depuis le dossier `PROJET_GUI_API` :

```bash
# Restaurer les dépendances
dotnet restore ApiTester.csproj

# Compiler en Debug
dotnet build ApiTester.csproj

# Lancer l'application
dotnet run --project ApiTester.csproj

# Compiler en Release (exclut AvaloniaUI.DiagnosticsSupport)
dotnet build ApiTester.csproj -c Release
```

La solution peut également être ouverte via `ApiTester.slnx`.

---

## Architecture du projet

L'application suit le modèle MVVM avec liaisons compilées Avalonia (`x:DataType` déclaré sur chaque vue et template) et injection de dépendances centralisée dans `App.axaml.cs`.

Principes techniques retenus :

- **Client HTTP partagé (`HttpClient` sur `SocketsHttpHandler`)** :
  - `UseCookies = false` afin de ne pas injecter silencieusement de cookies entre les appels et de laisser le contrôle explicite via l'en-tête `Cookie`.
  - Décompression automatique activée pour `GZip`, `Deflate` et `Brotli`.
  - Timeout global sur `HttpClient` fixé à `Timeout.InfiniteTimeSpan`, le délai d'expiration réel étant appliqué par requête via un `CancellationTokenSource` lié au jeton d'annulation utilisateur.
- **Séparation Services / ViewModels / Modèles** :
  - Tous les moteurs métier (`HttpService`, `CurlParser`, `TemplateEngine`, `RateLimitDetector`, `ResponseExtractor`, `ResponseRefiner`, `ExtractionSuggester`, `BatchRunner`, `ChainRunner`, `ChainCliGenerator`, `BatchResultWriter`) implémentent une interface dédiée et sont enregistrés en singletons dans `App.axaml.cs`.
  - Les opérations longues (requêtes, lots, chaînes, écriture sur disque) s'exécutent de manière asynchrone hors du thread UI et remontent l'avancement via `IProgress<T>`.
- **Thème et ressources visuelles** :
  - `Styles/Tokens.axaml` définit les palettes `Dark` et `Light`, les tailles de police (`Inter` pour l'interface, `Cascadia Mono` / `Consolas` en monospace), et les rayons de bordure.
  - `Styles/Controls.axaml` homogénéise les boutons, onglets, barres d'outils et états sans survol agressif.
  - `Styles/Icons.axaml` regroupe toutes les icônes vectorielles en `Geometry` SVG, sans aucun recours aux emojis ou symboles Unicode de substitution.
- **Communication inter-onglets** :
  - `MainWindowViewModel` relie les quatre onglets via des callbacks :
    - Ouverture d'un modèle issu de `Lot (curl)` dans l'onglet `Requête`.
    - Envoi d'un corps de réponse depuis `Requête`, `Lot (curl)` ou `Chaîne` directement vers `Extracteur`.
    - Envoi d'une extraction construite dans `Extracteur` vers une étape donnée de `Chaîne`.
    - Ouverture de l'éditeur overlay via `IOverlayEditorService`.

---

## Fonctionnement détaillé par onglet

### Onglet Requête

Permet d'envoyer une requête HTTP unitaire et d'inspecter ou filtrer sa réponse.

- **Barre d'envoi unifiée** :
  - Sélection de la méthode HTTP : `GET`, `POST`, `PUT`, `PATCH`, `DELETE`.
  - Saisie d'URL : si aucun schéma n'est indiqué, `http://` est ajouté pour `localhost` et `127.0.0.1`, et `https://` dans les autres cas.
  - Raccourcis clavier : `Entrée` dans le champ URL ou `Ctrl+Entrée` n'importe où dans l'onglet déclenche l'envoi.
  - Bouton `Envoyer` remplacé pendant l'exécution par `Annuler`, accompagné d'une barre de progression indéterminée.
- **Affinage de réponse sur la ligne d'envoi (`ResponseRefiner`)** :
  - Sélecteur de mode à côté de l'URL : `Auto`, `JSONPath`, `Regex`, `XPath`.
  - Champ d'expression appliqué en direct sans modifier la réponse d'origine (`RawBody`).
  - En mode `Auto`, le moteur tente `JSONPath` si le corps ressemble à du JSON et que l'expression suit une syntaxe de chemin, `XPath` si l'expression commence par `/` sur du XML, sinon `Regex`.
  - En mode `Regex`, les groupes capturants sont extraits automatiquement (séparés par ` | ` si plusieurs groupes existent), avec un timeout d'évaluation de 500 ms et un plafond de 10 000 correspondances.
  - Un bandeau sous les pastilles de statut indique le résumé du filtre (nombre de valeurs ou d'éléments), permet de basculer à tout moment entre l'affichage affiné et la réponse brute (`Voir brut` / `Voir affiné`), ou d'effacer le filtre.
- **Configuration de la requête (`RequestView`)** :
  - `Paramètres d'URL` : éditeur clé/valeur activable ou désactivable par ligne.
  - `En-têtes` : éditeur clé/valeur (l'en-tête `Content-Length` est ignoré car géré par .NET).
  - `Corps` :
    - `Aucun` (`None`).
    - `JSON` (`application/json`, bouton d'agrandissement vers l'overlay).
    - `Texte` (`text/plain`, avec bouton d'agrandissement ; si un en-tête `Content-Type` contient `multipart/form-data`, les fins de ligne sont automatiquement normalisées en `\r\n`).
    - `Formulaire` (`application/x-www-form-urlencoded`, éditeur clé/valeur).
  - `Authentification` : `Aucune`, `Bearer` (en-tête `Authorization: Bearer ...`), `Basic` (encodage Base64 UTF-8), ou `API Key` (placée dans un en-tête ou en paramètre de requête).
  - `Options` : délai d'expiration (`TimeoutSeconds`, 30 s par défaut).
- **Inspection de la réponse (`ResponseView`)** :
  - Pastilles de code HTTP (colorées selon 2xx, 3xx, 4xx, 5xx ou erreur réseau), durée en ms, taille formatée et `Content-Type`.
  - Onglet `Corps` : indentation automatique du JSON valide (`MaxDepth = 1024`) sans couper le contenu, et bouton d'agrandissement en overlay.
  - Onglet `En-têtes` : liste complète avec texte sélectionnable.
  - Bouton `Extraire une valeur...` qui charge le corps brut et le `Content-Type` dans l'onglet `Extracteur`.

---

### Onglet Lot (curl)

Conçu pour analyser une commande `curl`, faire varier un ou plusieurs paramètres/variables, prévisualiser le plan d'exécution, contrôler la cadence et enregistrer les réponses sur disque.

- **Analyseur de commande `curl` (`CurlParser`)** :
  - Prend en charge automatiquement la syntaxe POSIX (bash/zsh avec `\` et guillemets simples/doubles ou ANSI-C `$'...'`), PowerShell (échappement et continuation par `` ` ``), ainsi que `cmd.exe` (`curl.exe`, continuation `^` en fin de ligne, dé-échappement `^X` et découpage selon les règles `CommandLineToArgvW`).
  - Options gérées : `--url`, `-X`/`--request`, `-H`/`--header`, `-d`/`--data`/`--data-raw`/`--data-binary`/`--data-urlencode` (multiples `-d` concaténés par `&`), `-b`/`--cookie`, `-u`/`--user` (converti en authentification Basic), `-A`/`--user-agent`, `-e`/`--referer`, `-G`/`--get` (conversion des données en paramètres d'URL).
  - Les options ignorées ou non applicables (`-L`, `-k`/`--insecure`, `-i`, en-têtes `Host`/`Content-Length`/`Accept-Encoding`/`Connection`) produisent des avertissements regroupés et triés par priorité dans un panneau dépliable. Note : la validation TLS reste toujours active même si `-k` figure dans la commande collée.
  - Bouton `Ouvrir dans l'onglet Requête` : transfère la requête analysée vers l'onglet `Requête` (avec avertissement si des marqueurs `{{...}}` restent présents).
- **Axes de variation** :
  - **Paramètres d'URL** (`VaryingParams`) : chaque paramètre peut rester fixe ou varier sur une plage entière (`Début`, `Fin`, `Pas`).
  - **Variables `{{nom}}`** (`BatchVariables`) : détectées automatiquement dans l'URL, les paramètres, les en-têtes, le corps et les champs de formulaire (avec anti-rebond de 250 ms). Quatre modes disponibles pour chaque variable :
    1. `Fixe` : valeur constante.
    2. `Plage` : séquence numérique sur `BigInteger` (bornes très grandes acceptées) en base `Binaire` (2), `Octale` (8), `Décimale` (10) ou `Hexadécimale` (16), avec largeur minimale de remplissage par zéros (`Width` jusqu'à 128), majuscules/minuscules en hexadécimal et préfixe optionnel (`0x`, `0b`, `0o`).
    3. `Liste` : une valeur par ligne (éditable en ligne ou via l'éditeur overlay).
    4. `Aléatoire` : génération de `1` à `10 000` valeurs selon cinq types :
       - `Entier` (entre `Min` et `Max`, formatable en base 2/8/10/16).
       - `Octets hex` (entre 1 et 64 octets).
       - `Texte` (longueur 1 à 256, choix des jeux de caractères : minuscules, majuscules, chiffres, `-` et `_`).
       - `GUID` (UUID v4).
       - `Tirage dans liste`.
       Option `Unique` (avec algorithme Fisher-Yates ou tirage avec rejet) et champ `Graine` (`Seed`) optionnel pour rendre la série reproductible (la graine utilisée reste consultable).
- **Modes d'exécution et ordre des axes** :
  - `Combinaisons` : produit cartésien complet de tous les axes actifs. Lorsque deux axes ou plus varient, un panneau permet de monter/descendre les axes pour contrôler l'ordre d'imbrication des boucles, avec un aperçu des 12 premières combinaisons.
  - `Parallèle` : itération index par index (le nombre de requêtes correspond au plus petit axe ; un avertissement est affiché si les axes ont des tailles différentes).
- **Prévisualisation et garde-fous** :
  - Bouton `Aperçu` : génère et affiche les 3 premières requêtes finales (méthode, URL, en-têtes, corps, détection de JSON invalide après substitution) sans émettre de trafic réseau.
  - Confirmation obligatoire à partir de `500` requêtes.
  - Plafond dur fixé à `10 000` requêtes par lancement.
  - Avertissement avec case de confirmation supplémentaire si la pause entre requêtes est inférieure à `100 ms` (valeur par défaut : `300 ms`).
- **Gestion du rate limit (`RateLimitDetector` et `RateLimitPolicy`)** :
  - Détection sur HTTP `429` plus une liste personnalisable de codes HTTP supplémentaires (séparés par des virgules).
  - Respect configurable de l'en-tête `Retry-After` (durée en secondes ou date HTTP), plafonné par `MaxRetryAfterSeconds` (120 s par défaut).
  - Backoff exponentiel optionnel (délai initial, multiplicateur strictement > 1, plafond maximal) avec gigue aléatoire de +/- 10 % et nombre maximal de tentatives par requête (0 à 20, 3 par défaut).
  - Ralentissement préventif via les en-têtes de quota restant et de réinitialisation (`QuotaRemainingHeader`, `QuotaResetHeader`, `QuotaThreshold`) : si la valeur de réinitialisation est >= `1 000 000 000`, elle est interprétée comme un timestamp Unix en secondes ; sinon comme un nombre de secondes relatif.
- **Suivi et enregistrement sur disque (`BatchResultWriter`)** :
  - Statistiques en direct (réussites, limitations de débit, nouvelles tentatives, autres erreurs).
  - Option `Arrêter au premier échec` et option `Conserver les corps` (décocheable pour économiser la mémoire sur de gros volumes).
  - La première réponse reçue est sélectionnée automatiquement pour être affichée sans clic supplémentaire.
  - Enregistrement disque désactivé ou en mode `Un fichier par requête`, `Fichier groupé` (`.json` sous forme de tableau indenté ou `.jsonl` JSON Lines), ou `Les deux`.
  - Options de création d'un sous-dossier horodaté `lot_yyyyMMdd_HHmmss` et d'export d'un résumé `_resume.csv` (UTF-8 avec BOM, séparateur `;`, colonnes : `index;parametres;statut;duree_ms;taille_octets;fichier;erreur;rate_limited;tentatives`).
  - Assainissement des noms de fichiers générés (caractères interdits et noms réservés Windows comme `CON`, `NUL`, `COM1`, etc., longueur maximale de 120 caractères).

---

### Onglet Chaîne

Permet d'enchaîner plusieurs requêtes HTTP où les valeurs extraites d'une étape alimentent les étapes suivantes.

- **Gestion des étapes** :
  - Ajout, suppression, duplication et réordonnancement haut/bas des étapes.
  - Chaque étape dispose de son propre parseur `curl`, méthode, URL, paramètres, en-têtes, corps, variables locales `{{...}}`, ordre des axes, délai post-étape (`DelayAfterMs`, 500 ms par défaut), option `Continuer la chaîne si cette étape échoue`, et choix d'appliquer ou non la politique globale de rate limit.
- **Extractions entre étapes (`ChainExtraction`)** :
  - Source : `Corps` (`Body`) ou `En-tête` (`Header`, avec nom d'en-tête insensible à la casse).
  - Type : `JSONPath` (corps uniquement) ou `Regex` (avec choix de l'index de groupe capturant, 1 par défaut).
  - Nom de variable cible : utilisé ensuite via `{{variable}}` pour une valeur simple, ou `[[variable]]` lorsque la case `En liste [[...]]` est cochée.
  - Option `Obligatoire` et `Valeur par défaut si absent` (utilisable lorsqu'une extraction scalaire n'est pas obligatoire).
  - Bouton `Tester` sur chaque règle d'extraction : teste l'expression hors ligne sur la dernière réponse reçue par l'étape. La modification d'une expression efface l'ancien aperçu, et le basculement de `En liste [[...]]` relance automatiquement le test si une réponse est déjà disponible.
  - Lorsque l'extraction produit une liste, un bouton `Voir tout` ouvre l'overlay avec la totalité des éléments extraits (un par ligne).
- **Itérations et boucles sur listes extraites** :
  - Si une étape utilise une ou plusieurs listes `[[...]]` dans son URL, ses paramètres, ses en-têtes ou son corps, chaque liste devient automatiquement un axe d'itération (`BatchAxisKind.ExtractedList`), combinable ou parallélisable avec les paramètres et variables locales de l'étape.
  - L'application valide avant lancement que chaque `[[liste]]` provient d'une étape précédente et qu'aucun `{{variable}}` n'est utilisé à la place d'une liste.
- **Exécution et résultats** :
  - La chaîne s'exécute séquentiellement d'étape en étape ; la première réponse arrivée s'affiche immédiatement dans le panneau de droite.
  - Un panneau `Variables accumulées` liste les valeurs scalaires et les listes collectées au fil de l'exécution, avec un bouton `Voir` pour afficher l'intégralité d'une liste dans l'overlay (masqué pour les variables sensibles).
- **Générateur CLI autonome (`ChainCliGenerator`)** :
  - Génère un script exécutable complet reproduisant fidèlement l'enchaînement configuré (en-têtes, authentification, substitution `{{...}}`, itération `[[...]]`, boucles Combinaisons/Parallèle, extractions JSONPath y compris `[n++]`/`[*]`, et extractions Regex) :
    - **Python** (`PythonChainCliEmitter`) : Python 3 utilisant uniquement la bibliothèque standard (`urllib.request`, `json`, `re`, `gzip`, `zlib`, `uuid`).
    - **JavaScript** (`JavaScriptChainCliEmitter`) : Node.js (v18+) utilisant `fetch` natif et `node:crypto`.
    - **PHP** (`PhpChainCliEmitter`) : PHP 8+ CLI utilisant `ext-curl` et `json_*` / `preg_*`.
  - Le script généré s'ouvre dans l'overlay avec un bouton `Copier`.

---

### Onglet Extracteur

Outil interactif pour analyser une réponse et obtenir une expression `JSONPath` ou `Regex` prête à l'emploi.

- **Alimentation et format** :
  - Collage libre dans le champ `Réponse collée` (sans limite de longueur, avec compteur de caractères mis à jour en continu) ou ouverture en un clic depuis `Requête`, `Lot (curl)` ou `Chaîne` via `Extraire une valeur...`.
  - Choix du format : `Auto`, `JSON`, `XML`, `HTML`, `Texte`.
  - En mode `HTML`, une passe de nettoyage tolérante rend le document exploitable sous forme d'arbre XML lorsque c'est possible (gestion des `DOCTYPE`, balises auto-fermantes courantes comme `br`, `img`, `input`, `hr`, `meta`, `link`, `source`). En cas d'échec de parsing XML/JSON, bascule automatique vers le mode sélection manuelle.
- **Mode Arbre (JSON / XML / HTML)** :
  - Construction d'une arborescence interactive des clés, tableaux, éléments et attributs (profondeur JSON jusqu'à 1024).
  - Bouton `Tout déplier` / `Tout replier` placé au-dessus de l'arbre.
  - Cliquer sur un nœud génère immédiatement le `Chemin JSONPath` (pour JSON) et un `Regex alternatif`, teste les deux expressions sur l'intégralité du texte source, indique le nombre d'occurrences et signale si un regex risque de capturer la mauvaise occurrence par rapport à JSONPath.
- **Mode Sélection manuelle (Texte brut ou HTML irrégulier)** :
  - L'utilisateur sélectionne un extrait dans le texte et clique sur `Utiliser la sélection`.
  - Options de génération du motif :
    - Longueur du contexte avant (1 à 100 caractères).
    - Type de valeur capturée : `Chiffres` (`\d+`), `Sans espaces` (`\S+`), `Jusqu'à fin de ligne` (`[^\r\n]+`), ou `Personnalisé` (expression regex libre).
    - Option `Ancrer aussi après la valeur`.
    - Option `En liste [[...]]` : réécrit automatiquement les index numériques JSONPath comme `[0]` en `[*]` et teste l'extraction en mode liste.
- **Intégration avec l'onglet Chaîne** :
  - Permet de choisir l'étape cible dans un menu déroulant et de cliquer sur `Envoyer vers une extraction de chaîne` pour ajouter directement la règle générée (avec son mode liste conservé) dans l'étape sélectionnée.

---

### Éditeur superposé (Overlay)

Accessibles par le bouton d'agrandissement (`Corps de la requête`, `Corps de la réponse`, `Commande curl`, `Liste de valeurs`, `Réponse collée`) ou via `Voir tout` / `Voir` sur les listes extraites et les scripts CLI générés :

- Affichage plein cadre dans la fenêtre principale, sans troncature du contenu.
- Détection automatique de JSON valide (`MaxDepth = 1024`) faisant apparaître le bouton `Indenter le JSON`.
- Bouton `Copier` vers le presse-papiers système.
- Mode lecture seule (`Fermer`) ou édition (`Annuler` / `Enregistrer` mettant à jour la source d'origine et relançant l'analyse si applicable).
- Fermeture par la touche `Échap` ou par clic sur l'arrière-plan assombri.

---

## Syntaxe des variables, listes et JSONPath

### Marqueurs de gabarit (`TemplateEngine`)

Utilisables dans l'URL, les valeurs de paramètres, les valeurs d'en-têtes, le corps et les champs de formulaire :

- `{{maVariable}}` : remplace par la valeur courante d'une variable définie dans le lot/étape ou extraite d'une étape précédente.
- `[[maListe]]` : itère sur chaque valeur d'une liste extraite à une étape précédente (onglet Chaîne).
- Variables prédéfinies :
  - `{{$index}}` : index 1-based de la requête en cours.
  - `{{$guid}}` : nouvel UUID v4 généré pour chaque requête.
  - `{{$timestamp}}` : horodatage Unix UTC en secondes au moment de la substitution.

Règles d'encodage lors de la substitution :

- Dans l'URL : échappement par `Uri.EscapeDataString`.
- Dans un corps de type `JSON` : détection contextuelle de la position à l'intérieur ou à l'extérieur d'une chaîne entre guillemets ; à l'intérieur d'une chaîne JSON, les caractères spéciaux (`"`, `\`, retours à la ligne, tabulations, caractères de contrôle) sont échappés automatiquement sans double-encoder le reste.
- Dans les en-têtes : tout retour à la ligne (`\r` ou `\n`) après substitution est bloqué pour empêcher l'injection d'en-têtes HTTP.

### Syntaxe JSONPath supportée (`ResponseExtractor` et `ResponseRefiner`)

- Préfixe `$` ou `$.` optionnel (ex. `data.items[0].id` équivaut à `$.data.items[0].id`).
- Accès aux propriétés par notation pointée : `objet.sousObjet.champ`.
- Accès aux tableaux par index positif ou négatif : `items[0]`, `items[-1]` (dernier élément), y compris en dimensions imbriquées `matrix[0][1]`.
- Sélecteurs de liste (jokers) utilisables en mode liste `[[...]]` ou dans le filtre de réponse :
  - `[*]`
  - `[]`
  - `[++]`
  - `[ident++]` (ex. `models[n++].avatarUrl`, `items[i++].id`)
  - `[++ident]` (ex. `[++n]`)
- Dans `ResponseRefiner` (onglet Requête), `$` seul formate la racine complète et les sélections produisant des objets ou tableaux sont ré-indentées en JSON valide.

---

## Journalisation, sécurité et limites

- **Logs applicatifs** :
  - Écrits par Serilog dans `%LocalAppData%/ApiTester/logs/log-YYYYMMDD.txt` (sur Windows ; répertoire `SpecialFolder.LocalApplicationData` sous Linux/macOS).
  - Rotation quotidienne avec conservation des 14 derniers fichiers.
  - Niveau minimal `Information`, capture des exceptions non gérées dans `AppDomain.CurrentDomain.UnhandledException`.
- **Masquage des données sensibles** :
  - Toute variable ou paramètre dont le nom contient `token`, `key`, `secret`, `password` ou `auth` (insensible à la casse) voit sa valeur remplacée par `***` dans les résumés UI, les journaux, les aperçus d'extractions et les exports CSV/JSON/JSONL.
  - Les clés d'API passées en paramètre d'URL (`ApiKey` hors en-tête) sont masquées dans les logs HTTP de `HttpService`.
  - Le bouton d'inspection des listes extraites est masqué si la variable est détectée comme sensible.
- **Validation des données** :
  - Les noms de variables extraites doivent correspondre à `^[\p{L}_][\p{L}\p{N}_]*$`.
  - La profondeur maximale de parsing JSON est fixée à `1024` dans l'ensemble des composants (`ResponseViewModel`, `OverlayEditorViewModel`, `ResponseExtractor`, `ResponseRefiner`, `ExtractionSuggester`).
  - Aucune persistance automatique des requêtes ou configurations entre deux lancements n'est implémentée dans cette version (état en mémoire vive uniquement, hormis les réponses exportées via l'enregistrement de lot et les logs Serilog).

---

## Structure complète des fichiers

```text
PROJET_GUI_API/
├── ApiTester.csproj                   Configuration du projet .NET 10 et dépendances NuGet
├── ApiTester.slnx                     Fichier solution
├── Program.cs                         Point d'entrée, configuration Serilog et démarrage Avalonia
├── App.axaml                          Application Avalonia, chargement des styles et ViewLocator
├── App.axaml.cs                       Enregistrement IoC des services, ViewModels et fenêtre principale
├── ViewLocator.cs                     Résolution automatique ViewModel vers View
├── app.manifest                       Manifeste Windows (compatibilité DPI et OS)
├── Assets/
│   └── avalonia-logo.ico              Icône de l'application
├── Converters/
│   └── ViewValueConverters.cs         Convertisseurs de valeurs XAML (méthodes HTTP, statuts, enums, compteurs)
├── Models/
│   ├── AuthConfig.cs                  Modèle de configuration d'authentification
│   ├── BatchAxis.cs                   Représentation d'un axe d'itération (paramètre, variable ou liste)
│   ├── BatchPreviewItem.cs            Modèle d'élément d'aperçu de lot
│   ├── BatchResultItem.cs             Résultat d'une requête exécutée en lot
│   ├── BatchSaveModels.cs             Options d'enregistrement disque des lots
│   ├── BatchVariable.cs               Modèle complet de variable de gabarit (Fixe, Plage, Liste, Aléatoire)
│   ├── ChainEnums.cs                  Énumérations de chaîne (sources, types d'extraction, statuts, langages CLI)
│   ├── ChainExtraction.cs             Règle d'extraction entre étapes (corps/en-tête, JSONPath/Regex, liste)
│   ├── ChainResults.cs                Résultats de planification, d'exécution et variables accumulées de chaîne
│   ├── ChainStep.cs                   Modèle d'une étape de chaîne HTTP
│   ├── CurlParseResult.cs             Résultat du parseur curl et liste d'avertissements
│   ├── Enums.cs                       Méthodes HTTP, types de corps, types d'authentification
│   ├── ExtractorModels.cs             Formats, nœuds d'arbre et suggestions de l'Extracteur
│   ├── KeyValueItem.cs                Paire clé/valeur activable (paramètres, en-têtes, formulaire)
│   ├── RateLimitPolicy.cs             Politique de gestion du rate limit et validation
│   ├── RequestModel.cs                Modèle complet d'une requête HTTP
│   ├── ResponseFilterMode.cs          Modes du filtre d'affinage en ligne (Auto, JsonPath, Regex, XPath)
│   ├── ResponseModel.cs               Modèle complet d'une réponse HTTP reçue
│   └── VaryingParam.cs                Paramètre d'URL pouvant varier sur une plage entière
├── Services/
│   ├── BatchExecutionHelper.cs        Planification, combinaison/zip, masquage de secrets et gestion retry/pause
│   ├── BatchResultWriter.cs           Écriture asynchrone sur disque (fichiers unitaires, JSON, JSONL, CSV)
│   ├── BatchRunner.cs                 Moteur d'exécution et de prévisualisation des lots
│   ├── ChainCliGenerator.cs           Orchestrateur d'export CLI et construction des spécifications
│   ├── ChainRunner.cs                 Moteur d'exécution séquentielle et dynamique des chaînes
│   ├── ClipboardService.cs            Accès au presse-papiers via TopLevel Avalonia
│   ├── CurlParser.cs                  Analyseur multi-shell (POSIX, PowerShell, cmd.exe)
│   ├── ExtractionSuggester.cs         Détection de format, construction d'arbre JSON/XML/HTML et suggestion Regex
│   ├── FolderPickerService.cs         Sélection et ouverture de dossiers système
│   ├── HttpService.cs                 Exécution HTTP asynchrone et chronométrage
│   ├── JavaScriptChainCliEmitter.cs   Générateur de script CLI Node.js autonome
│   ├── NumberCodec.cs                 Conversion et formatage BigInteger en bases 2, 8, 10 et 16
│   ├── OverlayEditorService.cs        Bus d'ouverture de l'éditeur overlay
│   ├── PhpChainCliEmitter.cs          Générateur de script CLI PHP autonome
│   ├── PythonChainCliEmitter.cs       Générateur de script CLI Python 3 autonome
│   ├── RateLimitDetector.cs           Analyse Retry-After, backoff avec gigue et en-têtes de quota
│   ├── ResponseExtractor.cs           Moteur d'extraction JSONPath et Regex (scalaire et liste)
│   ├── ResponseRefiner.cs             Affinage temps réel des réponses (JSONPath, Regex, XPath, Auto)
│   ├── TemplateEngine.cs              Analyse et substitution {{...}} et [[...]]
│   └── I*.cs                          Interfaces associées aux services ci-dessus
├── Styles/
│   ├── Controls.axaml                 Styles des contrôles UI (boutons, onglets, champs, grilles)
│   ├── Icons.axaml                    Ressources géométriques SVG des icônes
│   └── Tokens.axaml                   Thèmes Dark/Light, polices et dimensions
├── ViewModels/
│   ├── BatchViewModel.cs              Logique de présentation de l'onglet Lot (curl)
│   ├── ChainViewModel.cs              Logique de présentation de l'onglet Chaîne et export CLI
│   ├── ExtractorViewModel.cs          Logique de présentation du laboratoire Extracteur
│   ├── MainWindowViewModel.cs         Navigation entre onglets et passerelles inter-vues
│   ├── OverlayEditorViewModel.cs      Logique de l'éditeur superposé et formatage JSON
│   ├── RequestViewModel.cs            Logique de construction de requête
│   ├── ResponseViewModel.cs           Affichage, formatage et affinage de la réponse
│   ├── SingleRequestViewModel.cs      Logique d'envoi et d'annulation unitaire
│   └── ViewModelBase.cs               Classe de base ObservableObject
└── Views/
    ├── BatchOrderPanel.axaml(.cs)     Panneau de réorganisation des axes et aperçu de combinaisons
    ├── BatchPreviewPanel.axaml(.cs)   Panneau d'aperçu des requêtes générées
    ├── BatchVariableEditor.axaml(.cs) Éditeur complet d'une variable de gabarit
    ├── BatchView.axaml(.cs)           Interface de l'onglet Lot (curl)
    ├── ChainExtractionEditor.axaml(.cs) Éditeur des règles d'extraction par étape
    ├── ChainView.axaml(.cs)           Interface de l'onglet Chaîne
    ├── ExtractorView.axaml(.cs)       Interface de l'onglet Extracteur
    ├── KeyValueEditor.axaml(.cs)      Composant réutilisable d'édition clé/valeur
    ├── MainWindow.axaml(.cs)          Fenêtre principale, onglets et hôte de l'overlay
    ├── RequestView.axaml(.cs)         Interface de configuration de requête
    ├── ResponseView.axaml(.cs)        Interface d'affichage de réponse
    ├── SingleRequestView.axaml(.cs)   Interface de l'onglet Requête unitaire
    └── VaryingParamEditor.axaml(.cs)  Éditeur des paramètres d'URL variables
```
