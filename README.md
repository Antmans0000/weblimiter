# WebLimiter

Surveillance, limitation de débit, blocage et quotas du trafic réseau **par application** sous Windows,
dans l'esprit de NetLimiter.

## ⬇️ [Télécharger l'installateur (WebLimiter-Setup.exe)](https://github.com/Antmans0000/weblimiter/releases/latest/download/WebLimiter-Setup.exe)

Un clic télécharge le setup, il suffit de le lancer (Windows 10/11 64 bits, aucun prérequis : .NET est inclus).
Le setup n'est pas signé : Windows SmartScreen peut afficher un avertissement
(« Informations complémentaires » → « Exécuter quand même »). Versions précédentes : [Releases](../../releases).

## Fonctions

- **Activité** : liste des applications avec débits ↓/↑ en temps réel, consommation du jour, nombre de connexions,
  détail des connexions (adresse, hôte via DNS inverse, ports, débits), graphe des 2 dernières minutes.
- **Limiteur** : limite en Ko/s par application, séparément en réception et en envoi.
- **Blocage** : coupe tout le trafic d'une application.
- **Quotas** : volume par jour ou par mois (total, reçu ou envoyé) ; une fois atteint, blocage ou ralentissement
  jusqu'à la période suivante.
- **Statistiques** : historique journalier par application (aujourd'hui, hier, 7 jours, mois, 12 mois).
- **Exclusions** : blocage direct des mises à jour Windows (Windows Update, BITS, Optimisation de livraison,
  Orchestrateur, Medic), liste de tous les services et des applications de la machine avec un interrupteur de blocage,
  ajout manuel d'un programme.
- **Service Windows** (onglet Paramètres) : le moteur peut tourner dans le service `WebLimiter` (compte Système,
  démarrage automatique) pour que le filtrage continue interface fermée.
- Zone de notification, démarrage avec Windows (tâche planifiée avec privilèges élevés), instance unique.

## Architecture

```
src/WebLimiter.Core   moteur (aucune dépendance UI)
  Backend/              IBackend : moteur local ou service (canal nommé), hôte de service Windows
  Engine/ServiceCatalog services Windows, PID → services, détection Windows Update
  Native/WinDivert.cs   P/Invoke WinDivert 2.2
  Native/Win32.cs       chemin des processus, tables TCP/UDP (iphlpapi)
  Engine/TrafficEngine  capture → attribution → comptage → blocage / mise en file / réinjection
  Engine/Shaper         seau de jetons + file d'attente bornée (une par appli et par sens)
  Engine/PortOwnerTable (protocole, port local) → PID
  Storage/              règles et statistiques en JSON dans %ProgramData%\WebLimiter
src/WebLimiter.App    interface WPF (.NET 8), thème sombre
tests/WebLimiter.Smoke  tests de bout en bout (admin) : curl limité / bloqué, `bits` (service BITS), `service` (via le canal nommé)
native/x64            WinDivert.dll + WinDivert64.sys (pilote signé)
```

**Services Windows.** La liste des services est lue auprès du gestionnaire de services (`EnumServicesStatusEx`)
et du registre ; rien n'est codé en dur. Un processus `svchost.exe` est identifié par les services qu'il héberge
(clé `svc:BITS`, `svc:DoSvc`…), ce qui permet de bloquer un service sans bloquer les autres. Les services de mise à jour
sont reconnus par la DLL qu'ils chargent (`wuaueng.dll`, `qmgr.dll`, `dosvc.dll`, `usosvc.dll`, `waasmedicsvc.dll`),
quelle que soit la langue du système. Une règle posée sur `svchost.exe` lui-même s'applique à tous ses services.

**Mode service.** `WebLimiter.exe --service` héberge le moteur sous le contrôle du gestionnaire de services ;
l'interface s'y connecte par le canal nommé `\\.\pipe\WebLimiter` (accès réservé à SYSTEM et aux administrateurs).
Sans service installé, le moteur tourne dans l'interface.

**Comment ça marche.** NetLimiter utilise son propre pilote noyau (`nldrv.sys`). WebLimiter s'appuie sur
[WinDivert](https://reqrypt.org/windivert.html), un pilote WFP déjà signé : tous les paquets IP non-loopback
remontent en mode utilisateur, sont attribués à un processus, comptés, puis réinjectés, retardés (limite) ou
jetés (blocage). Retarder et jeter les paquets fait converger TCP vers le débit voulu.
L'attribution utilise les événements de la couche SOCKET de WinDivert (bind/connect/listen/accept), complétés par
les tables `GetExtendedTcpTable` / `GetExtendedUdpTable` pour les sockets ouverts avant le démarrage.

Si WebLimiter plante ou est fermé, le handle WinDivert se ferme et le trafic repasse normalement.

## Compiler

SDK .NET 8 requis.

```bash
dotnet build WebLimiter.sln -c Release
```

L'exécutable est `src/WebLimiter.App/bin/Release/net8.0-windows/WebLimiter.exe` (demande les droits admin).
Options : `--tray` (démarre réduit dans la zone de notification), `--service` (réservé au gestionnaire de services).

## Installateur

`installer/build.ps1` publie l'application en autonome (.NET inclus) puis compile `installer/WebLimiter.iss`
avec [Inno Setup 6](https://jrsoftware.org/isinfo.php), attendu dans `tools/innosetup/`.
Résultat : `installer/Output/WebLimiter-Setup-<version>.exe`, qui installe l'application dans Program Files
et le service `WebLimiter`. À chaque release, joindre aussi une copie nommée `WebLimiter-Setup.exe` :
c'est elle que vise le lien de téléchargement direct (`releases/latest/download/WebLimiter-Setup.exe`).

## Limites connues

- **Antivirus avec bouclier web (Avast ici)** : son pilote ré-émet les connexions sous le PID 4 (System) ;
  WebLimiter ignore ces doublons et garde le propriétaire réel.
- Si Windows regroupe plusieurs services dans un même `svchost` (machines avec peu de mémoire), les bloquer
  un par un est impossible : ils sont coupés ensemble (l'onglet Exclusions indique « partagé avec N »).
- Une limite de débit est une moyenne : la précision est d'environ 90 % sur 10 s (montée en charge TCP).
- Sans le service installé, fermer complètement WebLimiter arrête limites, blocages et quotas.

## Pistes pour la suite

- Limites globales et priorités entre applications.
- Mode pare-feu interactif (demander à l'utilisateur à la première connexion d'une application).
- Règles par connexion / par adresse distante.
- Installateur (MSI/Inno Setup) et signature de code.

## Licences

WinDivert est distribué sous LGPL v3 (ou GPL v2) — voir `native/LICENSE-WinDivert.txt`. L'utiliser comme DLL
séparée, sans la modifier, est compatible avec un logiciel propriétaire ; la licence doit être fournie avec le produit.
