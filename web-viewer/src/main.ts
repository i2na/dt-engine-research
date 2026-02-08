import { DTEngine } from "./core/DTEngine";
import { RightSidebar } from "./ui/RightSidebar";

/**
 * Main Entry Point
 * Initializes DT Engine and UI bindings
 */

async function main() {
    console.log("DT Engine Viewer v1.0.0");
    console.log("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

    // Initialize engine
    const canvas = document.getElementById("canvas") as HTMLCanvasElement;
    const engine = new DTEngine();

    try {
        await engine.initialize(canvas);

        // Initialize UI
        const rightSidebar = new RightSidebar("metadata-panel");
        const loadingEl = document.getElementById("loading")!;
        const statsEl = document.getElementById("stats")!;

        // Event: Element selected
        engine.onElementSelected = (metadata, latency) => {
            if (metadata) {
                rightSidebar.showMetadata(metadata, latency);
            } else {
                rightSidebar.clear();
            }
        };

        // Event: Stats update
        engine.onStatsUpdate = (stats) => {
            statsEl.innerHTML = `
        Backend: ${stats.backend.toUpperCase()} |
        Triangles: ${stats.triangles.toLocaleString()} |
        Draw Calls: ${stats.calls} |
        Cache: ${stats.cacheSize}
      `;
        };

        // Load model
        const glbUrl = "/models/sample.glb";
        const parquetUrl = "/models/sample.parquet";

        loadingEl.classList.remove("hidden");

        try {
            await engine.loadModel(glbUrl, parquetUrl);
            loadingEl.classList.add("hidden");
        } catch (error) {
            loadingEl.innerHTML = `
        <p style="color: #ff6b6b;">모델 로드 실패</p>
        <p style="font-size: 12px; margin-top: 10px;">
          ${glbUrl}와 ${parquetUrl} 파일을<br>
          <code>/web-viewer/public/models/</code> 폴더에 배치해주세요
        </p>
      `;
            console.error("Failed to load model:", error);
            return;
        }

        // Bind UI controls
        bindLeftSidebarControls(engine);
        bindCanvasInteraction(engine);

        // Animation loop
        function animate() {
            requestAnimationFrame(animate);
            engine.render();
        }
        animate();

        console.log("✓ Application ready");
    } catch (error) {
        console.error("Failed to initialize engine:", error);
        document.getElementById("loading")!.innerHTML = `
      <p style="color: #ff6b6b;">엔진 초기화 실패</p>
      <p style="font-size: 12px; margin-top: 10px;">${error}</p>
    `;
    }
}

function bindLeftSidebarControls(engine: DTEngine) {
    // Rendering mode
    const modeMaterial = document.getElementById("mode-material")!;
    const modeWireframe = document.getElementById("mode-wireframe")!;
    const modeXray = document.getElementById("mode-xray")!;

    modeMaterial.addEventListener("click", () => {
        engine.setRenderMode("material");
        setActiveButton([modeMaterial, modeWireframe, modeXray], modeMaterial);
    });

    modeWireframe.addEventListener("click", () => {
        engine.setRenderMode("wireframe");
        setActiveButton([modeMaterial, modeWireframe, modeXray], modeWireframe);
    });

    modeXray.addEventListener("click", () => {
        engine.setRenderMode("xray");
        setActiveButton([modeMaterial, modeWireframe, modeXray], modeXray);
    });

    // Lighting controls
    const ambientIntensity = document.getElementById("ambient-intensity") as HTMLInputElement;
    const directionalIntensity = document.getElementById(
        "directional-intensity"
    ) as HTMLInputElement;
    const shadowsEnabled = document.getElementById("shadows-enabled") as HTMLInputElement;

    ambientIntensity.addEventListener("input", (e) => {
        engine.setAmbientIntensity(parseFloat((e.target as HTMLInputElement).value));
    });

    directionalIntensity.addEventListener("input", (e) => {
        engine.setDirectionalIntensity(parseFloat((e.target as HTMLInputElement).value));
    });

    shadowsEnabled.addEventListener("change", (e) => {
        engine.setShadowsEnabled((e.target as HTMLInputElement).checked);
    });

    // View options
    const showGrid = document.getElementById("show-grid") as HTMLInputElement;
    const showAxes = document.getElementById("show-axes") as HTMLInputElement;

    showGrid.addEventListener("change", (e) => {
        engine.setGridVisible((e.target as HTMLInputElement).checked);
    });

    showAxes.addEventListener("change", (e) => {
        engine.setAxesVisible((e.target as HTMLInputElement).checked);
    });

    // Model controls
    document.getElementById("reset-camera")!.addEventListener("click", () => {
        engine.resetCamera();
    });

    document.getElementById("fit-all")!.addEventListener("click", () => {
        engine.fitToView();
    });
}

function bindCanvasInteraction(engine: DTEngine) {
    const canvas = document.getElementById("canvas") as HTMLCanvasElement;

    canvas.addEventListener("click", (event) => {
        engine.handleClick(event.clientX, event.clientY);
    });
}

function setActiveButton(buttons: HTMLElement[], active: HTMLElement) {
    buttons.forEach((btn) => btn.classList.remove("active"));
    active.classList.add("active");
}

// Start application
main();
