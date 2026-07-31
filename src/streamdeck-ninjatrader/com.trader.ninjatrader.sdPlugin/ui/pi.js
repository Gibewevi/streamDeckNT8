/**
 * Property Inspector bridge — official Stream Deck protocol (SDK v2).
 *
 * Stream Deck calls connectElgatoStreamDeckSocket() when it opens a Property Inspector and
 * hands over the action's current settings in inActionInfo. The previous implementation
 * listened for a "loadSettings" window message that nothing ever sent, so no Property
 * Inspector ever displayed the saved values — hardcoded defaults hid it.
 */
(function () {
  let websocket = null;
  let uuid = null;
  let settings = {};
  let registered = false;
  const listeners = [];

  function notify() {
    for (const listener of listeners) {
      try { listener(settings); } catch (err) { console.error('PI listener failed', err); }
    }
  }

  // Called by Stream Deck itself — the name and argument order are part of the protocol.
  window.connectElgatoStreamDeckSocket = function (inPort, inPropertyInspectorUUID, inRegisterEvent, inInfo, inActionInfo) {
    uuid = inPropertyInspectorUUID;

    try {
      settings = (JSON.parse(inActionInfo).payload || {}).settings || {};
    } catch (err) {
      settings = {};
    }

    websocket = new WebSocket('ws://127.0.0.1:' + inPort);

    websocket.onopen = function () {
      websocket.send(JSON.stringify({ event: inRegisterEvent, uuid: inPropertyInspectorUUID }));
      registered = true;
      notify();
    };

    websocket.onmessage = function (event) {
      let message;
      try { message = JSON.parse(event.data); } catch (err) { return; }

      if (message.event === 'didReceiveSettings') {
        settings = (message.payload || {}).settings || {};
        notify();
      }
    };
  };

  window.PI = {
    /** Runs the callback with the saved settings, now and on every later change. */
    onSettings: function (listener) {
      listeners.push(listener);
      if (registered) listener(settings);
    },

    /**
     * Merges the given fields into the saved settings. Merging matters: a Property
     * Inspector that only knows some fields must never blank out the others.
     */
    save: function (patch) {
      settings = Object.assign({}, settings, patch);
      if (websocket && websocket.readyState === WebSocket.OPEN) {
        websocket.send(JSON.stringify({ event: 'setSettings', context: uuid, payload: settings }));
      }
    },
  };
})();
