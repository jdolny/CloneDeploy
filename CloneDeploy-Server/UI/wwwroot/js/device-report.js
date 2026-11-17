export function download(csv, filename = "devices-report.csv") {
    const url = URL.createObjectURL(new Blob(["\ufeff", csv], { type: "text/csv;charset=utf-8" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export async function downloadStream(stream, filename = "devices-report.csv") {
    const buffer = await stream.arrayBuffer();
    download(new TextDecoder().decode(buffer), filename);
}
