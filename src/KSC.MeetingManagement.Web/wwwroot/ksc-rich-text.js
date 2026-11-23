const editors = new Map();

export function attach(id, html, dotnet) {
    const editor = document.getElementById(id);
    if (!editor) return;

    editor.innerHTML = html || "<p><br></p>";
    const notify = () => {
        const value = editor.innerHTML;
        const length = (editor.innerText || "").replace(/\u00a0/g, " ").trim().length;
        dotnet.invokeMethodAsync("ContentChanged", value, length);
    };
    const onInput = () => notify();
    const onPaste = event => {
        event.preventDefault();
        const text = event.clipboardData?.getData("text/plain");
        if (text) document.execCommand("insertText", false, text);
    };
    const onKeyDown = event => {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "b") {
            event.preventDefault(); document.execCommand("bold"); notify();
        } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "i") {
            event.preventDefault(); document.execCommand("italic"); notify();
        } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "u") {
            event.preventDefault(); document.execCommand("underline"); notify();
        }
    };

    editor.addEventListener("input", onInput);
    editor.addEventListener("paste", onPaste);
    editor.addEventListener("keydown", onKeyDown);
    editors.set(id, { editor, dotnet, onInput, onPaste, onKeyDown });
}

export function format(id, command, value) {
    const instance = editors.get(id);
    if (!instance) return;
    instance.editor.focus();
    document.execCommand(command, false, value || null);
    instance.dotnet.invokeMethodAsync("ContentChanged", instance.editor.innerHTML,
        (instance.editor.innerText || "").replace(/\u00a0/g, " ").trim().length);
}

export function setContent(id, html) {
    const instance = editors.get(id);
    if (!instance || document.activeElement === instance.editor) return;
    instance.editor.innerHTML = html || "<p><br></p>";
}

export function focus(id) {
    editors.get(id)?.editor.focus();
}

export function detach(id) {
    const instance = editors.get(id);
    if (!instance) return;
    instance.editor.removeEventListener("input", instance.onInput);
    instance.editor.removeEventListener("paste", instance.onPaste);
    instance.editor.removeEventListener("keydown", instance.onKeyDown);
    editors.delete(id);
}
