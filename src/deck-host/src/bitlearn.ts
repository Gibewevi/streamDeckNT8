/**
 * Lien avec Bitlearn — appairage du poste et récupération de la disposition.
 *
 * Bitlearn est l'**éditeur** du layout, cet hôte en est le **moteur**. Le sens est unique : on
 * récupère, on n'envoie jamais. Il n'y a donc aucun conflit possible entre deux sources, et le
 * `layout.json` local cesse d'être une source pour devenir un **cache** — il sert uniquement à
 * démarrer quand Bitlearn est injoignable.
 *
 * Règle qui prime sur tout le reste : **rien ici ne doit pouvoir empêcher de trader.** Une panne
 * réseau, un VPS en cours de déploiement, un certificat expiré — tout cela laisse le deck
 * fonctionner sur la dernière disposition connue. Seul un `401` explicite signifie quelque chose,
 * et même lui n'arrête pas l'envoi d'ordres : il coupe la synchronisation, pas le trading.
 */
import { readFileSync, writeFileSync, mkdirSync, existsSync, unlinkSync } from 'fs';
import { dirname, join } from 'path';
import { createServer } from 'http';
import { randomBytes } from 'crypto';
import { spawn } from 'child_process';
import { AddressInfo } from 'net';
import { DeckStateReport } from './messages.js';
import { DEFAULT_DATA_DIR, Layout, LayoutStore, validateLayout } from './layout.js';
import * as log from './logger.js';

/**
 * Serveur Bitlearn visé, par ordre de priorité : `--bitlearn <url>`, puis
 * `TRADEDECK_BITLEARN_URL`, puis `bitlearn.json` à côté de l'état, puis la production.
 *
 * L'argument existe parce que la variable d'environnement se perd trop facilement — une autre
 * fenêtre, une syntaxe `set` au lieu de `$env:`, un lanceur intermédiaire — et qu'un oubli fait
 * viser la production **en silence**. L'argument, lui, est dans la commande qu'on relit.
 *
 * Le fichier existe pour l'installation : la tâche planifiée lance `node dist\\host.js` sans
 * argument, et une tâche ne reçoit pas les variables d'environnement d'une session. Sans lui, un
 * poste installé ne peut viser que la production — impossible d'en éprouver un contre un serveur
 * de développement.
 */
function readConfiguredUrl(): string | undefined {
  try {
    const path = join(DEFAULT_DATA_DIR, 'bitlearn.json');
    if (!existsSync(path)) return undefined;
    const parsed = JSON.parse(readFileSync(path, 'utf8')) as { url?: string };
    return typeof parsed?.url === 'string' && parsed.url ? parsed.url : undefined;
  } catch {
    // Un fichier illisible ne doit pas empêcher l'hôte de démarrer : on retombe sur la production.
    return undefined;
  }
}

function resolveBaseUrl(): string {
  const argv = process.argv.slice(2);
  const index = argv.indexOf('--bitlearn');
  const fromArg = index >= 0 ? argv[index + 1] : undefined;
  const raw = fromArg || process.env.TRADEDECK_BITLEARN_URL || readConfiguredUrl();
  if (!raw) return 'https://bitlearn.fr';

  try {
    // Normalisée pour que `http://localhost:3000/` et `http://localhost:3000` construisent la
    // même URL : un double slash au milieu d'un chemin donne un 404 difficile à relier à sa cause.
    return new URL(raw).origin;
  } catch {
    log.eventWarn('Bitlearn', 'Adresse de serveur illisible — retour à la production', { valeur: raw });
    return 'https://bitlearn.fr';
  }
}

const BASE_URL = resolveBaseUrl();

/**
 * Le jeton vit hors du dossier d'installation : une mise à jour qui remplace
 * `%LOCALAPPDATA%\TradeDeck` ne doit pas obliger à ré-appairer le poste.
 *
 * Et hors du profil **itinérant**, contrairement au reste de l'état. `%APPDATA%` suit
 * l'utilisateur d'une machine à l'autre sur un profil itinérant : le jeton y voyageait avec lui,
 * deux postes physiques se retrouvaient à partager une seule identité d'appareil, et la limite de
 * trois appareils cessait de vouloir dire quelque chose. La disposition et les journaux ont de
 * bonnes raisons de suivre l'utilisateur — ce jeton-là, non : il désigne une machine.
 */
