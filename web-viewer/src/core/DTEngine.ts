import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { CapabilityDetector, IRenderBackend } from "./RenderBackend";
import { CacheManager, ElementMetadata } from "./CacheSystem";
import { DTGLBLoader } from "../loaders/GLBLoader";
import { DTParquetLoader } from "../loaders/ParquetLoader";
import { GuidResolver } from "../utils/GuidResolver";

/**
 * Core Digital Twin Engine
 * Orchestrates rendering, data loading, and interaction
 */
export class DTEngine {
    private backend: IRenderBackend | null = null;
    private scene: THREE.Scene;
    private camera: THREE.PerspectiveCamera;
    private controls: OrbitControls | null = null;

    private cacheManager = new CacheManager();
    private glbLoader: DTGLBLoader;
    private parquetLoader = new DTParquetLoader();
    private guidResolver: GuidResolver | null = null;

    private modelGroup: THREE.Group | null = null;
    private selectedGuid: string | null = null;

    // Lighting
    private ambientLight: THREE.AmbientLight;
    private directionalLight: THREE.DirectionalLight;

    // Helpers
    private gridHelper: THREE.GridHelper | null = null;
    private axesHelper: THREE.AxesHelper | null = null;

    // Rendering mode
    private renderMode: "material" | "wireframe" | "xray" = "material";

    // Event handlers
    public onElementSelected?: (metadata: ElementMetadata | null, latency: number) => void;
    public onStatsUpdate?: (stats: any) => void;

    constructor() {
        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color(0x1a1a1a);

        this.camera = new THREE.PerspectiveCamera(
            60,
            window.innerWidth / window.innerHeight,
            0.1,
            10000
        );
        this.camera.position.set(50, 50, 50);
        this.camera.lookAt(0, 0, 0);

        // Lighting
        this.ambientLight = new THREE.AmbientLight(0xffffff, 0.6);
        this.scene.add(this.ambientLight);

        this.directionalLight = new THREE.DirectionalLight(0xffffff, 1.5);
        this.directionalLight.position.set(50, 100, 50);
        this.directionalLight.castShadow = true;
        this.directionalLight.shadow.mapSize.width = 2048;
        this.directionalLight.shadow.mapSize.height = 2048;
        this.directionalLight.shadow.camera.near = 0.5;
        this.directionalLight.shadow.camera.far = 500;
        this.directionalLight.shadow.camera.left = -100;
        this.directionalLight.shadow.camera.right = 100;
        this.directionalLight.shadow.camera.top = 100;
        this.directionalLight.shadow.camera.bottom = -100;
        this.scene.add(this.directionalLight);

        // Cache manager and loaders
        this.glbLoader = new DTGLBLoader(this.cacheManager);
    }

    async initialize(canvas: HTMLCanvasElement): Promise<void> {
        console.log("Initializing DT Engine...");

        // Initialize rendering backend
        this.backend = await CapabilityDetector.createBackend();
        await this.backend.initialize(canvas);

        // Initialize cache system
        await this.cacheManager.initialize();

        // Setup controls
        this.controls = new OrbitControls(this.camera, this.backend.renderer.domElement);
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.05;
        this.controls.minDistance = 5;
        this.controls.maxDistance = 500;

        // Setup helpers
        this.gridHelper = new THREE.GridHelper(100, 100, 0x444444, 0x222222);
        this.gridHelper.visible = false;
        this.scene.add(this.gridHelper);

        this.axesHelper = new THREE.AxesHelper(20);
        this.scene.add(this.axesHelper);

        // Handle window resize
        window.addEventListener("resize", this.onWindowResize.bind(this));

        console.log("✓ DT Engine initialized");
    }

    async loadModel(glbUrl: string, parquetUrl: string): Promise<void> {
        console.log("Loading model...");

        // Load GLB
        this.modelGroup = await this.glbLoader.load(glbUrl);
        this.scene.add(this.modelGroup);

        // Initialize GUID resolver
        const guidToMesh = new Map(
            this.glbLoader.getAllGuids().map((guid) => [guid, this.glbLoader.getMeshByGuid(guid)!])
        );
        const meshToGuid = new Map(
            Array.from(guidToMesh.entries()).map(([guid, mesh]) => [mesh, guid])
        );
        this.guidResolver = new GuidResolver(guidToMesh, meshToGuid);

        // Initialize Parquet loader
        try {
            await this.parquetLoader.initialize(parquetUrl);
            this.cacheManager.setParquetLoader(this.parquetLoader);
            console.log("✓ Parquet loader initialized");
        } catch (error) {
            console.error("Failed to initialize Parquet loader:", error);
            console.warn("Operating in GLB-only mode");
        }

        // Fit model to view
        this.fitToView();

        console.log("✓ Model loaded successfully");
    }

