window.citusDownloads = window.citusDownloads || {
    saveTextFile: function (fileName, content, contentType) {
        const blob = new Blob([content], { type: contentType || "text/plain;charset=utf-8" });
        const objectUrl = URL.createObjectURL(blob);
        const anchor = document.createElement("a");

        anchor.href = objectUrl;
        anchor.download = fileName || "download.txt";
        anchor.style.display = "none";

        document.body.appendChild(anchor);
        anchor.click();
        document.body.removeChild(anchor);

        window.setTimeout(function () {
            URL.revokeObjectURL(objectUrl);
        }, 0);
    }
};
