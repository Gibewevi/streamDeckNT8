/**
 * Journalisation des transitions d'état.
 *
 * L'hôte reçoit l'état du bridge cinq fois par seconde. Le journaliser tel quel remplirait le
 * fichier de centaines de milliers de lignes identiques ; ne rien journaliser du tout — ce qui
 * était le cas — rend le comportement des macros indéchiffrable après coup : on voyait
 * « break-even posé » sans jamais voir la position qui l'avait déclenché.
 *
 * Ce module ne journalise donc **que les changements**, une ligne par transition réelle. C'est
 * ce qui permet de rejouer une séance de bout en bout à partir du seul fichier du jour.
 */
import { TradingState } from './messages.js';
import { Layout } from './layout.js';
import { actionName } from './catalog.js';
import * as log from './logger.js';

/** Champs surveillés, réduits à ce qui a un sens métier. */
export interface Empreinte {
  ntConnected: boolean;
  account: string;
  instrument: string;
  quantity: number;
  posExists: boolean;
  posDirection: string;
  posQuantity: number;
  posAvgPrice: number;
  hasStop: boolean;
  stopPrice: number;
  stopCount: number;
  hasTarget: boolean;
  targetPrice: number;
  activeOrders: number;
  safetyArmed: boolean;
  entriesBlocked: boolean;
  blockReason: string;
  cooldownEnabled: boolean;
  cooldownActive: boolean;
  tiltActive: boolean;
  tiltReason: string;
}

export function empreinteDe(s: TradingState): Empreinte {
  const p = s.position;
  return {
    ntConnected: s.ntConnected,
    account: s.account || '',
    instrument: s.instrument || '',
    quantity: s.quantity ?? 0,
    posExists: p?.exists === true,
    posDirection: p?.direction ?? 'Flat',
    posQuantity: p?.quantity ?? 0,
    posAvgPrice: p?.averagePrice ?? 0,
    hasStop: p?.hasStopOrder === true,
    stopPrice: p?.stopPrice ?? 0,
    stopCount: p?.stopOrderCount ?? 0,
    hasTarget: p?.hasTargetOrder === true,
    targetPrice: p?.targetPrice ?? 0,
    activeOrders: p?.activeOrderCount ?? 0,
    safetyArmed: s.safety?.armed === true,
    entriesBlocked: s.safety?.entriesBlocked === true,
    blockReason: s.safety?.blockReason ?? '',
    cooldownEnabled: s.cooldownEnabled === true,
    cooldownActive: s.cooldownActive === true,
    tiltActive: s.safety?.tiltActive === true,
    tiltReason: s.safety?.tiltReason ?? '',
  };
}

/** Les prix sont des flottants : une égalité stricte produirait des transitions fantômes. */
const memePrix = (a: number, b: number) => Math.abs(a - b) < 1e-6;

/**
 * Compare deux empreintes et journalise chaque changement significatif.
 * `avant` vaut null au tout premier état reçu : on décrit alors la situation de départ.
 */
