# Firmware pédalier EFM8UB10F16G-QFN28

Ce prototype lit trois sorties analogiques de conditionnement de cellules de charge. Windows reçoit les axes X, Y et Z par USB HID et le jeu utilise directement le pédalier. Le logiciel retrouve les mesures brutes dans un instantané HID Feature et écrit le profil par cette interface. Les rapports Windows font 9 octets en entrée et 57 en Feature, zéro initial compris. Voir le [format actuel](../README.md#hid-reports-version-1).

## Paramètres de la carte

La cible utilise l’alimentation USB et la référence externe sur P0.0/VREF du microcontrôleur.
Le désassemblage de la sauvegarde originale confirme `REF0CN=0x00`, `P0MDIN=0xFE`,
et un gain ADC de 1 pour LC1–LC3. La tension physique de VREF reste à mesurer.
Les entrées de boutons et les quatre axes auxiliaires sortent du périmètre de cette version.

Pour la carte HE SIM PEDALS photographiée, le relevé de continuité et les pistes visibles donnent
**LC1 → U4 broche 16 (P1.6), LC2 → broche 17 (P1.5), LC3 → broche 18 (P1.4)**.
Le script utilise par défaut les canaux **14, 13, 12** dans cet ordre. USB en bas, la rangée supérieure
de U4 se lit 21 à 15 de gauche à droite. Le propriétaire a confirmé que la broche 4 de U1 rejoint
l’avant-dernier contact, la broche 16 de U4. Le rôle de chaque prise se règle dans
l’assistant de calibration. [Brochage QFN28, figure et tableau 6.1](https://www.silabs.com/documents/public/data-sheets/efm8ub1-datasheet.pdf)

| Broche signal U4 | Numéro pour `--mux` |
| --- | --- |
| P0.1 à P0.7 | 1 à 7 (P0.0 réservé à VREF) |
| P1.0 à P1.7 | 8 à 15 |
| P2.0 à P2.3 | 20 à 23 |

## Compilation

Installer Python 3 et SDCC pour MCS-51. Depuis la racine du dépôt, remplacer VID et PID par les
identifiants USB du prototype. Pour un câblage différent, ajouter `--mux LC1 LC2 LC3` avec ses trois
canaux ADC. Le script accepte les nombres décimaux et hexadécimaux (`0x…`). [Compilateur SDCC](https://sdcc.sourceforge.net/)

```text
python firmware/efm8/build.py --vid VID --pid PID
```

Le script produit `firmware/efm8/bin/pedals.ihx` (Intel HEX) et la carte mémoire `pedals.mem`. L’option `--out` choisit un autre répertoire. Le script fait uniquement la compilation. Le plafond de code est de 14 Kio et les données de travail utilisent le premier Kio de XRAM. Le démarrage vérifie la variante QFN28.

La compilation vérifie aussi les sommes de contrôle Intel HEX, le vecteur de démarrage et toutes
les adresses présentes. Elle refuse toute donnée à partir de `0x3800`. Le bootloader sauvegardé
occupe `0x3A00–0x3FFF` et ses pages de sécurité `0xFB80–0xFBFF` doivent rester intactes.
Le contrôle indépendant ci-dessous accepte le fichier de restauration de l’application originale
avec ses réglages à `0xF800–0xFB7F`. Il refuse une image complète contenant le bootloader.

```text
python firmware/efm8/image_check.py chemin/application-et-reglages-sans-bootloader.hex --allow-nv
python -m unittest discover -s firmware/tests -p test_image_check.py
```

Ces commandes contrôlent seulement les fichiers. L'outil de transfert ci-dessous limite aussi chaque
effacement et écriture à ces zones. La programmation de notre application par USB a été testée
sur une carte ; la réécriture de l'original reste à valider.

## Outil USB de transfert et de restauration

L’outil Python fonctionne sous Windows sans paquet externe. Il cible uniquement le pédalier
HE SIM PEDALS sauvegardé, avec le bootloader HID `10C4:8B02`, l’identifiant de puce `0x3241`
et la version de bootloader `0x90`. Le frein à main PIC n’est jamais sélectionné.
Les empreintes des deux sauvegardes BIN sont contrôlées avant toute connexion de transfert.

```text
python firmware/efm8/usb_update.py list
python firmware/efm8/usb_update.py plan firmware/efm8/bin/pedals.ihx
python firmware/efm8/usb_update.py probe --backup-dir DOSSIER_SAUVEGARDE
python firmware/efm8/usb_update.py verify APPLICATION_ORIGINALE.hex --allow-nv --backup-dir DOSSIER_SAUVEGARDE --run
```

`list` lit les métadonnées. `plan` contrôle uniquement le fichier. `probe` identifie le bootloader
et compare les CRC du bootloader et des pages de sécurité à la sauvegarde. `verify` compare
les pages d’application et les pages de réglages présentes dans l’image. Ces quatre modes
n’envoient aucune commande d’effacement ou de programmation. `--run` demande un redémarrage
uniquement après la réussite des contrôles.

Le 16 novembre 2026, `probe` puis `verify` ont passé ces contrôles sur la carte originale.
Le redémarrage a ramené `HE SIM PEDALS`. Plusieurs révisions de notre application ont ensuite été
programmées par USB avec contrôle CRC des zones protégées. L'outil reste expérimental et exige
les empreintes de la sauvegarde de cette carte précise. Le moteur d’écriture a été testé contre un modèle de flash, avec une
coupure simulée avant et après chaque commande.

L’écriture réelle demande la sous-commande `program`, `--execute` et l’empreinte exacte
affichée par `plan`. La commande suivante efface et remplace l’application :

```text
python firmware/efm8/usb_update.py program IMAGE.hex --backup-dir DOSSIER_SAUVEGARDE --execute --sha256 EMPREINTE_HEX --run
```

Pour restaurer l’application originale et ses réglages, utiliser le fichier
`application-et-reglages-sans-bootloader.hex`, son empreinte propre et ajouter `--allow-nv`.
L’image complète contenant le bootloader est systématiquement refusée par cet outil USB.

L’outil efface toutes les 29 pages d’application de 512 octets, y compris les parties inutilisées.
Il ne touche aux pages de données de 64 octets que si l’image les contient intégralement
et si `--allow-nv` est présent. La page zéro est effacée en premier et l’octet zéro reste
à `0xFF` pendant le transfert. Après les contrôles par page et sur l’application complète,
et une nouvelle comparaison des zones protégées, l’octet zéro est programmé en dernier.
Des contrôles supplémentaires suivent cette activation.

Chaque session garde un journal JSONL dans `DOSSIER_SAUVEGARDE/usb-sessions`. Les écritures
ne sont jamais répétées automatiquement. En cas d’erreur, l’outil s’arrête sans démarrer
l’application. Si l’erreur arrive après l’écriture de l’octet zéro, utiliser FW/reset au
branchement pour revenir au bootloader. Aucune commande de verrouillage n’est prise en charge.
Un bootloader endommagé demanderait toujours une restauration par C2.

[Protocole AN945, chapitre 7](https://www.silabs.com/documents/public/application-notes/an945-efm8-factory-bootloader-user-guide.pdf)

## Essai sur banc

Sur une carte d’essai équipée du même composant et d’entrées connues, vérifier d’abord que Windows détecte **OpenPedalControl Pedals**. Dans le logiciel, actionner chaque entrée séparément, régler les bornes et appliquer une courbe reconnaissable. Contrôler les trois axes dans `joy.cpl`, fermer le logiciel, débrancher puis rebrancher, et contrôler de nouveau les axes et la relecture du profil. L'énumération DirectInput, les trois entrées indépendantes, la relecture du profil et sa persistance
après redémarrage logiciel ont été contrôlées sur la carte. Une courbe non linéaire sauvegardée a produit
60 échantillons conformes au calcul PC. La coupure d'alimentation après sauvegarde et la calibration
complète par l'utilisateur restent à vérifier.

En veille USB, le prototype laisse le processeur et l’ADC alimentés. Le courant du montage en veille et la reprise après suspension de Windows restent à mesurer sur le banc.

L’ADC reprend les paramètres observés dans l’original pour LC1–LC3 : `ADC0CF=0x13`,
`ADC0AC=0x82`, `ADC0PWR=0x4F` et `ADC0TK=0xBF`. Il accumule deux résultats 12 bits,
puis le prototype les moyenne pour retrouver une plage native de 0 à 4092.
Les valeurs USB et la calibration couvrent 0 à 4095. La première configuration est linéaire.
Le firmware conserve le profil dans deux pages de données non volatiles de 64 octets,
à `0xF800` et `0xF840`. L'écriture vise l'autre page, contrôle les données et termine par un
octet de validation pour permettre la reprise après une coupure. Sur la carte d’origine,
ces pages contiennent des réglages sauvegardés dans le paquet de restauration.
[Manuel de référence EFM8UB1, mémoire flash, ADC0 et USB0](https://www.silabs.com/documents/public/reference-manuals/efm8ub1-rm.pdf)
