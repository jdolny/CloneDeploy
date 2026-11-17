import * as monaco from 'monaco-editor/editor/editor.api.js';
import 'monaco-editor/features/register.all.js';
import 'monaco-editor/languages/definitions/powershell/register.js';
import 'monaco-editor/languages/definitions/bat/register.js';
import { registerPowerShellCompletions } from './powershell-completions.js';

registerPowerShellCompletions(monaco);

self.MonacoEnvironment = {
    getWorker: () => new Worker(new URL('./editor.worker.js', import.meta.url), { type: 'module' })
};
export { monaco };
