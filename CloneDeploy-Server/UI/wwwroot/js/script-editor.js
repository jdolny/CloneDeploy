let editorModule;
function loadEditor() {
    if (!editorModule) {
        const stylesheet = document.createElement('link');
        stylesheet.rel = 'stylesheet';
        stylesheet.href = new URL('../vendor/monaco/editor.css', import.meta.url).href;
        const stylesReady = new Promise((resolve, reject) => {
            stylesheet.onload = resolve;
            stylesheet.onerror = () => reject(new Error(window.cloneDeployText("Js.scripteditor.EditorStylesheetCouldNotBeLoaded")));
        });
        document.head.append(stylesheet);
        editorModule = Promise.all([
            import('../vendor/monaco/editor.js'), stylesReady
        ]).then(([module]) => module.monaco);
    }
    return editorModule;
}

class ScriptEditor extends HTMLElement {
    static observedAttributes = ['language', 'readonly'];

    connectedCallback() {
        const generation = this.generation = (this.generation ?? 0) + 1;
        // Blazor inserts the element before its children.
        requestAnimationFrame(() => this.initialize(generation));
    }

    async initialize(generation) {
        try {
            const monaco = await loadEditor();
            if (!this.isConnected || this.generation !== generation) return;
            this.monaco = monaco;
            this.textarea = this.querySelector('textarea');
            this.host = this.querySelector('[data-editor-host]');
            this.themeScope = this.closest('[data-editor-theme]');
            this.editor = monaco.editor.create(this.host, {
                value: this.textarea.value,
                language: this.getAttribute('language'),
                readOnly: this.getAttribute('readonly') === 'true',
                theme: this.themeScope?.getAttribute('data-editor-theme') === 'dark' ? 'vs-dark' : 'vs',
                automaticLayout: true,
                minimap: { enabled: false },
                fontSize: 14,
                tabSize: 4,
                insertSpaces: true,
                scrollBeyondLastLine: false,
                ariaLabel: window.cloneDeployText("Js.scripteditor.ScriptContents"),
                fixedOverflowWidgets: true
            });
            this.changeSubscription = this.editor.onDidChangeModelContent(() => {
                this.textarea.value = this.editor.getValue();
                this.textarea.dispatchEvent(new Event('input', { bubbles: true }));
            });
            this.themeObserver = new MutationObserver(() => {
                monaco.editor.setTheme(this.themeScope.getAttribute('data-editor-theme') === 'dark' ? 'vs-dark' : 'vs');
            });
            if (this.themeScope) this.themeObserver.observe(this.themeScope, { attributes: true, attributeFilter: ['data-editor-theme'] });
            this.setAttribute('ready', '');
            this.editor.layout();
        } catch (error) {
            if (!this.isConnected || this.generation !== generation) return;
            this.release();
            this.querySelector('[data-editor-status]').textContent = window.cloneDeployText("Js.scripteditor.CodeEditorUnavailableYouCanStillEditTheScript");
            console.error(window.cloneDeployText("Js.scripteditor.UnableToInitializeScriptEditor"), error);
        }
    }

    attributeChangedCallback(name) {
        if (!this.editor) return;
        if (name === 'language') this.monaco.editor.setModelLanguage(this.editor.getModel(), this.getAttribute('language'));
        if (name === 'readonly') this.editor.updateOptions({ readOnly: this.getAttribute('readonly') === 'true' });
    }

    disconnectedCallback() {
        this.generation = (this.generation ?? 0) + 1;
        this.release();
    }

    release() {
        this.themeObserver?.disconnect();
        this.changeSubscription?.dispose();
        const model = this.editor?.getModel();
        this.editor?.dispose();
        model?.dispose();
        this.editor = null;
        this.removeAttribute('ready');
    }
}

if (!customElements.get('cd-script-editor')) customElements.define('cd-script-editor', ScriptEditor);