    render(): void {
        if (!this.backend) return;

        this.controls?.update();
        this.backend.render(this.scene, this.camera);

        // Update stats
        if (this.onStatsUpdate) {
            const stats = {
                backend: this.backend.type,
                triangles: this.backend.renderer.info.render.triangles,
                calls: this.backend.renderer.info.render.calls,
                cacheSize: this.cacheManager.getStats().l1Size,
            };
            this.onStatsUpdate(stats);
        }
    }

    async handleClick(x: number, y: number): Promise<void> {
        if (!this.guidResolver) return;

        const guid = this.guidResolver.pick(x, y, this.camera, this.scene);

        if (guid) {
            console.log("Clicked element:", guid);

            // Clear previous highlight
            if (this.selectedGuid) {
                this.guidResolver.clearHighlight(this.selectedGuid);
            }

            // Highlight new selection
            this.guidResolver.highlightByGuid(guid);
            this.selectedGuid = guid;

            // Lookup metadata via 4-tier cache
            const result = await this.cacheManager.lookup(guid);
            console.log(
                `Metadata retrieved from ${result.source} in ${result.latency.toFixed(1)}ms`
            );

            if (this.onElementSelected) {
                this.onElementSelected(result.data, result.latency);
            }
        } else {
            // Clear selection
            if (this.selectedGuid) {
                this.guidResolver.clearHighlight(this.selectedGuid);
                this.selectedGuid = null;
            }

            if (this.onElementSelected) {
                this.onElementSelected(null, 0);
            }
        }
    }

    // Rendering controls
    setRenderMode(mode: "material" | "wireframe" | "xray"): void {
        this.renderMode = mode;

        this.scene.traverse((node: any) => {
            if (node instanceof THREE.Mesh) {
                switch (mode) {
                    case "material":
                        node.material = (node as any)._originalMaterial || node.material;
                        (node.material as THREE.Material).wireframe = false;
                        (node.material as THREE.Material).transparent = false;
                        (node.material as THREE.Material).opacity = 1.0;
                        break;

                    case "wireframe":
                        (node.material as THREE.Material).wireframe = true;
                        break;

                    case "xray":
                        (node.material as THREE.Material).wireframe = false;
                        (node.material as THREE.Material).transparent = true;
                        (node.material as THREE.Material).opacity = 0.3;
                        break;
                }
                (node.material as THREE.Material).needsUpdate = true;
            }
        });
    }

    setAmbientIntensity(intensity: number): void {
        this.ambientLight.intensity = intensity;
    }

    setDirectionalIntensity(intensity: number): void {
        this.directionalLight.intensity = intensity;
    }

    setShadowsEnabled(enabled: boolean): void {
        this.backend!.renderer.shadowMap.enabled = enabled;
        this.directionalLight.castShadow = enabled;
    }

    setGridVisible(visible: boolean): void {
        if (this.gridHelper) {
            this.gridHelper.visible = visible;
        }
    }

    setAxesVisible(visible: boolean): void {
        if (this.axesHelper) {
            this.axesHelper.visible = visible;
        }
    }

    resetCamera(): void {
        this.camera.position.set(50, 50, 50);
        this.camera.lookAt(0, 0, 0);
        this.controls?.reset();
    }

    fitToView(): void {
        if (!this.modelGroup) return;

        const box = new THREE.Box3().setFromObject(this.modelGroup);
        const center = box.getCenter(new THREE.Vector3());
        const size = box.getSize(new THREE.Vector3());

        const maxDim = Math.max(size.x, size.y, size.z);
        const fov = this.camera.fov * (Math.PI / 180);
        let cameraZ = Math.abs(maxDim / 2 / Math.tan(fov / 2));
        cameraZ *= 1.5; // Add margin

        this.camera.position.set(center.x + cameraZ, center.y + cameraZ, center.z + cameraZ);
        this.camera.lookAt(center);

        if (this.controls) {
            this.controls.target.copy(center);
            this.controls.update();
        }
    }

    private onWindowResize(): void {
        const width = window.innerWidth;
        const height = window.innerHeight;

        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();

        this.backend?.setSize(width, height);
    }

    dispose(): void {
        this.backend?.dispose();
        this.parquetLoader?.dispose();
        this.controls?.dispose();
        window.removeEventListener("resize", this.onWindowResize.bind(this));
    }
}
