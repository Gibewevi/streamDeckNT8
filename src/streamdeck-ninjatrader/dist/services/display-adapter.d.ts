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
export declare class ConsoleDisplayAdapter implements DisplayAdapter {
    setTitle(context: string, title: string): void;
    setImage(context: string, imageDataUri: string): void;
    showOk(context: string): void;
    showAlert(context: string): void;
}
export declare function setDisplayAdapter(adapter: DisplayAdapter): void;
export declare function getDisplayAdapter(): DisplayAdapter;
