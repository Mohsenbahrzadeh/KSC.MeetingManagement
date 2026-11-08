export function downloadText(name, contentType, text) {
    const url = URL.createObjectURL(new Blob([text], { type: contentType }));
    try {
        const link = document.createElement('a');
        link.href = url;
        link.download = name;
        document.body.appendChild(link);
        link.click();
        link.remove();
    } finally {
        URL.revokeObjectURL(url);
    }
}