const TOKEN_PATH = join(process.env.LOCALAPPDATA || DEFAULT_DATA_DIR, 'StreamDeckTrader', 'device.json');

/**
 * L'ancien logement, itinérant. Lu une seule fois, puis déplacé.
 *
 * Sans cette reprise, la mise à jour désapparierait tous les postes existants. Et l'ancien fichier
 * est bien **supprimé** après recopie : le laisser en ferait la source qu'un autre poste du même
 * profil lirait, et le défaut ne serait pas corrigé mais doublé.
 */
const TOKEN_PATH_ITINERANT = join(DEFAULT_DATA_DIR, 'device.json');

/**
 * Cadence du battement d'état.
 *
 * Ce n'est plus lui qui apporte les modifications — l'attente longue s'en charge et les applique
 * en quelques dizaines de millisecondes. Il ne sert plus qu'à deux choses : alimenter les voyants
 * de l'éditeur (boîtier, bridge, NinjaTrader), et rattraper la disposition si l'attente longue
 * venait à échouer durablement.
 *
 * 5 s : les voyants doivent rester crédibles — l'éditeur considère un état de plus de 15 s comme
 * périmé — sans pour autant produire du trafic pour rien.
 */
const POLL_INTERVAL_MS = 5_000;

/** Au-delà, on considère que Bitlearn ne répond pas — le deck ne doit pas attendre. */
const REQUEST_TIMEOUT_MS = 8_000;

/**
 * La requête d'attente reste ouverte jusqu'à 20 s côté serveur : son délai doit être plus long,
 * sinon on la coupe systématiquement juste avant sa réponse.
 */
const WATCH_TIMEOUT_MS = 30_000;

/** Pause avant de rouvrir l'attente quand elle a échoué — évite de marteler un serveur absent. */
const WATCH_RETRY_MS = 5_000;

/** Laisse le temps de se connecter à Bitlearn, sans laisser un écouteur ouvert indéfiniment. */
const PAIRING_TIMEOUT_MS = 5 * 60_000;

const PAIRING_PATH = '/callback';

/** Réponse affichée dans l'onglet à la fin — c'est la dernière chose que l'utilisateur voit. */
function pageDeRetour(titre: string, message: string): string {
  const esc = (s: string) => s.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]!));
  return `<!doctype html><html lang="fr"><head><meta charset="utf-8">
<title>TradeDeck</title><style>
body{background:#0f172a;color:#e2e8f0;font:16px/1.6 system-ui,sans-serif;display:flex;
min-height:100vh;align-items:center;justify-content:center;margin:0;text-align:center}
div{max-width:26rem;padding:2rem}h1{font-size:1.5rem;margin:0 0 .75rem}p{color:#94a3b8;margin:0}
</style></head><body><div><h1>${esc(titre)}</h1><p>${esc(message)}</p></div></body></html>`;
}

interface StoredDevice {
  token: string;
  deviceId?: number;
  pairedAt?: string;
}

/** Ce que l'hôte remonte à chaque battement, et la grille pour laquelle il demande sa disposition. */
export interface SyncContext {
  columns: number;
  rows: number;
  status: {
    deck: boolean;
    deckModel: string;
    bridge: boolean;
    nt: boolean;
    /** Pourquoi `nt` est faux : plateforme absente, add-on non déposé, incomplet, ou déposé. */
    ntAddon: string;
    appVersion: string;
  };
  /**
   * L'état vivant, pour que l'éditeur montre ce que montre le boîtier — macro armée, compte et
   * instrument courants, temporisation, pause, Auto BE.
   *
   * Absent tant que le bridge n'a rien publié : mieux vaut un éditeur qui n'affiche pas d'état
   * qu'un éditeur qui affiche un état inventé.
   */
  etat?: DeckStateReport;
}

