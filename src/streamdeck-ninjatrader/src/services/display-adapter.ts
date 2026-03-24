/**
 * Abstraction for Stream Deck display operations.
 * Allows actions to update button visuals without direct SDK coupling.
 * 
 * In production, the concrete implementation wraps the SD SDK's
 * setTitle() and setImage() calls. For testing, a mock can be used.
 */
export interface DisplayAdapter {
  setTitle(context: string, title: string): void;
  setImage(context: string, imageDataUri: string): void;
  showOk(context: string): void;
  showAlert(context: string): void;
}

/**
 * Console-based adapter for development/testing.
 * Logs all display calls.
 */
export class ConsoleDisplayAdapter implements DisplayAdapter {
  setTitle(context: string, title: string): void {
    // In dev mode, just log
  }
  setImage(context: string, imageDataUri: string): void {
    // In dev mode, just log
  }
  showOk(context: string): void {
    // SD SDK: streamDeck.actions.showOk(context)
  }
  showAlert(context: string): void {
    // SD SDK: streamDeck.actions.showAlert(context)
  }
}

// Singleton adapter — replaced at plugin init with the real SDK adapter
let _adapter: DisplayAdapter = new ConsoleDisplayAdapter();

export function setDisplayAdapter(adapter: DisplayAdapter): void {
  _adapter = adapter;
}

export function getDisplayAdapter(): DisplayAdapter {
  return _adapter;
}