export function journaliserTransitions(avant: Empreinte | null, apres: Empreinte): void {
  if (!avant) {
    log.event('State', 'État initial reçu du bridge', {
      ntConnected: apres.ntConnected, compte: apres.account, instrument: apres.instrument,
      quantite: apres.quantity,
      position: apres.posExists ? `${apres.posDirection} ${apres.posQuantity} @ ${apres.posAvgPrice}` : 'plat',
      stop: apres.hasStop ? apres.stopPrice : 'aucun',
      target: apres.hasTarget ? apres.targetPrice : 'aucun',
      securite: apres.safetyArmed ? 'armée' : 'désarmée',
      temporisation: apres.cooldownEnabled ? 'activée' : 'désactivée',
    });
    return;
  }

  // --- Contexte de travail ---
  if (avant.ntConnected !== apres.ntConnected) {
    const msg = apres.ntConnected ? 'NinjaTrader connecté' : 'NinjaTrader déconnecté';
    apres.ntConnected ? log.event('State', msg) : log.eventWarn('State', msg);
  }
  if (avant.account !== apres.account) {
    // Un compte non simulé mérite d'être signalé : c'est le réglage le plus lourd de conséquences.
    const reel = apres.account !== '' && !/^sim/i.test(apres.account);
    const ctx = { de: avant.account || '(aucun)', vers: apres.account || '(aucun)' };
    reel ? log.eventWarn('State', 'Compte changé — COMPTE NON SIMULÉ', ctx)
         : log.event('State', 'Compte changé', ctx);
  }
  if (avant.instrument !== apres.instrument) {
    log.event('State', 'Instrument changé', { de: avant.instrument || '(aucun)', vers: apres.instrument || '(aucun)' });
  }
  if (avant.quantity !== apres.quantity) {
    log.event('State', 'Quantité de travail changée', { de: avant.quantity, vers: apres.quantity });
  }

  // --- Position ---
  if (!avant.posExists && apres.posExists) {
    log.event('Position', 'Position OUVERTE', {
      direction: apres.posDirection, quantite: apres.posQuantity, prixMoyen: apres.posAvgPrice,
    });
  } else if (avant.posExists && !apres.posExists) {
    log.event('Position', 'Position FERMÉE', {
      direction: avant.posDirection, quantite: avant.posQuantity, prixMoyen: avant.posAvgPrice,
    });
  } else if (apres.posExists) {
    const tailleChangee = avant.posQuantity !== apres.posQuantity;
    const prixChange = !memePrix(avant.posAvgPrice, apres.posAvgPrice);
    if (tailleChangee || prixChange) {
      // C'est l'événement dont dépend le réarmement d'Auto BE : il doit être explicite.
      const sens = Math.abs(apres.posQuantity) > Math.abs(avant.posQuantity) ? 'RENFORT' : 'RÉDUCTION';
      log.event('Position', `Position modifiée — ${tailleChangee ? sens : 'prix moyen recalculé'}`, {
        quantite: `${avant.posQuantity} → ${apres.posQuantity}`,
        prixMoyen: `${avant.posAvgPrice} → ${apres.posAvgPrice}`,
        direction: apres.posDirection,
      });
    }
    if (avant.posDirection !== apres.posDirection) {
      log.event('Position', 'Sens de position inversé', { de: avant.posDirection, vers: apres.posDirection });
    }
  }

  // --- Ordres de protection ---
  if (avant.hasStop !== apres.hasStop) {
    if (apres.hasStop) {
      log.event('Protection', 'Stop en place', { prix: apres.stopPrice, nombre: apres.stopCount });
    } else if (apres.posExists) {
      // Le stop ne disparaît anormalement que si la position, elle, est toujours là. À la
      // fermeture, sa disparition est normale : avertir à chaque sortie apprendrait à ignorer
      // l'avertissement le jour où il compte.
      log.eventWarn('Protection', 'Stop DISPARU alors que la position est ouverte', { dernierPrix: avant.stopPrice });
    } else {
      log.debugEvent('Protection', 'Stop retiré avec la position', { dernierPrix: avant.stopPrice });
    }
  } else if (apres.hasStop && !memePrix(avant.stopPrice, apres.stopPrice)) {
    log.event('Protection', 'Stop déplacé', { de: avant.stopPrice, vers: apres.stopPrice });
  }
  if (avant.hasTarget !== apres.hasTarget) {
    log.event('Protection', apres.hasTarget ? 'Target en place' : 'Target retiré', {
      prix: apres.hasTarget ? apres.targetPrice : avant.targetPrice,
    });
  } else if (apres.hasTarget && !memePrix(avant.targetPrice, apres.targetPrice)) {
    log.event('Protection', 'Target déplacé', { de: avant.targetPrice, vers: apres.targetPrice });
  }
  if (avant.activeOrders !== apres.activeOrders) {
    log.debugEvent('Protection', 'Nombre d\'ordres actifs changé', { de: avant.activeOrders, vers: apres.activeOrders });
  }

  // --- Macro de sécurité ---
  if (avant.safetyArmed !== apres.safetyArmed) {
    log.event('Safety', apres.safetyArmed ? 'Macro ARMÉE' : 'Macro DÉSARMÉE');
  }
  if (avant.entriesBlocked !== apres.entriesBlocked) {
    apres.entriesBlocked
      ? log.eventWarn('Safety', 'ENTRÉES BLOQUÉES par la macro de sécurité', { motif: apres.blockReason })
      : log.event('Safety', 'Entrées de nouveau autorisées', { motifPrecedent: avant.blockReason });
  }

  // --- Anti-Tilt ---
  // Une entrée sous friction reste possible : le message dit « ralenties » et non « bloquées »,
  // parce que confondre les deux dans le journal rendrait un incident illisible après coup.
  if (avant.tiltActive !== apres.tiltActive) {
    apres.tiltActive
      ? log.eventWarn('Safety', 'ANTI-TILT déclenché — entrées sous appui long', { motif: apres.tiltReason })
      : log.event('Safety', 'Anti-Tilt terminé — entrées instantanées à nouveau', { motifPrecedent: avant.tiltReason });
  } else if (apres.tiltActive && avant.tiltReason !== apres.tiltReason) {
    log.eventWarn('Safety', 'Anti-Tilt : motif changé', { de: avant.tiltReason, vers: apres.tiltReason });
  }

  // --- Temporisation ---
  if (avant.cooldownEnabled !== apres.cooldownEnabled) {
    log.event('Cooldown', apres.cooldownEnabled ? 'Temporisation activée' : 'Temporisation désactivée');
  }
  if (avant.cooldownActive !== apres.cooldownActive) {
    apres.cooldownActive
      ? log.eventWarn('Cooldown', 'Temporisation DÉCLENCHÉE — entrées suspendues')
      : log.event('Cooldown', 'Temporisation terminée');
  }
}

/**
 * Décrit la configuration du deck au démarrage.
 *
 * Rend le fichier du jour autonome : en le relisant plus tard, on sait quelles macros étaient
 * posées et avec quels réglages, sans avoir à retrouver le `layout.json` de l'époque.
 */
export function journaliserConfiguration(layout: Layout): void {
  log.event('Session', 'Configuration du deck', {
    pages: layout.pages.length,
    grille: `${layout.device.columns}x${layout.device.rows}`,
    luminosite: layout.brightness,
  });

  layout.pages.forEach((page, i) => {
    const touches = Object.entries(page.slots);
    if (touches.length === 0) return;
    for (const [slot, a] of touches) {
      const reglages = Object.entries(a.settings ?? {})
        .filter(([, v]) => v !== '' && v !== undefined && v !== null)
        .map(([k, v]) => `${k}=${v}`)
        .join(' ');
      log.event('Session', `Page ${i + 1} · touche ${Number(slot) + 1} · ${actionName(a.actionId)}`, {
        reglages: reglages || '(aucun)',
      });
    }
  });
}