/**
 * Ouvre l'URL dans le navigateur par défaut.
 *
 * `rundll32 url.dll,FileProtocolHandler` plutôt que `cmd /c start` : `start` traite son premier
 * argument entre guillemets comme un titre de fenêtre et découpe l'URL sur les `&`, ce qui casse
 * précisément les URL à plusieurs paramètres comme la nôtre.
 *
 * L'échec n'est pas bloquant : l'URL est aussi imprimée sur la console, l'utilisateur peut la
 * coller lui-même.
 */
const pause = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms).unref?.());

function ouvrirDansLeNavigateur(url: string): void {
  try {
    const child = spawn('rundll32', ['url.dll,FileProtocolHandler', url], {
      detached: true,
      stdio: 'ignore',
    });
    child.on('error', (err) => log.eventWarn('Bitlearn', 'Navigateur non ouvert automatiquement', { raison: err.message }));
    child.unref();
  } catch (err) {
    log.eventWarn('Bitlearn', 'Navigateur non ouvert automatiquement', { raison: (err as Error)?.message });
  }
}

export class BitlearnClient {
  #token: string | null = null;
  /** Destinataire de la clé de scellement rendue à l'appairage. Voir `onJournalKey`. */
  #surCle: ((cleHex: string) => void) | null = null;
  #timer: NodeJS.Timeout | null = null;
  /** Signature de la dernière disposition appliquée — évite de réécrire un fichier identique. */
  #lastApplied = '';
  /** Empêche de répéter le même avertissement à chaque tour dans le journal. */
  #lastFailure = '';
  /** Empreinte de la disposition détenue — l'attente longue ne répond que si elle a changé. */
  #since: string | null = null;
  /** Coupe la boucle d'attente : sans ce drapeau, elle survivrait à l'arrêt de l'hôte. */
  #stopped = false;

  constructor() {
    this.#token = this.#loadToken();
    // Journalisé au démarrage : une variable d'environnement oubliée fait viser la production
    // silencieusement, et l'erreur ne se manifeste qu'en 404 sur une page d'appairage absente.
    // Mieux vaut lire la cible dans le journal que la déduire d'un code HTTP.
    log.event('Bitlearn', 'Serveur ciblé', { url: BASE_URL, appairé: this.#token !== null });
  }

  get paired(): boolean {
    return this.#token !== null;
  }

  /**
   * Déplace le jeton du profil itinérant vers le profil local, une fois.
   *
   * Rien ici n'a le droit d'empêcher le démarrage : un déplacement impossible — fichier verrouillé,
   * dossier en lecture seule — laisse simplement le jeton où il est, et l'ancien chemin reste lu.
   */
  #migrerJeton(): void {
    if (existsSync(TOKEN_PATH) || !existsSync(TOKEN_PATH_ITINERANT)) return;
    try {
      mkdirSync(dirname(TOKEN_PATH), { recursive: true });
      writeFileSync(TOKEN_PATH, readFileSync(TOKEN_PATH_ITINERANT, 'utf8'), 'utf8');
      unlinkSync(TOKEN_PATH_ITINERANT);
      log.event('Bitlearn', 'Jeton d\'appareil déplacé hors du profil itinérant', { vers: TOKEN_PATH });
    } catch (err) {
      log.eventWarn('Bitlearn', 'Jeton d\'appareil non déplacé — l\'ancien emplacement reste utilisé', {
        raison: (err as Error)?.message,
      });
    }
  }

  #loadToken(): string | null {
    this.#migrerJeton();

    // L'ancien chemin sert de repli tant que le déplacement n'a pas pu se faire : sans lui, un
    // poste dont le profil refuse l'écriture se retrouverait désapparié à la mise à jour.
    const chemin = existsSync(TOKEN_PATH) ? TOKEN_PATH : TOKEN_PATH_ITINERANT;
    if (!existsSync(chemin)) return null;

    try {
      const stored = JSON.parse(readFileSync(chemin, 'utf8')) as StoredDevice;
      return typeof stored?.token === 'string' && stored.token ? stored.token : null;
    } catch (err) {
      // Un fichier illisible ne doit pas empêcher l'hôte de démarrer : le poste se comporte
      // simplement comme non appairé, et l'utilisateur peut le relier à nouveau.
      log.fail('Bitlearn', err, 'Jeton d\'appareil illisible — poste considéré comme non appairé');
      return null;
    }
  }

