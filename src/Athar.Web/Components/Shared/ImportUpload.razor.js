// T164: the import upload goes over plain HTTP (not the Blazor circuit), so files up to 4 GB stream to the server with progress.
export function pick(input) {
  input.click();
}

export function upload(input, url, token, dotnet) {
  return new Promise((resolve, reject) => {
    const file = input.files[0];
    if (!file) return reject(new Error("No file chosen."));
    const xhr = new XMLHttpRequest();
    xhr.open("POST", `${url}&name=${encodeURIComponent(file.name)}`);
    xhr.setRequestHeader("RequestVerificationToken", token);
    xhr.setRequestHeader("Content-Type", "application/octet-stream");
    let last = -1;
    xhr.upload.onprogress = (e) => {
      if (!e.lengthComputable) return;
      const percent = Math.floor((e.loaded * 100) / e.total);
      if (percent !== last) {
        last = percent;
        dotnet.invokeMethodAsync("Progress", percent).catch(() => {}); // the dialog may have closed
      }
    };
    xhr.onload = () => {
      input.value = "";
      if (xhr.status >= 200 && xhr.status < 300) return resolve(JSON.parse(xhr.responseText).job_id);
      let message = xhr.statusText || `HTTP ${xhr.status}`;
      try { message = JSON.parse(xhr.responseText).detail || message; } catch { /* not JSON */ }
      reject(new Error(message));
    };
    xhr.onerror = () => reject(new Error("The upload was interrupted."));
    xhr.send(file);
  });
}

export function size(input) {
  return input.files[0]?.size ?? 0;
}
