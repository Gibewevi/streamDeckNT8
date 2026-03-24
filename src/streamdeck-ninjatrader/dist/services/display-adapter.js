/**
 * Console-based adapter for development/testing.
 * Logs all display calls.
 */
export class ConsoleDisplayAdapter {
    setTitle(context, title) {
        // In dev mode, just log
    }
    setImage(context, imageDataUri) {
        // In dev mode, just log
    }
    showOk(context) {
        // SD SDK: streamDeck.actions.showOk(context)
    }
    showAlert(context) {
        // SD SDK: streamDeck.actions.showAlert(context)
    }
}
// Singleton adapter — replaced at plugin init with the real SDK adapter
let _adapter = new ConsoleDisplayAdapter();
export function setDisplayAdapter(adapter) {
    _adapter = adapter;
}
export function getDisplayAdapter() {
    return _adapter;
}
//# sourceMappingURL=display-adapter.js.map