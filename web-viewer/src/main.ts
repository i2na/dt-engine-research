import { DTEngine } from "./core/DTEngine";
import { RightSidebar } from "./ui/RightSidebar";

async function main() {
    const canvas = document.getElementById("canvas") as HTMLCanvasElement;
    const engine = new DTEngine();

    try {
        await engine.initialize(canvas);

        const rightSidebar = new RightSidebar("metadata-panel");
        const loadingEl = document.getElementById("loading")!;
        const statsEl = document.getElementById("stats")!;

        engine.onElementSelected = (metadata, latency) => {
            if (metadata) {
                rightSidebar.showMetadata(metadata, latency);
            } else {
                rightSidebar.clear();
            }
        };

        let statsTick = 0;
        engine.onStatsUpdate = (stats) => {
            statsTick++;
            if (statsTick % 10 !== 0) return;
            statsEl.textContent =
                `${stats.backend.toUpperCase()} | ` +
                `Tris: ${stats.triangles.toLocaleString()} | ` +
                `Draws: ${stats.calls} | ` +
                `Cache: ${stats.cacheSize}`;
        };

        const glbUrl = new URL("/models/model.glb", window.location.href).href;
        const parquetUrl = new URL("/models/model.parquet", window.location.href).href;

        loadingEl.classList.remove("hidden");

        try {
            await engine.loadModel(glbUrl, parquetUrl);
            loadingEl.classList.add("hidden");
        } catch (error) {
            loadingEl.innerHTML = `
                <p style="color: #ff6b6b;">모델 로드 실패</p>
                <p style="font-size: 12px; margin-top: 10px; color: #8b949e;">
                    model.glb, model.parquet 파일을<br>
                    <code>/web-viewer/public/models/</code> 폴더에 배치해주세요
                </p>
            `;
            console.error("Failed to load model:", error);
            return;
        }

        bindSidebarControls(engine);
        bindCanvasInteraction(engine, canvas);
        initCollapsibleSections();

        (function animate() {
            requestAnimationFrame(animate);
            engine.render();
        })();
    } catch (error) {
        console.error("Failed to initialize engine:", error);
        document.getElementById("loading")!.innerHTML = `
            <p style="color: #ff6b6b;">엔진 초기화 실패</p>
            <p style="font-size: 12px; margin-top: 10px; color: #8b949e;">${error}</p>
        `;
    }
}