  #saveToken(device: StoredDevice): void {
    mkdirSync(dirname(TOKEN_PATH), { recursive: true });
    writeFileSync(TOKEN_PATH, JSON.stringify(device, null, 2), 'utf8');
    this.#token = device.token;
  }

  /**
   * Échange un code d'appairage contre un jeton d'appareil. Appelé une seule fois dans la vie de
   * l'installation : le jeton n'expire pas, seul Bitlearn peut le révoquer.
   */
  async pair(code: string, deviceName: string, appVersion: string): Promise<void> {
    const response = await this.#request('/api/tradedeck/devices/pair', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code, deviceName, appVersion }),
    }, false);

    const payload = await response.json() as {
      token?: string; journalKey?: string; device?: { id?: number }; error?: string;
    };
    if (!response.ok || !payload?.token) {
      throw new Error(payload?.error || `Appairage refusé (${response.status})`);
    }

    this.#saveToken({
      token: payload.token,
      deviceId: payload.device?.id,
      pairedAt: new Date().toISOString(),
    });

    // La clé de scellement n'est rendue qu'ici, comme le jeton. Un serveur d'ancienne version ne
    // l'envoie pas : le poste continue alors de synchroniser sans sceau, et ses séances tombent au
    // palier non vérifiable côté Bitlearn — dégradé, jamais cassé.
    if (typeof payload.journalKey === 'string' && payload.journalKey) {
      this.#surCle?.(payload.journalKey);
    } else {
      log.eventWarn('Bitlearn', 'Aucune clé de scellement reçue — le journal partira non scellé');
    }

    log.event('Bitlearn', 'Poste appairé', { deviceId: payload.device?.id, scelle: Boolean(payload.journalKey) });
  }

  /**
   * Branche le destinataire de la clé de scellement.
   *
   * Une callback plutôt qu'une dépendance directe sur le journal : ce client sert aussi à la
   * disposition et au battement, et rien ici ne doit avoir à connaître les spools.
   */
  onJournalKey(handler: (cleHex: string) => void): void {
    this.#surCle = handler;
  }

  /**
   * Appairage sans rien à recopier : on ouvre un écouteur temporaire, on envoie l'utilisateur sur
   * Bitlearn, et Bitlearn le renvoie ici avec un code.
   *
   * Le sens compte : c'est **l'application qui ouvre le navigateur**, jamais l'inverse. Laisser
   * une page web s'adresser à un serveur local reviendrait à ouvrir la porte que le contrôle
   * d'`Origin` du serveur de configuration ferme délibérément — et une page HTTPS ne peut de
   * toute façon pas appeler du HTTP local sans être bloquée pour contenu mixte. Une redirection,
   * elle, passe.
   *
   * N'échoue jamais bruyamment : un appairage raté laisse simplement le poste non appairé, avec
   * la disposition locale. Le trading n'en dépend pas.
   */
  async requestPairing(deviceName: string, appVersion: string): Promise<boolean> {
    if (this.#token) return true;

    // Lie la réponse à *cette* demande : sans lui, un autre programme tournant sur la machine
    // pourrait présenter son propre code à notre écouteur.
    const state = randomBytes(24).toString('base64url');

    return new Promise<boolean>((resolve) => {
      let settled = false;
      const finish = (ok: boolean) => {
        if (settled) return;
        settled = true;
        clearTimeout(timeout);
        server.close();
        resolve(ok);
      };

      const server = createServer(async (req, res) => {
        const url = new URL(req.url || '/', `http://127.0.0.1`);
        if (url.pathname !== PAIRING_PATH) {
          res.writeHead(404).end();
          return;
        }

        const code = url.searchParams.get('code') || '';
        if (url.searchParams.get('state') !== state) {
          // Ni le code ni l'identité ne sont sûrs : on refuse sans rien tenter.
          log.eventWarn('Bitlearn', 'Retour d\'appairage rejeté — state inattendu');
          res.writeHead(400, { 'Content-Type': 'text/html; charset=utf-8' })
            .end(pageDeRetour('Demande rejetée', 'Cette réponse ne correspond pas à la demande en cours. Relancez TradeDeck.'));
          finish(false);
          return;
        }

        try {
          await this.pair(code, deviceName, appVersion);
          // Renvoyé vers l'interface plutôt que laissé sur une page morte : le poste vient
          // d'être lié, la suite se passe entièrement sur Bitlearn. Lui demander de fermer
          // l'onglet le laissait devant une impasse, sans lui dire où aller.
          res.writeHead(302, { Location: `${BASE_URL}/tradedeck/configuration?appaire=1` }).end();
          finish(true);
        } catch (err) {
          log.fail('Bitlearn', err, 'Échange du code d\'appairage refusé');
          res.writeHead(400, { 'Content-Type': 'text/html; charset=utf-8' })
            .end(pageDeRetour('Appairage refusé', (err as Error)?.message || 'Réessayez depuis TradeDeck.'));
          finish(false);
        }
      });

      const timeout = setTimeout(() => {
        log.eventWarn('Bitlearn', 'Appairage abandonné — aucune réponse dans le délai imparti');
        finish(false);
      }, PAIRING_TIMEOUT_MS);
      timeout.unref?.();

      server.on('error', (err) => {
        log.fail('Bitlearn', err, 'Écouteur d\'appairage impossible à ouvrir');
        finish(false);
      });

      // Port 0 : le système en attribue un libre. Écoute sur 127.0.0.1 uniquement — rien de tout
      // ceci n'a à être joignable depuis le réseau.
      server.listen(0, '127.0.0.1', () => {
        const port = (server.address() as AddressInfo).port;
        const callback = `http://127.0.0.1:${port}${PAIRING_PATH}`;
        const target = `${BASE_URL}/tradedeck/pair?callback=${encodeURIComponent(callback)}&state=${encodeURIComponent(state)}`;

        log.event('Bitlearn', 'Appairage demandé — ouverture du navigateur', { port, serveur: BASE_URL });
        ouvrirDansLeNavigateur(target);
        // Affiché en plus de l'ouverture automatique : `rundll32` ouvre le navigateur *par
        // défaut* de Windows, qui n'est pas forcément celui où l'utilisateur est connecté à
        // Bitlearn. Coller l'adresse ailleurs marche — l'écouteur ne regarde pas d'où vient le
        // retour.
        process.stdout.write(
          `\n  Serveur Bitlearn : ${BASE_URL}\n` +
          `  Autorisez ce poste dans le navigateur où vous êtes connecté :\n\n  ${target}\n\n`
        );
      });
    });
  }

  /**
   * Envoie un lot de journal — exécutions et événements comportementaux.
   *
   * Rend `true` seulement si Bitlearn a accusé réception. L'appelant n'avance son curseur qu'à
   * cette condition : réémettre est gratuit — les index uniques côté serveur rendent un doublon
   * impossible — alors que perdre une exécution est définitif.
   */
  /**
   * `soldes` : cash value par nom de compte, telle que NinjaTrader la publie à l'instant de l'envoi.
   *
   * Voyage avec le journal plutôt que dans le battement, parce que c'est le journal qui crée les
   * comptes côté Bitlearn — le solde doit arriver en même temps que les trades qu'il explique, pas
   * cinq secondes avant ou après.
   */
  async sendJournal(lot: {
    executions: unknown[];
    events: unknown[];
    /** Échantillons de solde scellés — le capital de référence de l'XP et sa réconciliation. */
    balances?: unknown[];
    soldes?: Record<string, number>;
  }): Promise<boolean> {
    if (!this.#token) return false;

    try {
      const response = await this.#request('/api/tradedeck/journal', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(lot),
      }, true);

      if (response.status === 401 || response.status === 403) {
        this.#warnOnce('unauthorized', 'Poste non autorisé — journal non envoyé');
        return false;
      }
      if (!response.ok) {
        this.#warnOnce(`journal-${response.status}`, 'Bitlearn a refusé le lot de journal', { statut: response.status });
        return false;
      }

      this.#lastFailure = '';
      return true;
    } catch (err) {
      this.#warnOnce('journal-reseau', 'Journal non envoyé — Bitlearn injoignable', {
        raison: (err as Error)?.message,
      });
      return false;
    }
  }

  /**
   * Battement : remonte l'état de la chaîne et récupère la disposition, en un seul échange.
   *
   * Les deux ensemble plutôt qu'en deux requêtes — à trois secondes d'intervalle, dédoubler
   * l'aller-retour pour trois booléens double le trafic sans rien apporter, et les deux moitiés
   * pourraient se désynchroniser.
   *
   * Retourne `null` sur tout échec — appelant averti : ce n'est pas une erreur, c'est
   * « garde ce que tu as ».
   */
  async syncLayout(context: SyncContext): Promise<Layout | null> {
    if (!this.#token) return null;

    try {
      const response = await this.#request(
        '/api/tradedeck/sync',
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            columns: context.columns,
            rows: context.rows,
            status: context.status,
            etat: context.etat,
          }),
        },
        true
      );

      if (response.status === 401 || response.status === 403) {
        // Le seul cas où l'on sait avec certitude que la synchronisation est finie : appareil
        // révoqué ou habilitation retirée. On cesse d'interroger, mais le deck continue.
        this.#stopPolling();
        this.#warnOnce('unauthorized', 'Poste non autorisé par Bitlearn — synchronisation arrêtée, le deck continue de fonctionner');
        return null;
      }

      if (!response.ok) {
        this.#warnOnce(`http-${response.status}`, 'Bitlearn a refusé la requête de disposition', { statut: response.status });
        return null;
      }

      const payload = await response.json() as { layout?: unknown; journalKey?: string };

      // Rattrapage : un poste appairé avant le scellement n'a pas de clé, et Bitlearn la lui rend
      // ici, une seule fois. Sans ça il resterait au palier non vérifiable pour toujours — son
      // journal partirait bien, mais son XP serait dérisoire sans que rien ne dise pourquoi.
      //
      // Le destinataire ignore une clé qu'il possède déjà : la réinstaller casserait la chaîne en
      // cours pour rien.
      if (typeof payload?.journalKey === 'string' && payload.journalKey) {
        this.#surCle?.(payload.journalKey);
      }

      // Validé même venant de Bitlearn : une disposition sans page fige le boîtier, et la
      // provenance ne rend pas un document correct.
      const layout = validateLayout(payload?.layout);
      this.#lastFailure = '';
      return layout;
    } catch (err) {
      this.#warnOnce('network', 'Bitlearn injoignable — la disposition en cache reste appliquée', {
        raison: (err as Error)?.message,
      });
      return null;
    }
  }

  /**
   * Démarre la synchronisation. Deux mécanismes, chacun pour ce qu'il fait de mieux :
   *
   *   - **l'attente longue** (`/watch`) applique une modification dès qu'elle est enregistrée,
   *     en quelques dizaines de millisecondes, et ne coûte rien tant que rien ne bouge ;
   *   - **le battement** (`/sync`, toutes les 5 s) remonte l'état de la chaîne pour les voyants
   *     de l'éditeur, et sert de filet : si l'attente meurt sans se relever, la disposition
   *     finit quand même par arriver.
   *
   * `getContext` est relu à chaque tour plutôt que capturé une fois : le boîtier peut être
   * branché après le démarrage, NinjaTrader se connecter en cours de séance, et c'est justement
   * cet état changeant que l'on remonte.
   */
  startLayoutSync(store: LayoutStore, getContext: () => SyncContext): void {
    if (!this.#token || this.#timer) return;

    const battement = async () => {
      const context = getContext();
      const layout = await this.syncLayout(context);
      if (layout) this.#applyLayout(store, layout, context);
    };

    void battement();
    this.#timer = setInterval(() => void battement(), POLL_INTERVAL_MS);
    // Ce minuteur ne doit pas à lui seul maintenir le processus en vie à l'arrêt.
    this.#timer.unref?.();

    void this.#watchLoop(store, getContext);
  }

  /**
   * Boucle d'attente longue. Chaque requête reste ouverte jusqu'à ce que la disposition change,
   * puis on rouvre aussitôt.
   *
   * Volontairement sans limite de tentatives : un VPS en cours de déploiement revient, et un
   * hôte qui aurait cessé d'attendre resterait muet jusqu'à son propre redémarrage.
   */
  async #watchLoop(store: LayoutStore, getContext: () => SyncContext): Promise<void> {
    while (!this.#stopped && this.#token) {
      const context = getContext();
      try {
        const url = `/api/tradedeck/watch?columns=${context.columns}&rows=${context.rows}`
          + (this.#since ? `&since=${encodeURIComponent(this.#since)}` : '');
        const response = await this.#request(url, { method: 'GET' }, true, WATCH_TIMEOUT_MS);

        if (response.status === 401 || response.status === 403) {
          this.#warnOnce('unauthorized', 'Poste non autorisé par Bitlearn — synchronisation arrêtée, le deck continue de fonctionner');
          this.stop();
          return;
        }

        // 204 : rien n'a changé pendant l'attente. On rouvre immédiatement, sans pause — c'est
        // le cas normal, pas un échec.
        if (response.status === 204) continue;

        if (!response.ok) {
          await pause(WATCH_RETRY_MS);
          continue;
        }

        const payload = await response.json() as { layout?: unknown; since?: string };
        this.#since = payload?.since ?? null;
        const layout = validateLayout(payload?.layout);
        this.#applyLayout(store, layout, context);
      } catch {
        // Coupure, délai dépassé, serveur absent : on retente. Le battement continue de son
        // côté, donc la disposition finit par arriver même si l'attente reste en échec.
        await pause(WATCH_RETRY_MS);
      }
    }
  }

  /** Point de passage unique : la comparaison de signature évite de réécrire un layout identique. */
  #applyLayout(store: LayoutStore, layout: Layout, context: SyncContext): void {
    const signature = JSON.stringify(layout);
    if (signature === this.#lastApplied) return;

    this.#lastApplied = signature;
    // `update` écrit le cache local et notifie l'hôte, qui redessine. C'est le même chemin que
    // l'ancienne interface locale empruntait — rien de nouveau côté rendu.
    store.update(layout);
    log.event('Bitlearn', 'Disposition mise à jour depuis Bitlearn', {
      pages: layout.pages.length, colonnes: context.columns, lignes: context.rows,
    });
  }

  stop(): void {
    this.#stopped = true;
    this.#stopPolling();
  }

  #stopPolling(): void {
    if (this.#timer) {
      clearInterval(this.#timer);
      this.#timer = null;
    }
  }

  async #request(path: string, init: RequestInit, authenticated: boolean, timeoutMs = REQUEST_TIMEOUT_MS): Promise<Response> {
    const headers = new Headers(init.headers);
    if (authenticated && this.#token) headers.set('Authorization', `Bearer ${this.#token}`);

    // Sans délai maximal, une connexion qui n'aboutit jamais laisse la promesse en suspens et le
    // cycle de synchronisation ne repart plus. L'attente longue en demande un plus large que les
    // autres appels, d'où le paramètre.
    const abort = AbortSignal.timeout(timeoutMs);
    return fetch(`${BASE_URL}${path}`, { ...init, headers, signal: abort });
  }

  /**
   * Un avertissement par cause, pas un toutes les 30 s. Un VPS arrêté une nuit produirait
   * autrement un millier de lignes identiques, qui noieraient ce qui compte vraiment.
   */
  #warnOnce(kind: string, message: string, ctx?: Record<string, unknown>): void {
    if (this.#lastFailure === kind) return;
    this.#lastFailure = kind;
    log.eventWarn('Bitlearn', message, ctx);
  }
}
