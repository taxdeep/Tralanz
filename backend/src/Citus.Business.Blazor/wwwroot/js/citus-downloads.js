window.citusDownloads = window.citusDownloads || {
    saveTextFile: function (fileName, content, contentType) {
        const blob = new Blob([content], { type: contentType || "text/plain;charset=utf-8" });
        triggerDownload(blob, fileName || "download.txt");
    },
    saveBinaryFile: function (fileName, base64Content, contentType) {
        // Decode base64 -> Uint8Array. Used for PDFs and other binaries
        // shipped from the .NET side via Convert.ToBase64String. Faster
        // than JSInterop byte[] transport for files in the few-MB range.
        const binary = atob(base64Content);
        const len = binary.length;
        const bytes = new Uint8Array(len);
        for (let i = 0; i < len; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });
        triggerDownload(blob, fileName || "download.bin");
    }
};

function triggerDownload(blob, fileName) {
    const objectUrl = URL.createObjectURL(blob);
    const anchor = document.createElement("a");

    anchor.href = objectUrl;
    anchor.download = fileName;
    anchor.style.display = "none";

    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);

    window.setTimeout(function () {
        URL.revokeObjectURL(objectUrl);
    }, 0);
}
