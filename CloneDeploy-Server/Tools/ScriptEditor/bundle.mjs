import { build } from 'esbuild';
import { copyFile, mkdir } from 'node:fs/promises';

const outdir = '../../UI/wwwroot/vendor/monaco';
await mkdir(outdir, { recursive: true });
await build({
    entryPoints: { editor: 'editor.js', 'editor.worker': 'node_modules/monaco-editor/esm/vs/editor/editor.worker.js' },
    outdir, bundle: true, format: 'esm', minify: true, target: 'es2022',
    loader: { '.ttf': 'file' }, assetNames: '[name]-[hash]', legalComments: 'linked'
});
await copyFile('node_modules/monaco-editor/LICENSE', `${outdir}/LICENSE.txt`);
await copyFile('node_modules/monaco-editor/ThirdPartyNotices.txt', `${outdir}/ThirdPartyNotices.txt`);
