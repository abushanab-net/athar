// Profile photo cropping (T113 follow-up): the chosen image moves under a round window; drag or the arrow keys pan, the
// zoom slider or +/- zoom. crop() draws the window's square at 512 px and returns it as a stream for .NET, which keeps
// the server's own checks (image by its bytes, 2 MB).
const SIZE = 512;
let picked = null; // { url, img: HTMLImageElement }
let view = null;   // { stage, img, base, zoom, x, y } — x/y: image offset inside the stage, in stage pixels

export function attach(input, dotnet) {
  input.addEventListener("change", () => {
    const file = input.files?.[0];
    input.value = ""; // choosing the same file again still fires change
    if (!file || !file.type.startsWith("image/")) return;
    const img = new Image();
    img.onload = () => {
      cancel();
      picked = { url: img.src, img };
      dotnet.invokeMethodAsync("Picked").catch(() => {});
    };
    img.onerror = () => URL.revokeObjectURL(img.src);
    img.src = URL.createObjectURL(file);
  });
}

export function pick(input) {
  input.click();
}

/** Puts the picked image in the stage, covering it (the shorter side fills the window), centred. */
export function show(stage) {
  if (!picked) return;
  const size = stage.clientWidth;
  const img = picked.img;
  img.draggable = false;
  img.alt = "";
  img.style.cssText = "position:absolute;left:0;top:0;transform-origin:0 0;user-select:none;touch-action:none;max-width:none";
  stage.replaceChildren(img);
  const base = size / Math.min(img.naturalWidth, img.naturalHeight);
  view = { stage, img, base, zoom: 1, x: 0, y: 0 };
  centre();
  let drag = null;
  stage.onpointerdown = (e) => {
    drag = { x: e.clientX - view.x, y: e.clientY - view.y };
    stage.setPointerCapture(e.pointerId);
  };
  stage.onpointermove = (e) => drag && move(e.clientX - drag.x, e.clientY - drag.y);
  stage.onpointerup = stage.onpointercancel = () => (drag = null);
  stage.onkeydown = (e) => {
    const step = e.shiftKey ? 32 : 8;
    const keys = { ArrowLeft: [step, 0], ArrowRight: [-step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] };
    if (keys[e.key]) {
      e.preventDefault();
      move(view.x + keys[e.key][0], view.y + keys[e.key][1]);
    }
  };
}

/** zoom: 1 (cover) to 4, keeping the window's centre on the same point of the image. */
export function zoom(value) {
  if (!view) return;
  const size = view.stage.clientWidth;
  const before = scale();
  const cx = (size / 2 - view.x) / before, cy = (size / 2 - view.y) / before;
  view.zoom = Math.min(4, Math.max(1, value));
  const after = scale();
  move(size / 2 - cx * after, size / 2 - cy * after);
}

/** The window's square at 512 px, as JPEG (white behind transparent pixels). Blazor turns the Blob into the IJSStreamReference .NET asked for. */
export async function crop() {
  if (!view) return null;
  const size = view.stage.clientWidth;
  const s = scale();
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = SIZE;
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = "#fff";
  ctx.fillRect(0, 0, SIZE, SIZE);
  ctx.drawImage(view.img, -view.x / s, -view.y / s, size / s, size / s, 0, 0, SIZE, SIZE);
  const blob = await new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", 0.9));
  return blob;
}

export function cancel() {
  if (picked) URL.revokeObjectURL(picked.url);
  picked = null;
  view = null;
}

const scale = () => view.base * view.zoom;

function centre() {
  const size = view.stage.clientWidth, s = scale();
  move((size - view.img.naturalWidth * s) / 2, (size - view.img.naturalHeight * s) / 2);
}

/** Moves the image, never leaving a gap in the window. */
function move(x, y) {
  const size = view.stage.clientWidth, s = scale();
  const w = view.img.naturalWidth * s, h = view.img.naturalHeight * s;
  view.x = Math.min(0, Math.max(size - w, x));
  view.y = Math.min(0, Math.max(size - h, y));
  view.img.style.transform = `translate(${view.x}px, ${view.y}px) scale(${s})`;
}
