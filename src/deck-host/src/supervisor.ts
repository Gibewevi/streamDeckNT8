/**
 * Supervision du bridge.
 *
 * C'était le plugin Elgato qui lançait `StreamDeckBridge.exe` (`plugin.ts:1039-1062`), en
 * fire-and-forget : détaché, sorties ignorées, jamais arrêté ni surveillé. En supprimant
 * l'application Stream Deck, plus personne ne le démarre — l'hôte doit reprendre ce rôle.
 *
 * Deux différences volontaires avec l'ancien comportement :
 *  - on ne relance pas aveuglément : si le bridge répond déjà, on le laisse tranquille. Il est
 *    partagé, et une seconde instance se battrait pour les ports 8218/8219 ;
 *  - on surveille : si la connexion ne revient pas, on retente un lancement. Sans cela, un
 *    bridge mort laisserait le deck inerte sans que rien ne le signale.
 */
import { spawn, execFile } from 'child_process';
import { existsSync } from 'fs';
import { createConnection } from 'net';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import * as log from './logger.js';

/**
 * Ferme l'application Stream Deck d'Elgato si elle tourne.
 *
 * Elle et TradeDeck ne peuvent pas coexister : elles se disputent le boîtier USB, et surtout
 * son plugin occupe l'UNIQUE place plugin du bridge, ce qui laisse TradeDeck « hors ligne »
 * sans explication.
 *
 * Désactiver son démarrage automatique ne suffit pas : Windows 11 relance à l'ouverture de
 * session les applications qui tournaient à l'extinction (« Redémarrer automatiquement les
 * applications »), ce qui la ramenait à chaque redémarrage. On la ferme donc à chaque démarrage
 * de l'hôte, avant de connecter le bridge.
 *
 * `DECKHOST_KeepElgato=1` désactive ce comportement — utile pour une mise à jour de firmware,
 * que seule l'application Elgato sait faire.
 */
export function neutraliserElgato(): Promise<void> {
  return new Promise((resolve) => {
    if (process.env.DECKHOST_KeepElgato === '1') {
      log.event('Supervisor', 'Application Elgato laissée en place (DECKHOST_KeepElgato=1)');
      resolve();
      return;
    }

    // /T ferme aussi les processus enfants : plugins node, QtWebEngine, crashpad.
    execFile('taskkill', ['/IM', 'StreamDeck.exe', '/F', '/T'], (err) => {
      // taskkill sort en erreur quand le processus n'existe pas : c'est le cas normal.
      // Sa sortie n'est pas reprise ici : elle est écrite dans la page de codes OEM et
      // arriverait illisible dans le fichier, qui est en UTF-8.
      if (err) log.debugEvent('Supervisor', 'Application Elgato absente — rien à fermer');
      else log.eventWarn('Supervisor', 'Application Elgato fermée : elle occupait la place plugin du bridge');
      // Laisse le temps au bridge de constater la déconnexion et de libérer la place.
      setTimeout(resolve, 1200);
    });
  });
}

const HERE = dirname(fileURLToPath(import.meta.url));

/** Emplacements connus, du plus « produit » au plus « héritage ». */
function candidatePaths(): string[] {
  const fromEnv = process.env.DECKHOST_BridgePath;
  return [
    ...(fromEnv ? [fromEnv] : []),
    // Cible : le bridge est livré à côté de l'hôte.
    join(HERE, '..', 'bridge', 'StreamDeckBridge.exe'),
    join(HERE, '..', '..', 'StreamDeckBridge', 'publish', 'StreamDeckBridge.exe'),
    // Héritage : là où le plugin Elgato l'installait.
    join(process.env.APPDATA ?? '', 'Elgato', 'StreamDeck', 'Plugins',
      'com.trader.ninjatrader.sdPlugin', 'bridge', 'StreamDeckBridge.exe'),
  ];
}

export function resolveBridgePath(): string | null {
  for (const p of candidatePaths()) {
    if (p && existsSync(p)) return p;
  }
  return null;
}

/**
 * Teste si le port du bridge accepte une connexion TCP.
 *
 * On n'ouvre surtout pas une WebSocket pour sonder : le bridge n'accepte qu'un seul client
 * plugin, et une sonde qui reste ouverte prendrait la place de l'hôte lui-même.
 */
export function isBridgeUp(port: number, timeoutMs = 700): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = createConnection({ host: '127.0.0.1', port });
    const done = (up: boolean) => {
      socket.destroy();
      resolve(up);
    };
    socket.setTimeout(timeoutMs);
    socket.once('connect', () => done(true));
    socket.once('timeout', () => done(false));
    socket.once('error', () => done(false));
  });
}

export class BridgeSupervisor {
  #port: number;
  #path: string | null;
  #timer: ReturnType<typeof setInterval> | null = null;
  #launching = false;

  constructor(port: number) {
    this.#port = port;
    this.#path = resolveBridgePath();
    if (!this.#path) {
      log.eventWarn('Supervisor', 'Exécutable du bridge introuvable — il devra être lancé à la main', {
        cherché: candidatePaths().join(' | '),
      });
    }
  }

  /** Lance le bridge s'il ne répond pas déjà, puis surveille périodiquement. */
  async start(): Promise<void> {
    await this.#ensure();
    // 15 s : assez réactif pour qu'un bridge mort ne passe pas une séance inaperçu, assez espacé
    // pour ne pas peser. La sonde est une simple ouverture TCP refermée aussitôt.
    this.#timer = setInterval(() => void this.#ensure(), 15000);
  }

  async #ensure(): Promise<void> {
    if (this.#launching || !this.#path) return;
    if (await isBridgeUp(this.#port)) return;

    this.#launching = true;
    try {
      log.event('Supervisor', 'Bridge injoignable — lancement', { path: this.#path, port: this.#port });
      const child = spawn(this.#path, [], { detached: true, stdio: 'ignore', windowsHide: true });
      child.unref();
      // Laisse au bridge le temps d'ouvrir ses ports avant que la sonde suivante ne conclue.
      await new Promise((r) => setTimeout(r, 2500));
      log.event('Supervisor', (await isBridgeUp(this.#port)) ? 'Bridge démarré' : 'Bridge toujours injoignable après lancement');
    } catch (err) {
      log.fail('Supervisor', err, 'Lancement du bridge impossible', { path: this.#path });
    } finally {
      this.#launching = false;
    }
  }

  stop(): void {
    if (this.#timer) clearInterval(this.#timer);
    this.#timer = null;
    // Le bridge n'est volontairement pas tué : il porte la macro de sécurité et son verrou, qui
    // doivent survivre au redémarrage de l'interface.
  }
}