function bindSidebarControls(engine: DTEngine) {
    // --- Environment ---
    bindColorInput("bg-color", (hex) => engine.setBackgroundColor(hex));
    bindToggle("env-toggle", (v) => engine.setEnvironmentMapEnabled(v));
    bindSlider("env-intensity", "env-intensity-val", (v) => engine.setEnvironmentIntensity(v));
    bindSlider("env-blur", "env-blur-val", (v) => engine.setEnvironmentBlur(v));

    // --- Lighting ---
    bindSlider("exposure", "exposure-val", (v) => engine.setExposure(v));
    bindSlider("ambient-intensity", "ambient-intensity-val", (v) => engine.setAmbientIntensity(v));
    bindSlider("dir-intensity", "dir-intensity-val", (v) => engine.setDirectionalIntensity(v));

    const sunAzEl = document.getElementById("sun-azimuth") as HTMLInputElement;
    const sunElEl = document.getElementById("sun-elevation") as HTMLInputElement;
    const sunAzVal = document.getElementById("sun-azimuth-val")!;
    const sunElVal = document.getElementById("sun-elevation-val")!;

    const updateSun = () => {
        const az = parseFloat(sunAzEl.value);
        const el = parseFloat(sunElEl.value);
        sunAzVal.textContent = `${az}°`;
        sunElVal.textContent = `${el}°`;
        engine.setSunPosition(az, el);
    };
    sunAzEl.addEventListener("input", updateSun);
    sunElEl.addEventListener("input", updateSun);

    bindToggle("shadows-toggle", (v) => engine.setShadowsEnabled(v));

    // --- Effects ---
    bindToggle("ssao-toggle", (v) => engine.setSSAOEnabled(v));
    bindSlider("ssao-radius", "ssao-radius-val", (v) => engine.setSSAORadius(v), true);
    bindToggle("bloom-toggle", (v) => engine.setBloomEnabled(v));
    bindSlider("bloom-strength", "bloom-strength-val", (v) => engine.setBloomStrength(v));
    bindSlider("bloom-threshold", "bloom-threshold-val", (v) => engine.setBloomThreshold(v));
    bindToggle("outline-toggle", (v) => engine.setOutlineEnabled(v));
    bindToggle("model-outline-toggle", (v) => engine.setModelOutlineEnabled(v));

    // --- Camera ---
    bindSlider("fov", "fov-val", (v) => engine.setFOV(v), true, "°");

    const projPersp = document.getElementById("proj-perspective")!;
    const projOrtho = document.getElementById("proj-ortho")!;
    projPersp.addEventListener("click", () => {
        engine.setProjection("perspective");
        setActiveBtn([projPersp, projOrtho], projPersp);
    });
    projOrtho.addEventListener("click", () => {
        engine.setProjection("orthographic");
        setActiveBtn([projPersp, projOrtho], projOrtho);
    });

    // --- Display ---
    const modeMat = document.getElementById("mode-material")!;
    const modeWire = document.getElementById("mode-wireframe")!;
    const modeXray = document.getElementById("mode-xray")!;
    const modeAll = [modeMat, modeWire, modeXray];

    modeMat.addEventListener("click", () => { engine.setRenderMode("material"); setActiveBtn(modeAll, modeMat); });
    modeWire.addEventListener("click", () => { engine.setRenderMode("wireframe"); setActiveBtn(modeAll, modeWire); });
    modeXray.addEventListener("click", () => { engine.setRenderMode("xray"); setActiveBtn(modeAll, modeXray); });

    bindToggle("show-grid", (v) => engine.setGridVisible(v));
    bindToggle("show-axes", (v) => engine.setAxesVisible(v));

    // --- Quick Actions ---
    document.getElementById("reset-camera")!.addEventListener("click", () => engine.resetCamera());
    document.getElementById("fit-all")!.addEventListener("click", () => engine.fitToView());
}

function bindCanvasInteraction(engine: DTEngine, canvas: HTMLCanvasElement) {
    let pointerDownPos: { x: number; y: number } | null = null;

    canvas.addEventListener("pointerdown", (e) => {
        pointerDownPos = { x: e.clientX, y: e.clientY };
        canvas.setPointerCapture(e.pointerId);
    });

    const handlePointerEnd = (e: PointerEvent) => {
        if (!pointerDownPos) return;
        const dx = e.clientX - pointerDownPos.x;
        const dy = e.clientY - pointerDownPos.y;
        if (Math.abs(dx) < 4 && Math.abs(dy) < 4) {
            engine.handleClick(e.clientX, e.clientY);
        }
        pointerDownPos = null;
    };

    canvas.addEventListener("pointerup", handlePointerEnd);
    canvas.addEventListener("pointercancel", handlePointerEnd);
}

function initCollapsibleSections() {
    document.querySelectorAll<HTMLElement>(".section-header").forEach((header) => {
        header.addEventListener("click", () => {
            header.closest(".panel-section")!.classList.toggle("collapsed");
        });
    });
}

// --- Helpers ---

function bindSlider(
    id: string,
    valId: string,
    cb: (v: number) => void,
    integer = false,
    suffix = ""
) {
    const el = document.getElementById(id) as HTMLInputElement;
    const valEl = document.getElementById(valId)!;
    el.addEventListener("input", () => {
        const v = parseFloat(el.value);
        valEl.textContent = (integer ? v.toFixed(0) : v.toFixed(2)) + suffix;
        cb(v);
    });
}

function bindToggle(id: string, cb: (v: boolean) => void) {
    const el = document.getElementById(id) as HTMLInputElement;
    el.addEventListener("change", () => cb(el.checked));
}

function bindColorInput(id: string, cb: (hex: string) => void) {
    const el = document.getElementById(id) as HTMLInputElement;
    el.addEventListener("input", () => cb(el.value));
}

function setActiveBtn(all: HTMLElement[], active: HTMLElement) {
    all.forEach((b) => b.classList.remove("active"));
    active.classList.add("active");
}

main();
